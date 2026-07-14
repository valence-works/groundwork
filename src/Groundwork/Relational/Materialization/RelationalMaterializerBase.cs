using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Materialization;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Materialization;

public abstract class RelationalMaterializerBase(DbConnection connection)
{
    protected virtual string ParameterPrefix => "@";

    protected abstract IReadOnlyList<string> SchemaStatements { get; }

    protected abstract string InsertSchemaHistorySql { get; }

    protected virtual string ExactEqualityPredicate(string columnExpression, string parameterReference) =>
        $"{columnExpression} = {parameterReference}";

    protected abstract IReadOnlyList<string> CreateOptimizedProjectionStatements(MaterializedProjection projection);

    public async Task MaterializeAsync(MaterializationPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.IsPlannable)
            throw new InvalidOperationException("Cannot execute an unplannable materialization plan.");

        await EnsureOpenAsync(cancellationToken);
        await using var transaction = await BeginMaterializationTransactionAsync(cancellationToken);

        var admissions = plan.Operations
            .OfType<CreateStorageUnitOperation>()
            .Select(operation => new IdentitySchemaAdmission(
                new StorageUnitIdentity(operation.StorageUnit.Identity),
                operation.StorageUnit.IdentitySchemaState))
            .ToArray();
        if (admissions.Length > 0)
            await AcquireIdentitySchemaLockAsync(transaction, cancellationToken);

        foreach (var statement in SchemaStatements)
            await ExecuteAsync(statement, transaction, cancellationToken);

        await AdmitIdentitySchemasAsync(admissions, transaction, cancellationToken);

        foreach (var operation in plan.Operations)
        {
            switch (operation)
            {
                case CreateStorageUnitOperation:
                    break;
                case CreateIndexOperation:
                    break;
                case BackfillCanonicalJsonOperation backfill:
                    await BackfillIndexAsync(ToLegacyMaterializedIndex(backfill), transaction, cancellationToken);
                    break;
                case CreateOptimizedProjectionOperation projection:
                    await MaterializeOptimizedProjectionAsync(projection.Projection, transaction, cancellationToken);
                    break;
                case RecordSchemaHistoryOperation history:
                    await RecordSchemaHistoryAsync(history.Entry, transaction, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported materialization operation '{operation.Kind}'.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    protected virtual async ValueTask<DbTransaction> BeginMaterializationTransactionAsync(
        CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(cancellationToken);

    protected abstract Task AcquireIdentitySchemaLockAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    protected abstract Task<IReadOnlySet<string>> ReadDocumentColumnsAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    protected abstract Task<IdentityLookupIndexShape> ReadIdentityLookupIndexShapeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    protected abstract Task AddIdentityColumnsAsync(
        IReadOnlySet<string> existingColumns,
        DbTransaction transaction,
        CancellationToken cancellationToken);

    protected abstract Task EnsureIdentityLookupIndexAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken);

    protected virtual Task FinalizeIdentityColumnsAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract IReadOnlyList<string> RequiredIdentityLookupIndexColumns { get; }

    private async Task AdmitIdentitySchemasAsync(
        IReadOnlyList<IdentitySchemaAdmission> admissions,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (admissions.Count == 0)
            return;

        var recordedUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var admission in admissions)
        {
            var retainedState = await ReadIdentitySchemaStateAsync(admission.StorageUnit, transaction, cancellationToken);
            if (retainedState is null)
                continue;
            if (retainedState != admission.RequiredState)
            {
                throw new InvalidOperationException(
                    $"Document Store Storage Unit '{admission.StorageUnit.Value}' identity schema state does not match the materialization target. Forward re-keying requires an explicit schema evolution.");
            }

            recordedUnits.Add(admission.StorageUnit.Value);
        }

        var columns = await ReadDocumentColumnsAsync(transaction, cancellationToken);
        if (!columns.Contains("id"))
        {
            throw new InvalidOperationException(
                "The Document Store document table has no original identity column 'id'; identity schema evolution cannot recover the authoritative identity.");
        }

        await AddIdentityColumnsAsync(columns, transaction, cancellationToken);
        await BackfillOrValidateIdentityRowsAsync(admissions, recordedUnits, transaction, cancellationToken);
        await FinalizeIdentityColumnsAsync(transaction, cancellationToken);
        await EnsureIdentityLookupIndexAsync(transaction, cancellationToken);
        var indexShape = await ReadIdentityLookupIndexShapeAsync(transaction, cancellationToken);
        if (!indexShape.IsUnique || !indexShape.Columns.SequenceEqual(RequiredIdentityLookupIndexColumns, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Document Store identity lookup index does not have the required unique key shape.");
        }

        foreach (var admission in admissions.Where(x => !recordedUnits.Contains(x.StorageUnit.Value)))
            await RecordIdentitySchemaStateAsync(admission, transaction, cancellationToken);
    }

    private async Task<DocumentStoreIdentitySchemaState?> ReadIdentitySchemaStateAsync(
        StorageUnitIdentity storageUnit,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT state_json
            FROM groundwork_document_identity_schema
            WHERE {ExactEqualityPredicate("storage_unit", $"{ParameterPrefix}storageUnit")};
            """,
            transaction);
        AddParameter(command, "storageUnit", storageUnit.Value);
        var retainedJson = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return retainedJson is null
            ? null
            : DocumentStoreIdentitySchemaState.FromCanonicalJson(retainedJson);
    }

    private async Task BackfillOrValidateIdentityRowsAsync(
        IReadOnlyList<IdentitySchemaAdmission> admissions,
        IReadOnlySet<string> recordedUnits,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var admissionByUnit = admissions.ToDictionary(x => x.StorageUnit.Value, StringComparer.Ordinal);
        var rows = new List<RetainedIdentityRow>();
        await using (var command = CreateCommand(
                         """
                         SELECT document_kind, storage_scope, id, id_comparison_key, id_lookup_key
                         FROM groundwork_documents;
                         """,
                         transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var unit = reader.GetString(0);
                if (!admissionByUnit.TryGetValue(unit, out var admission))
                {
                    throw new InvalidOperationException(
                        $"Document Store Storage Unit '{unit}' has retained rows but is not declared by the materialization target.");
                }

                var originalId = reader.GetString(2);
                var policy = PortableStringComparison.ForIdentityPolicy(admission.RequiredState.StringCasePolicy);
                var projection = PortableStringComparison.ProjectIdentity(originalId, policy);
                rows.Add(new(
                    unit,
                    reader.GetString(1),
                    originalId,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    recordedUnits.Contains(unit),
                    projection));
            }
        }

        var duplicate = rows
            .GroupBy(row => (row.StorageUnit, row.StorageScope, row.Required.LookupKey))
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Document Store Storage Unit '{duplicate.Key.StorageUnit}' contains identities that collide under the required identity schema; no schema state was recorded.");
        }

        foreach (var row in rows)
        {
            if (row.HasRecordedState)
            {
                if (!string.Equals(row.RetainedComparisonKey, row.Required.ComparisonKey, StringComparison.Ordinal) ||
                    !string.Equals(row.RetainedLookupKey, row.Required.LookupKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Document Store Storage Unit '{row.StorageUnit}' contains an identity projection that does not match its recorded original identity.");
                }

                continue;
            }

            if (string.Equals(row.RetainedComparisonKey, row.Required.ComparisonKey, StringComparison.Ordinal) &&
                string.Equals(row.RetainedLookupKey, row.Required.LookupKey, StringComparison.Ordinal))
            {
                continue;
            }

            await using var update = CreateCommand(
                $"""
                UPDATE groundwork_documents
                SET id_comparison_key = {ParameterPrefix}comparisonKey,
                    id_lookup_key = {ParameterPrefix}lookupKey
                WHERE {ExactEqualityPredicate("document_kind", $"{ParameterPrefix}storageUnit")}
                  AND {ExactEqualityPredicate("storage_scope", $"{ParameterPrefix}scope")}
                  AND {ExactEqualityPredicate("id", $"{ParameterPrefix}id")};
                """,
                transaction);
            AddParameter(update, "comparisonKey", row.Required.ComparisonKey);
            AddParameter(update, "lookupKey", row.Required.LookupKey);
            AddParameter(update, "storageUnit", row.StorageUnit);
            AddParameter(update, "scope", row.StorageScope);
            AddParameter(update, "id", row.OriginalId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Document Store identity schema evolution lost its authoritative row lock.");
        }
    }

    private async Task RecordIdentitySchemaStateAsync(
        IdentitySchemaAdmission admission,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            INSERT INTO groundwork_document_identity_schema
            (storage_unit, state_json)
            VALUES ({ParameterPrefix}storageUnit, {ParameterPrefix}stateJson);
            """,
            transaction);
        AddParameter(command, "storageUnit", admission.StorageUnit.Value);
        AddParameter(command, "stateJson", admission.RequiredState.ToCanonicalJson());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected sealed record IdentityLookupIndexShape(bool IsUnique, IReadOnlyList<string> Columns);

    private sealed record RetainedIdentityRow(
        string StorageUnit,
        string StorageScope,
        string OriginalId,
        string? RetainedComparisonKey,
        string? RetainedLookupKey,
        bool HasRecordedState,
        PortableStringIdentityProjection Required);

    private sealed record IdentitySchemaAdmission(
        StorageUnitIdentity StorageUnit,
        DocumentStoreIdentitySchemaState RequiredState);

    private static MaterializedIndex ToLegacyMaterializedIndex(BackfillCanonicalJsonOperation backfill)
    {
        if (backfill.SubjectKind != CanonicalJsonBackfillSubjectKind.LogicalIndex ||
            backfill.LogicalIndex is null ||
            backfill.StorageUnit is null)
        {
            throw new InvalidOperationException(
                $"Legacy relational materialization cannot execute physical backfill '{backfill.Identity}'.");
        }

        var index = backfill.LogicalIndex;
        return new MaterializedIndex(
            backfill.StorageUnit.Value,
            index.Identity,
            index.Fields.Select(field => field.Path).ToArray(),
            index.ValueKind,
            index.IsUnique,
            index.IsSortable,
            index.MissingValueBehavior);
    }

    private async Task RecordSchemaHistoryAsync(
        SchemaHistoryEntry history,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(InsertSchemaHistorySql, transaction);
        AddParameter(command, "manifestId", history.ManifestIdentity.Value);
        AddParameter(command, "manifestVersion", history.ManifestVersion.Value);
        AddParameter(command, "providerName", history.Provider.Name);
        AddParameter(command, "providerVersion", history.Provider.Version);
        AddParameter(command, "appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected virtual async Task MaterializeOptimizedProjectionAsync(
        MaterializedProjection projection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in CreateOptimizedProjectionStatements(projection))
            await ExecuteAsync(statement, transaction, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the portable index projection (<c>groundwork_document_indexes</c>) for a materialized index
    /// from the documents that already exist in the unit. Index projection rows are otherwise only written at
    /// save time, so without this an index added to a unit with pre-existing documents would leave those
    /// documents invisible to the new index until each one is next saved. Runs inside the materialization
    /// transaction, so the backfill commits atomically with the rest of the plan.
    /// </summary>
    private async Task BackfillIndexAsync(
        MaterializedIndex index,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Only single-field indexes are projected into groundwork_document_indexes (mirrors the save-time
        // maintenance path); composite indexes never produce rows, so there is nothing to backfill.
        if (index.FieldPaths.Count != 1)
            return;

        await DeleteIndexRowsAsync(index.UnitIdentity, index.Identity, transaction, cancellationToken);

        var documents = await LoadDocumentsForIndexAsync(index.UnitIdentity, transaction, cancellationToken);
        foreach (var (storageScope, documentId, contentJson) in documents)
        {
            using var document = JsonDocument.Parse(contentJson);
            if (!RelationalIndexValues.TryGetIndexValue(document.RootElement, index.FieldPaths, out var value))
                continue;

            await InsertIndexRowAsync(index, storageScope, documentId, value, transaction, cancellationToken);
        }
    }

    private async Task DeleteIndexRowsAsync(
        string documentKind,
        string indexName,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            DELETE FROM groundwork_document_indexes
            WHERE {ExactEqualityPredicate("document_kind", "@kind")}
              AND {ExactEqualityPredicate("index_name", "@index")};
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "index", indexName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<(string StorageScope, string DocumentId, string ContentJson)>> LoadDocumentsForIndexAsync(
        string documentKind,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            SELECT storage_scope, id, content_json
            FROM groundwork_documents
            WHERE {ExactEqualityPredicate("document_kind", "@kind")};
            """,
            transaction);
        AddParameter(command, "kind", documentKind);

        var documents = new List<(string StorageScope, string DocumentId, string ContentJson)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            documents.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return documents;
    }

    private async Task InsertIndexRowAsync(
        MaterializedIndex index,
        string storageScope,
        string documentId,
        string value,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        // is_unique is derived from the index declaration (not document data) and is constant for every row of
        // this index, so it is inlined as an integer literal. This keeps the statement provider-agnostic across
        // the INTEGER (SQLite/PostgreSQL) and BIT (SQL Server) column types without needing a boolean binding.
        var isUnique = index.IsUnique ? 1 : 0;
        await using var command = CreateCommand(
            $"""
            INSERT INTO groundwork_document_indexes
            (document_kind, storage_scope, index_name, index_value, document_id, is_unique)
            VALUES (@kind, @scope, @index, @value, @documentId, {isUnique});
            """,
            transaction);
        AddParameter(command, "kind", index.UnitIdentity);
        AddParameter(command, "scope", storageScope);
        AddParameter(command, "index", index.Identity);
        AddParameter(command, "value", value);
        AddParameter(command, "documentId", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected async Task ExecuteAsync(string sql, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected DbCommand CreateCommand(string commandText, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    protected void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"{ParameterPrefix}{name}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
    }
}
