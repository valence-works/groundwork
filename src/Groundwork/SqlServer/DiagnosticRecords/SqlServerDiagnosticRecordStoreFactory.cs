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

    /// <summary>Creates a provider-neutral scope/session factory for a declared deployment.</summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new DelegatingDiagnosticRecordStoreSessionFactory(async (definition, cancellationToken) =>
            new DiagnosticRecordStoreLease(await CreateAsync(connectionString, definition, cancellationToken)));
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
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordQueryValidator.Validate(query, definition, CapabilityOnlyQueryHandler.Instance);
        SqlServerDiagnosticRecordValidator.ValidateScopeAndThrow(query.Scope, query.Stream);
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new SqlServerDiagnosticRecordStore(connectionString, definition);
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
        SqlServerDiagnosticRecordValidator.ValidateOperationAndThrow(request.Scope, request.Stream, request.OperationId);
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, cancellationToken: cancellationToken);
        var store = new SqlServerDiagnosticRecordStore(connectionString, definition);
        return await ExplainAsync(connectionString, store.Inner.BuildTrimSelectionCommand(request), cancellationToken);
    }

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
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = $"UPDATE STATISTICS {RelationalDiagnosticRecordSchema.RecordsTable}; UPDATE STATISTICS {RelationalDiagnosticRecordSchema.FieldsTable};";
            await statistics.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {diagnosticCommand.CommandText} SET STATISTICS XML OFF;";
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
}
