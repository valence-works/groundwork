using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Provider.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.DiagnosticRecords;

public sealed class PostgreSqlDiagnosticRecordStore : IDiagnosticRecordStore
{
    private readonly RelationalDiagnosticRecordStore inner;
    private readonly InstrumentedDiagnosticRecordStore instrumented;

    public PostgreSqlDiagnosticRecordStore(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null)
        : this(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            definition,
            timeProvider,
            null,
            (snapshot, cancellationToken) => PostgreSqlDiagnosticRecordMaterializer.AdmitAsync(
                connectionString,
                snapshot,
                cancellationToken))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal PostgreSqlDiagnosticRecordStore(
        RelationalSessionFactory sessions,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        Func<DiagnosticRecordStreamDefinition, CancellationToken, Task>? materializeAsync = null)
    {
        var snapshot = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(snapshot);
        inner = new(
            sessions,
            sessions,
            snapshot,
            new PostgreSqlDiagnosticRecordDialect(),
            timeProvider,
            interceptAsync,
            materializeAsync is null ? null : cancellationToken => materializeAsync(snapshot, cancellationToken));
        instrumented = new(inner, new("postgresql", "diagnostic-records"));
    }

    public DiagnosticRecordStoreHandlers Handlers => instrumented.Handlers;

    public ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default) =>
        instrumented.AppendAsync(batch, cancellationToken);

    public ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        instrumented.QueryAsync(query, cancellationToken);

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        instrumented.InspectAsync(request, cancellationToken);

    public ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        instrumented.TrimAsync(request, cancellationToken);

    internal RelationalDiagnosticRecordStore Inner => inner;
}

internal sealed class PostgreSqlDiagnosticRecordDialect : RelationalDiagnosticRecordDialect
{
    public override bool UsesSessionScopedStreamLock => true;

    public override string ApplyLimit(string selectSql, string parameterName) =>
        $"{selectSql} LIMIT {Parameter(parameterName)}";

    public override string Contains(string expression, string parameterName) =>
        $"strpos({expression}, {Parameter(parameterName)}) > 0";

    public override string BuildOperationCleanup(
        string table,
        string highWaterParameterName,
        string batchSizeParameterName) =>
        $"""
        DELETE FROM {table}
        WHERE ctid IN (
            SELECT ctid
            FROM {table}
            WHERE tombstone_until_ticks < {Parameter(highWaterParameterName)}
            ORDER BY tombstone_until_ticks
            LIMIT {Parameter(batchSizeParameterName)}
        );
        """;

    public override int MaxParametersPerCommand => 65_535;

    public override string BuildProviderClockAdvance(string table, string currentParameterName) =>
        $"UPDATE {table} SET clock_high_water_ticks = GREATEST(clock_high_water_ticks, {Parameter(currentParameterName)}) WHERE id = 1 RETURNING clock_high_water_ticks;";

    public override string BuildStreamLock() =>
        "SELECT pg_advisory_lock(hashtextextended(length(@tenant) || ':' || @tenant || length(@scope) || ':' || @scope || length(@stream) || ':' || @stream, 0));";

    public override string BuildStreamUnlock() =>
        "SELECT pg_advisory_unlock(hashtextextended(length(@tenant) || ':' || @tenant || length(@scope) || ':' || @scope || length(@stream) || ':' || @stream, 0));";

    public override void ValidateStreamUnlockResult(object? result)
    {
        if (result is not true)
            throw new InvalidOperationException("PostgreSQL did not release the diagnostic stream advisory lock.");
    }

    public override void InvalidateConnectionPool(System.Data.Common.DbConnection connection) =>
        NpgsqlConnection.ClearPool((NpgsqlConnection)connection);
}
