using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Materialization;

public abstract class RelationalMaterializerBase(DbConnection connection)
{
    protected virtual string ParameterPrefix => "@";

    protected abstract IReadOnlyList<string> SchemaStatements { get; }

    protected abstract string InsertSchemaHistorySql { get; }

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
                case CreateStorageUnitOperation:
                    break;
                case CreateIndexOperation index:
                    await BackfillIndexAsync(index.Index, transaction, cancellationToken);
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
        foreach (var (documentId, contentJson) in documents)
        {
            using var document = JsonDocument.Parse(contentJson);
            if (!RelationalIndexValues.TryGetIndexValue(document.RootElement, index.FieldPaths, out var value))
                continue;

            await InsertIndexRowAsync(index, documentId, value, transaction, cancellationToken);
        }
    }

    private async Task DeleteIndexRowsAsync(
        string documentKind,
        string indexName,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM groundwork_document_indexes
            WHERE document_kind = @kind AND index_name = @index;
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "index", indexName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<(string DocumentId, string ContentJson)>> LoadDocumentsForIndexAsync(
        string documentKind,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, content_json
            FROM groundwork_documents
            WHERE document_kind = @kind;
            """,
            transaction);
        AddParameter(command, "kind", documentKind);

        var documents = new List<(string DocumentId, string ContentJson)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            documents.Add((reader.GetString(0), reader.GetString(1)));

        return documents;
    }

    private async Task InsertIndexRowAsync(
        MaterializedIndex index,
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
            (document_kind, index_name, index_value, document_id, is_unique)
            VALUES (@kind, @index, @value, @documentId, {isUnique});
            """,
            transaction);
        AddParameter(command, "kind", index.UnitIdentity);
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
