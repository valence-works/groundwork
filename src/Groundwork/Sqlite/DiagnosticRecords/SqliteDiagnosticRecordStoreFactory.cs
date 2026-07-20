using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.DiagnosticRecords;

public static class SqliteDiagnosticRecordStoreFactory
{
    public static void ValidateDefinition(DiagnosticRecordStreamDefinition definition) =>
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);

    /// <summary>
    /// Creates a provider-neutral scope/session factory that admits only an already-deployed,
    /// compatible schema. Session opening and store leasing never materialize or repair storage.
    /// </summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordStoreSessionFactory(
            new SqliteDiagnosticRecordDeploymentInspector(connectionString),
            (definition, _) => ValueTask.FromResult(
                new DiagnosticRecordStoreLease(OpenExisting(connectionString, definition))));
    }

    /// <summary>
    /// Creates read-only native-plan inspection for already-admitted diagnostic-record storage.
    /// Returned raw plans may contain database metadata and query values; hosts must treat them
    /// as sensitive diagnostic evidence.
    /// </summary>
    public static IDiagnosticRecordPlanInspector CreatePlanInspector(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordPlanInspector(
            new SqliteDiagnosticRecordDeploymentInspector(connectionString),
            (definition, query, cancellationToken) => InspectQueryPlanAsync(connectionString, definition, query, cancellationToken),
            (definition, request, cancellationToken) => InspectStatisticsPlanAsync(connectionString, definition, request, cancellationToken),
            (definition, request, cancellationToken) => InspectTrimPlanAsync(connectionString, definition, request, cancellationToken));
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

    private static SqliteDiagnosticRecordStore OpenExisting(
        string connectionString,
        DiagnosticRecordStreamDefinition definition)
    {
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
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
        var store = new SqliteDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildTrimSelectionCommand(request), cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainStatisticsAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordRequestValidator.Validate(request, definition);
        var store = new SqliteDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildStatisticsCommand(request), cancellationToken);
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

    private static async ValueTask<DiagnosticRecordNativePlan> InspectQueryPlanAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken) =>
        new("sqlite", DiagnosticRecordPlanOperation.Query, DiagnosticRecordNativePlanFormats.SqliteExplainQueryPlan,
            await ExplainQueryAsync(connectionString, definition, query, cancellationToken));

    private static async ValueTask<DiagnosticRecordNativePlan> InspectTrimPlanAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken) =>
        new("sqlite", DiagnosticRecordPlanOperation.TrimSelection, DiagnosticRecordNativePlanFormats.SqliteExplainQueryPlan,
            await ExplainTrimAsync(connectionString, definition, request, cancellationToken));

    private static async ValueTask<DiagnosticRecordNativePlan> InspectStatisticsPlanAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken) =>
        new("sqlite", DiagnosticRecordPlanOperation.Statistics, DiagnosticRecordNativePlanFormats.SqliteExplainQueryPlan,
            await ExplainStatisticsAsync(connectionString, definition, request, cancellationToken));

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
