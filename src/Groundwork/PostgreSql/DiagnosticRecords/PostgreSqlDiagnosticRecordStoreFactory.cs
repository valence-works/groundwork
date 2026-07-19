using System.Globalization;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Provider.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.DiagnosticRecords;

public static class PostgreSqlDiagnosticRecordStoreFactory
{
    public static void ValidateDefinition(DiagnosticRecordStreamDefinition definition) =>
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);

    /// <summary>Creates a provider-neutral scope/session factory for a declared deployment.</summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordStoreSessionFactory(async (definition, cancellationToken) =>
            new DiagnosticRecordStoreLease(await CreateAsync(connectionString, definition, cancellationToken)));
    }

    public static async Task<PostgreSqlDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(definition);
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        return new(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            definition,
            null,
            null);
    }

    internal static async Task<PostgreSqlDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        Func<NpgsqlConnection>? createOperationConnection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(timeProvider);
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        var sessions = RelationalSessionFactory.Concurrent(createOperationConnection ?? (() => new NpgsqlConnection(connectionString)));
        return new(sessions, definition, timeProvider, interceptAsync);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordQueryValidator.Validate(query, definition, CapabilityOnlyQueryHandler.Instance);
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new PostgreSqlDiagnosticRecordStore(connectionString, definition);
        var snapshot = query.Continuation is null
            ? await ReadCursorHighWaterAsync(connectionString, query.Scope, query.Stream, cancellationToken)
            : long.Parse(query.Continuation.SnapshotHighWater.Value, CultureInfo.InvariantCulture);
        return await ExplainAsync(connectionString, store.Inner.BuildQueryCommand(query, snapshot), cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordRequestValidator.Validate(request, definition);
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new PostgreSqlDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildTrimSelectionCommand(request), cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT comparison_key FROM {RelationalDiagnosticRecordSchema.FieldsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream AND field_name = @field ORDER BY cursor, value_ordinal;";
        command.Parameters.AddWithValue("tenant", scope.TenantId);
        command.Parameters.AddWithValue("scope", scope.ScopeId);
        command.Parameters.AddWithValue("stream", stream.Value);
        command.Parameters.AddWithValue("field", field);
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyList<string>> ExplainAsync(
        string connectionString,
        RelationalDiagnosticCommand diagnosticCommand,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = $"ANALYZE {RelationalDiagnosticRecordSchema.RecordsTable}; ANALYZE {RelationalDiagnosticRecordSchema.FieldsTable};";
            await statistics.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (FORMAT JSON) {diagnosticCommand.CommandText}";
        foreach (var item in diagnosticCommand.Parameters)
            command.Parameters.AddWithValue(item.Key, item.Value);
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            plans.Add(reader.GetString(0));
        return plans;
    }

    private static async ValueTask<long> ReadCursorHighWaterAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream;";
        command.Parameters.AddWithValue("tenant", scope.TenantId);
        command.Parameters.AddWithValue("scope", scope.ScopeId);
        command.Parameters.AddWithValue("stream", stream.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
    }

    private sealed class CapabilityOnlyQueryHandler : IDiagnosticQueryHandler
    {
        public static CapabilityOnlyQueryHandler Instance { get; } = new();
        public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
            Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(), true, true, true, true, true);

        public ValueTask<DiagnosticRecordPage> QueryAsync(
            DiagnosticRecordQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
