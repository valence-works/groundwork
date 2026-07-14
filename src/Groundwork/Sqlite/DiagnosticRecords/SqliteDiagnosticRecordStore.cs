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
            null,
            (snapshot, cancellationToken) => SqliteDiagnosticRecordMaterializer.AdmitAsync(
                connectionString,
                snapshot,
                cancellationToken))
    {
    }

    internal SqliteDiagnosticRecordStore(
        RelationalSessionFactory readSessions,
        RelationalSessionFactory writeSessions,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        Func<DiagnosticRecordStreamDefinition, CancellationToken, Task>? materializeAsync = null)
    {
        var snapshot = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(snapshot);
        inner = new(
            readSessions,
            writeSessions,
            snapshot,
            new SqliteDiagnosticRecordDialect(),
            timeProvider,
            interceptAsync,
            materializeAsync is null ? null : cancellationToken => materializeAsync(snapshot, cancellationToken));
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
    public override string TableReference(string table, string alias) =>
        table == RelationalDiagnosticRecordSchema.FieldsTable && alias == "lfield"
            ? $"{table} AS {alias} INDEXED BY ix_groundwork_diagnostic_fields_scope_latest"
            : base.TableReference(table, alias);

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
