using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in SchemaStatements)
            await ExecuteAsync(statement, transaction, cancellationToken);

        foreach (var operation in plan.Operations)
        {
            switch (operation)
            {
                case CreateStorageUnitOperation storageUnit:
                    await AdmitIdentityPolicyAsync(storageUnit.StorageUnit, transaction, cancellationToken);
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

    private async Task AdmitIdentityPolicyAsync(
        MaterializedStorageUnit storageUnit,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var policy = storageUnit.StringIdentityCasePolicy.ToString();
        string? retainedPolicy = null;
        string? retainedComparisonAlgorithm = null;
        string? retainedLookupAlgorithm = null;
        await using (var command = CreateCommand(
                         $"""
                         SELECT string_case_policy, comparison_algorithm, lookup_algorithm
                         FROM groundwork_document_identity_schema
                         WHERE {ExactEqualityPredicate("document_kind", $"{ParameterPrefix}kind")};
                         """,
                         transaction))
        {
            AddParameter(command, "kind", storageUnit.Identity);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                retainedPolicy = reader.GetString(0);
                retainedComparisonAlgorithm = reader.GetString(1);
                retainedLookupAlgorithm = reader.GetString(2);
            }
        }

        if (retainedPolicy is null)
        {
            await using var command = CreateCommand(
                $"""
                INSERT INTO groundwork_document_identity_schema
                (document_kind, string_case_policy, comparison_algorithm, lookup_algorithm)
                VALUES ({ParameterPrefix}kind, {ParameterPrefix}policy, {ParameterPrefix}comparisonAlgorithm, {ParameterPrefix}lookupAlgorithm);
                """,
                transaction);
            AddParameter(command, "kind", storageUnit.Identity);
            AddParameter(command, "policy", policy);
            AddParameter(command, "comparisonAlgorithm", storageUnit.ComparisonAlgorithmId);
            AddParameter(command, "lookupAlgorithm", storageUnit.LookupAlgorithmId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (string.Equals(retainedPolicy, policy, StringComparison.Ordinal) &&
            string.Equals(retainedComparisonAlgorithm, storageUnit.ComparisonAlgorithmId, StringComparison.Ordinal) &&
            string.Equals(retainedLookupAlgorithm, storageUnit.LookupAlgorithmId, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Conventional document kind '{storageUnit.Identity}' identity policy or algorithm state does not match the materialization target. Drop and recreate the schema; automatic re-keying is not supported.");
    }

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
