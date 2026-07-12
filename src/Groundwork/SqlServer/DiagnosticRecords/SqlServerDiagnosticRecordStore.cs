using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Provider.Relational;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Data.Common;

namespace Groundwork.SqlServer.DiagnosticRecords;

public sealed class SqlServerDiagnosticRecordStore : IDiagnosticRecordStore
{
    private readonly RelationalDiagnosticRecordStore inner;
    private readonly InstrumentedDiagnosticRecordStore instrumented;

    public SqlServerDiagnosticRecordStore(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null)
        : this(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            definition,
            timeProvider,
            null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal SqlServerDiagnosticRecordStore(
        RelationalSessionFactory sessions,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync)
    {
        SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        inner = new(sessions, sessions, definition, new SqlServerDiagnosticRecordDialect(), timeProvider, interceptAsync);
        var core = new CoreHandlers(inner);
        instrumented = new(
            new DiagnosticRecordStoreHandlers(core, core, core, core),
            new("sqlserver", "diagnostic-records"));
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

    private sealed class CoreHandlers(RelationalDiagnosticRecordStore inner) :
        IDiagnosticAppendHandler,
        IDiagnosticQueryHandler,
        IDiagnosticInspectHandler,
        IDiagnosticTrimHandler
    {
        public DiagnosticQueryHandlerCapabilities Capabilities => inner.Capabilities;

        public ValueTask<DiagnosticAppendResult> AppendAsync(
            DiagnosticRecordBatch batch,
            CancellationToken cancellationToken = default)
        {
            SqlServerDiagnosticRecordValidator.ValidateAppendAndThrow(batch);
            return inner.AppendAsync(batch, cancellationToken);
        }

        public ValueTask<DiagnosticRecordPage> QueryAsync(
            DiagnosticRecordQuery query,
            CancellationToken cancellationToken = default)
        {
            SqlServerDiagnosticRecordValidator.ValidateScopeAndThrow(query.Scope, query.Stream);
            return inner.QueryAsync(query, cancellationToken);
        }

        public ValueTask<DiagnosticStreamStatistics> InspectAsync(
            DiagnosticStreamInspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            SqlServerDiagnosticRecordValidator.ValidateScopeAndThrow(request.Scope, request.Stream);
            return inner.InspectAsync(request, cancellationToken);
        }

        public ValueTask<DiagnosticTrimResult> TrimAsync(
            DiagnosticTrimRequest request,
            CancellationToken cancellationToken = default)
        {
            SqlServerDiagnosticRecordValidator.ValidateOperationAndThrow(request.Scope, request.Stream, request.OperationId);
            return inner.TrimAsync(request, cancellationToken);
        }
    }
}

internal sealed class SqlServerDiagnosticRecordDialect : RelationalDiagnosticRecordDialect
{
    public override bool UsesSessionScopedStreamLock => true;

    public override int MaxParametersPerCommand => 2_100;

    private static readonly Regex CursorIdentifier = new(
        @"(?<![@\w\[])\bcursor\b(?!\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override string ApplyLimit(string selectSql, string parameterName)
    {
        const string select = "SELECT ";
        if (!selectSql.StartsWith(select, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SQL Server limit application requires a SELECT statement.", nameof(selectSql));
        return $"SELECT TOP ({Parameter(parameterName)}) {selectSql[select.Length..]}";
    }

    public override string Contains(string expression, string parameterName) =>
        $"CHARINDEX({Parameter(parameterName)}, {expression}) > 0";

    public override string BuildOperationCleanup(
        string table,
        string highWaterParameterName,
        string batchSizeParameterName) =>
        $"""
        WITH cleanup_rows AS (
            SELECT TOP ({Parameter(batchSizeParameterName)}) *
            FROM {table} WITH (UPDLOCK, READPAST)
            WHERE tombstone_until_ticks < {Parameter(highWaterParameterName)}
            ORDER BY tombstone_until_ticks
        )
        DELETE FROM cleanup_rows;
        """;

    public override string BuildProviderClockAdvance(string table, string currentParameterName) =>
        $"UPDATE {table} WITH (UPDLOCK, HOLDLOCK) SET clock_high_water_ticks = CASE WHEN clock_high_water_ticks > {Parameter(currentParameterName)} THEN clock_high_water_ticks ELSE {Parameter(currentParameterName)} END OUTPUT INSERTED.clock_high_water_ticks WHERE id = 1;";

    public override string BuildStreamLock() =>
        "DECLARE @lock_result INT, @lock_resource NVARCHAR(255) = CONCAT('groundwork:diagnostic:', DATALENGTH(@tenant), ':', @tenant, DATALENGTH(@scope), ':', @scope, DATALENGTH(@stream), ':', @stream); EXEC @lock_result = sp_getapplock @Resource = @lock_resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; IF @lock_result < 0 THROW 51001, 'Could not acquire the diagnostic stream writer lock.', 1;";

    public override string BuildStreamUnlock() =>
        "DECLARE @lock_result INT, @lock_resource NVARCHAR(255) = CONCAT('groundwork:diagnostic:', DATALENGTH(@tenant), ':', @tenant, DATALENGTH(@scope), ':', @scope, DATALENGTH(@stream), ':', @stream); EXEC @lock_result = sp_releaseapplock @Resource = @lock_resource, @LockOwner = 'Session'; IF @lock_result < 0 THROW 51002, 'Could not release the diagnostic stream writer lock.', 1;";

    public override void InvalidateConnectionPool(DbConnection connection) =>
        SqlConnection.ClearPool((SqlConnection)connection);

    public override string PrepareCommandText(string commandText) =>
        CursorIdentifier.Replace(commandText, "[cursor]");

    public override void ConfigureParameter(DbParameter parameter, object value)
    {
        if (parameter is SqlParameter sqlParameter && value is string text)
        {
            var carriesUnicodeContent = sqlParameter.ParameterName.StartsWith("@canonical", StringComparison.Ordinal) ||
                                        sqlParameter.ParameterName is "@payload" or "@result";
            sqlParameter.SqlDbType = carriesUnicodeContent
                ? System.Data.SqlDbType.NVarChar
                : System.Data.SqlDbType.VarChar;
            sqlParameter.Size = carriesUnicodeContent
                ? text.Length is > 4_000 ? -1 : Math.Max(1, text.Length)
                : Math.Max(1, System.Text.Encoding.UTF8.GetByteCount(text));
        }
    }

    public override string BuildCountFromPage(string pageSql)
    {
        const string marker = "SELECT TOP (";
        var selectIndex = pageSql.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (selectIndex < 0)
            throw new InvalidOperationException("The SQL Server diagnostic page query has no final TOP projection.");
        return $"{pageSql[..selectIndex]}SELECT COUNT(*) FROM selected;";
    }
}
