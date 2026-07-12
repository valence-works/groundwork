using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Provider.Relational;

namespace Groundwork.Sqlite.DiagnosticRecords;

public sealed class SqliteDiagnosticRecordStore : IDiagnosticRecordStore
{
    private readonly RelationalDiagnosticRecordStore inner;
    private readonly InstrumentedDiagnosticRecordStore instrumented;

    public SqliteDiagnosticRecordStore(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null)
        : this(
            SqliteRelationalSessions.CreateSerializedDeferred(connectionString),
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            definition,
            timeProvider,
            null)
    {
    }

    internal SqliteDiagnosticRecordStore(
        RelationalSessionFactory readSessions,
        RelationalSessionFactory writeSessions,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync)
    {
        inner = new(readSessions, writeSessions, definition, new SqliteDiagnosticRecordDialect(), timeProvider, interceptAsync);
        instrumented = new(inner, new("sqlite", "diagnostic-records"));
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

internal sealed class SqliteDiagnosticRecordDialect : RelationalDiagnosticRecordDialect
{
    public override string ApplyLimit(string selectSql, string parameterName) =>
        $"{selectSql} LIMIT {Parameter(parameterName)}";

    public override string Contains(string expression, string parameterName) =>
        $"instr({expression}, {Parameter(parameterName)}) > 0";

    public override string BuildOperationCleanup(
        string table,
        string highWaterParameterName,
        string batchSizeParameterName) =>
        $"""
        DELETE FROM {table}
        WHERE rowid IN (
            SELECT rowid
            FROM {table}
            WHERE tombstone_until_ticks < {Parameter(highWaterParameterName)}
            ORDER BY tombstone_until_ticks
            LIMIT {Parameter(batchSizeParameterName)}
        );
        """;

}
