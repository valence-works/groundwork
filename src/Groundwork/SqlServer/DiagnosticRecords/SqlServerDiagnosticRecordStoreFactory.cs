using System.Globalization;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Provider.Relational;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.DiagnosticRecords;

public static class SqlServerDiagnosticRecordStoreFactory
{
    public static void ValidateDefinition(DiagnosticRecordStreamDefinition definition) =>
        SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);

    public static async Task ValidateAdmissionAsync(
        string connectionString,
        IReadOnlyList<DiagnosticRecordStreamDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
            ValidateDefinition(definition);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();";
        var enabled = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
        if (!enabled)
        {
            throw new InvalidOperationException(
                "SQL Server diagnostic records require READ_COMMITTED_SNAPSHOT ON.");
        }
    }

    /// <summary>
    /// Creates a provider-neutral scope/session factory that admits only an already-deployed,
    /// compatible schema. Session opening and store leasing never materialize or repair storage.
    /// </summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordStoreSessionFactory(
            new SqlServerDiagnosticRecordDeploymentInspector(connectionString),
            (definition, _) => ValueTask.FromResult(
                new DiagnosticRecordStoreLease(OpenExisting(connectionString, definition))));
    }

    /// <summary>
    /// Creates read-only native-plan inspection for already-admitted diagnostic-record storage.
    /// Returned raw plans may contain database metadata and query values; hosts must treat them
    /// as sensitive diagnostic evidence.
    /// </summary>
    public static IDiagnosticRecordPlanInspector CreatePlanInspector(string connectionString)
        => CreatePlanInspector(connectionString, new SqlServerDiagnosticRecordPlanExplainHooks());

    internal static IDiagnosticRecordPlanInspector CreatePlanInspector(
        string connectionString,
        SqlServerDiagnosticRecordPlanExplainHooks hooks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(hooks);
        return new DelegatingDiagnosticRecordPlanInspector(
            new SqlServerDiagnosticRecordDeploymentInspector(connectionString),
            (definition, query, cancellationToken) => InspectQueryPlanAsync(connectionString, definition, query, hooks, cancellationToken),
            (definition, request, cancellationToken) => InspectTrimPlanAsync(connectionString, definition, request, hooks, cancellationToken));
    }

    public static async Task<SqlServerDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(definition);
        SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        return new(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            definition,
            null,
            null);
    }

    private static SqlServerDiagnosticRecordStore OpenExisting(
        string connectionString,
        DiagnosticRecordStreamDefinition definition)
    {
        SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        return new(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            definition,
            null,
            null);
    }

    internal static async Task<SqlServerDiagnosticRecordStore> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        Func<SqlConnection>? createOperationConnection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(timeProvider);
        SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
        var sessions = RelationalSessionFactory.Concurrent(createOperationConnection ?? (() => new SqlConnection(connectionString)));
        return new(sessions, definition, timeProvider, interceptAsync);
    }

    internal static async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        await ExplainQueryAsync(
            connectionString,
            definition,
            query,
            new SqlServerDiagnosticRecordPlanExplainHooks(),
            cancellationToken);

    internal static async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        await ExplainTrimAsync(
            connectionString,
            definition,
            request,
            new SqlServerDiagnosticRecordPlanExplainHooks(),
            cancellationToken);

    internal static async ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT comparison_key FROM {RelationalDiagnosticRecordSchema.FieldsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream AND field_name = @field ORDER BY [cursor], value_ordinal;";
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyList<string>> ExplainAsync(
        string connectionString,
        RelationalDiagnosticCommand diagnosticCommand,
        SqlServerDiagnosticRecordPlanExplainHooks hooks,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Exception? primaryFailure = null;
        try
        {
            await SetStatisticsXmlAsync(connection, enabled: true, cancellationToken);
            await InvokeAsync(hooks.AfterEnableAcknowledged, cancellationToken);
            await InvokeAsync(hooks.BeforeRead, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = diagnosticCommand.CommandText;
            foreach (var item in diagnosticCommand.Parameters)
                AddParameter(command, item.Key, item.Value);
            var plans = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            do
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.FieldCount != 1)
                        continue;
                    var value = reader.GetValue(0);
                    if (value is System.Data.SqlTypes.SqlXml xml)
                        plans.Add(xml.Value);
                    else if (value is string text && text.Contains("ShowPlanXML", StringComparison.Ordinal))
                        plans.Add(text);
                }
            } while (await reader.NextResultAsync(cancellationToken));
            return plans;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await InvokeAsync(hooks.BeforeDisable, CancellationToken.None);
                await SetStatisticsXmlAsync(connection, enabled: false, CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                await QuarantineAsync(connection, cleanupFailure);
                if (primaryFailure is not null)
                    RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
                else
                    throw;
            }
        }
    }

    private static async ValueTask<DiagnosticRecordNativePlan> InspectQueryPlanAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        SqlServerDiagnosticRecordPlanExplainHooks hooks,
        CancellationToken cancellationToken) =>
        new("sqlserver", DiagnosticRecordPlanOperation.Query, DiagnosticRecordNativePlanFormats.SqlServerShowplanXml,
            await ExplainQueryAsync(connectionString, definition, query, hooks, cancellationToken));

    private static async ValueTask<DiagnosticRecordNativePlan> InspectTrimPlanAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        SqlServerDiagnosticRecordPlanExplainHooks hooks,
        CancellationToken cancellationToken) =>
        new("sqlserver", DiagnosticRecordPlanOperation.TrimSelection, DiagnosticRecordNativePlanFormats.SqlServerShowplanXml,
            await ExplainTrimAsync(connectionString, definition, request, hooks, cancellationToken));

    private static async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        SqlServerDiagnosticRecordPlanExplainHooks hooks,
        CancellationToken cancellationToken)
    {
        DiagnosticRecordQueryValidator.Validate(query, definition, CapabilityOnlyQueryHandler.Instance);
        SqlServerDiagnosticRecordValidator.ValidateScopeAndThrow(query.Scope, query.Stream);
        var store = new SqlServerDiagnosticRecordStore(connectionString, definition);
        var snapshot = query.Continuation is null
            ? await ReadCursorHighWaterAsync(connectionString, query.Scope, query.Stream, cancellationToken)
            : long.Parse(query.Continuation.SnapshotHighWater.Value, CultureInfo.InvariantCulture);
        return await ExplainAsync(connectionString, store.Inner.BuildQueryCommand(query, snapshot), hooks, cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        SqlServerDiagnosticRecordPlanExplainHooks hooks,
        CancellationToken cancellationToken)
    {
        DiagnosticRecordRequestValidator.Validate(request, definition);
        SqlServerDiagnosticRecordValidator.ValidateOperationAndThrow(request.Scope, request.Stream, request.OperationId);
        var store = new SqlServerDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildTrimSelectionCommand(request), hooks, cancellationToken);
    }

    private static async ValueTask<long> ReadCursorHighWaterAsync(
        string connectionString,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE tenant_id = @tenant AND scope_id = @scope AND stream_id = @stream;";
        command.Parameters.AddWithValue("@tenant", scope.TenantId);
        command.Parameters.AddWithValue("@scope", scope.ScopeId);
        command.Parameters.AddWithValue("@stream", stream.Value);
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

    private static void AddParameter(SqlCommand command, string name, object value)
    {
        var parameter = command.Parameters.AddWithValue($"@{name}", value);
        if (value is string text)
        {
            var carriesUnicodeContent = parameter.ParameterName.StartsWith("@canonical", StringComparison.Ordinal) ||
                                        parameter.ParameterName is "@payload" or "@result";
            parameter.SqlDbType = carriesUnicodeContent
                ? System.Data.SqlDbType.NVarChar
                : System.Data.SqlDbType.VarChar;
            parameter.Size = carriesUnicodeContent
                ? text.Length is > 4_000 ? -1 : Math.Max(1, text.Length)
                : Math.Max(1, System.Text.Encoding.UTF8.GetByteCount(text));
        }
    }

    private static async ValueTask SetStatisticsXmlAsync(
        SqlConnection connection,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML {(enabled ? "ON" : "OFF")};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task QuarantineAsync(SqlConnection connection, Exception cleanupFailure)
    {
        try
        {
            SqlConnection.ClearPool(connection);
        }
        catch (Exception quarantineFailure)
        {
            RelationalCleanupFailures.Attach(cleanupFailure, quarantineFailure);
        }
        try
        {
            await connection.CloseAsync();
        }
        catch (Exception quarantineFailure)
        {
            RelationalCleanupFailures.Attach(cleanupFailure, quarantineFailure);
        }
    }

    private static ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask>? hook,
        CancellationToken cancellationToken) =>
        hook?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
}

internal sealed record SqlServerDiagnosticRecordPlanExplainHooks(
    Func<CancellationToken, ValueTask>? AfterEnableAcknowledged = null,
    Func<CancellationToken, ValueTask>? BeforeRead = null,
    Func<CancellationToken, ValueTask>? BeforeDisable = null);
