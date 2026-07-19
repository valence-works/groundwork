using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.DiagnosticRecords;

public static class SqliteDiagnosticRecordStoreFactory
{
    /// <summary>Creates a provider-neutral scope/session factory for a declared deployment.</summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordStoreSessionFactory(async (definition, cancellationToken) =>
            new DiagnosticRecordStoreLease(await CreateAsync(connectionString, definition, cancellationToken)));
    }

    public static async Task<SqliteDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        return new(
            SqliteRelationalSessions.CreateSerializedDeferred(connectionString),
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            definition,
            null,
            null);
    }

    internal static async Task<SqliteDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        SqliteImmediateTransactionObserver? transactionObserver = null,
        CancellationToken cancellationToken = default)
    {
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        return new(
            SqliteRelationalSessions.CreateSerializedDeferred(connectionString),
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString, transactionObserver),
            definition,
            timeProvider,
            interceptAsync);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordQueryValidator.Validate(query, definition, new CapabilityOnlyQueryHandler());
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new SqliteDiagnosticRecordStore(connectionString, definition);
        var snapshot = query.Continuation is null
            ? await ReadCursorHighWaterAsync(connectionString, query.Scope, query.Stream, cancellationToken)
            : long.Parse(query.Continuation.SnapshotHighWater.Value);
        return await ExplainAsync(connectionString, store.Inner.BuildQueryCommand(query, snapshot), cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordRequestValidator.Validate(request, definition);
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new SqliteDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildTrimSelectionCommand(request), cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT comparison_key FROM {RelationalDiagnosticRecordSchema.FieldsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream AND field_name = @field ORDER BY cursor, value_ordinal;";
        command.Parameters.AddWithValue("@tenant", scope.TenantId);
        command.Parameters.AddWithValue("@scope", scope.ScopeId);
        command.Parameters.AddWithValue("@stream", stream.Value);
        command.Parameters.AddWithValue("@field", field);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    internal static async ValueTask<long> CountOperationRowsAsync(
        string connectionString,
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        var table = kind == DiagnosticOperationKind.Append
            ? RelationalDiagnosticRecordSchema.AppendOperationsTable
            : RelationalDiagnosticRecordSchema.TrimOperationsTable;
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyList<string>> ExplainAsync(
        string connectionString,
        RelationalDiagnosticCommand diagnosticCommand,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var analyze = connection.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            await analyze.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {diagnosticCommand.CommandText}";
        foreach (var item in diagnosticCommand.Parameters)
            command.Parameters.AddWithValue($"@{item.Key}", item.Value);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(3));
        return result;
    }

    private static async ValueTask<long> ReadCursorHighWaterAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream;";
        command.Parameters.AddWithValue("@tenant", scope.TenantId);
        command.Parameters.AddWithValue("@scope", scope.ScopeId);
        command.Parameters.AddWithValue("@stream", stream.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CapabilityOnlyQueryHandler : IDiagnosticQueryHandler
    {
        public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
            Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(), true, true, true, true, true);

        public ValueTask<DiagnosticRecordPage> QueryAsync(DiagnosticRecordQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
