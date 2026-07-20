using System.Globalization;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.DiagnosticRecords;

/// <summary>Performs a non-mutating SQL Server inspection before a diagnostic-record session opens.</summary>
public sealed class SqlServerDiagnosticRecordDeploymentInspector(string connectionString)
    : IDiagnosticRecordDeploymentInspector
{
    private const string BinaryUtf8Collation = "Latin1_General_100_BIN2_UTF8";

    public string Provider => "sqlserver";

    private readonly string connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A SQL Server connection string is required.", nameof(connectionString))
        : connectionString;

    public async ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        foreach (var stream in deployment.Streams)
            SqlServerDiagnosticRecordValidator.ValidateDefinitionAndThrow(stream);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentInspection.Ready(Provider);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureReadCommittedSnapshotAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(connection, cancellationToken);
        var indexes = await ReadIndexesAsync(connection, cancellationToken);
        var physical = RelationalDiagnosticRecordDeploymentInspector.ClassifyPhysical(
            Provider,
            deployment,
            ExpectedTables(),
            ExpectedIndexes(),
            tables,
            indexes);
        if (!physical.IsReady)
            return physical;

        return RelationalDiagnosticRecordDeploymentInspector.ClassifyDefinitions(
            Provider,
            deployment,
            await ReadDefinitionsAsync(connection, cancellationToken));
    }

    private static IReadOnlyList<RelationalDiagnosticTableSnapshot> ExpectedTables() =>
        RelationalDiagnosticRecordSchema.Standard.Tables.Select(table => new RelationalDiagnosticTableSnapshot(
            table.Name,
            table.Columns.Select(column => new RelationalDiagnosticColumnSnapshot(
                column.Name,
                SqlType(column),
                column.IsNullable,
                column.Type == RelationalDiagnosticColumnType.Text && column.UsesBinaryTextSemantics
                    ? BinaryUtf8Collation
                    : null)).ToArray(),
            table.PrimaryKey,
            "USER_TABLE",
            "NONCLUSTERED",
            true)).ToArray();

    private static IReadOnlyList<RelationalDiagnosticIndexSnapshot> ExpectedIndexes() =>
        RelationalDiagnosticRecordSchema.Standard.Indexes.Select(index =>
        {
            var isLatest = index.Name == "ix_groundwork_diagnostic_fields_scope_latest";
            return new RelationalDiagnosticIndexSnapshot(
                index.Name,
                index.Table,
                (isLatest ? index.Columns.Where(column => column != "value_ordinal") : index.Columns)
                .Select(column => new RelationalDiagnosticIndexKeySnapshot(column, false)).ToArray(),
                index.IsUnique,
                isLatest ? "value_ordinal=0" : null,
                [],
                "NONCLUSTERED",
                true);
        }).ToArray();

    private static async Task<IReadOnlyList<RelationalDiagnosticTableSnapshot>> ReadTablesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT object_ref.name, column_ref.name,
                   CASE WHEN type_ref.name = 'varchar' THEN
                       CASE WHEN column_ref.max_length = -1 THEN 'varchar(max)'
                            ELSE CONCAT('varchar(', column_ref.max_length, ')') END
                   ELSE type_ref.name END,
                   column_ref.is_nullable, COALESCE(column_ref.collation_name, ''), column_ref.column_id
                   , object_ref.type_desc
            FROM sys.objects AS object_ref
            JOIN sys.columns AS column_ref ON column_ref.object_id = object_ref.object_id
            JOIN sys.types AS type_ref ON type_ref.user_type_id = column_ref.user_type_id
            WHERE object_ref.schema_id = SCHEMA_ID() AND object_ref.type IN ('U', 'V')
            ORDER BY object_ref.name, column_ref.column_id;

            SELECT table_ref.name, column_ref.name, index_column.key_ordinal, index_ref.type_desc,
                   index_ref.is_disabled
            FROM sys.indexes AS index_ref
            JOIN sys.tables AS table_ref ON table_ref.object_id = index_ref.object_id
            JOIN sys.index_columns AS index_column ON index_column.object_id = index_ref.object_id AND index_column.index_id = index_ref.index_id
            JOIN sys.columns AS column_ref ON column_ref.object_id = index_column.object_id AND column_ref.column_id = index_column.column_id
            WHERE table_ref.schema_id = SCHEMA_ID() AND index_ref.is_primary_key = 1 AND index_column.key_ordinal > 0
            ORDER BY table_ref.name, index_column.key_ordinal;
            """;
        var columns = new Dictionary<string, List<RelationalDiagnosticColumnSnapshot>>(StringComparer.Ordinal);
        var primaryKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var primaryKeyKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var primaryKeyUsability = new Dictionary<string, bool>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!columns.TryGetValue(table, out var tableColumns))
                columns[table] = tableColumns = [];
            kinds[table] = reader.GetString(6);
            tableColumns.Add(new(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4) is { Length: > 0 } collation ? collation : null));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!primaryKeys.TryGetValue(table, out var key))
                primaryKeys[table] = key = [];
            key.Add(reader.GetString(1));
            primaryKeyKinds[table] = reader.GetString(3);
            primaryKeyUsability[table] = !reader.GetBoolean(4);
        }
        return columns.Select(pair => new RelationalDiagnosticTableSnapshot(
            pair.Key,
            pair.Value,
            primaryKeys.TryGetValue(pair.Key, out var key) ? key : [],
            kinds[pair.Key],
            primaryKeyKinds.TryGetValue(pair.Key, out var primaryKeyKind) ? primaryKeyKind : null,
            primaryKeyUsability.TryGetValue(pair.Key, out var isPrimaryKeyUsable) && isPrimaryKeyUsable)).ToArray();
    }

    private static async Task<IReadOnlyList<RelationalDiagnosticIndexSnapshot>> ReadIndexesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT index_ref.name, table_ref.name, index_ref.is_unique, COALESCE(index_ref.filter_definition, ''),
                   index_column.key_ordinal, column_ref.name, index_column.is_descending_key,
                   index_column.is_included_column,
                   CASE WHEN index_ref.is_hypothetical = 1 THEN CONCAT(index_ref.type_desc, ':HYPOTHETICAL')
                        ELSE index_ref.type_desc END,
                   index_ref.is_disabled
            FROM sys.indexes AS index_ref
            JOIN sys.tables AS table_ref ON table_ref.object_id = index_ref.object_id
            JOIN sys.index_columns AS index_column ON index_column.object_id = index_ref.object_id AND index_column.index_id = index_ref.index_id
            JOIN sys.columns AS column_ref ON column_ref.object_id = index_column.object_id AND column_ref.column_id = index_column.column_id
            WHERE table_ref.schema_id = SCHEMA_ID()
              AND index_ref.name IS NOT NULL
              AND index_ref.is_primary_key = 0
              AND (index_column.key_ordinal > 0 OR index_column.is_included_column = 1)
            ORDER BY table_ref.name, index_ref.name, index_column.is_included_column,
                     index_column.key_ordinal, index_column.index_column_id;
            """;
        var indexes = new Dictionary<(string Table, string Name), (bool Unique, string? Filter, string Kind, bool IsUsable, List<RelationalDiagnosticIndexKeySnapshot> Keys, List<string> Includes)>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var key = (reader.GetString(1), name);
            if (!indexes.TryGetValue(key, out var index))
                indexes[key] = index = (reader.GetBoolean(2), reader.GetString(3), reader.GetString(8), !reader.GetBoolean(9), [], []);
            if (reader.GetBoolean(7))
                index.Includes.Add(reader.GetString(5));
            else
                index.Keys.Add(new(reader.GetString(5), reader.GetBoolean(6)));
        }
        return indexes.Select(pair => new RelationalDiagnosticIndexSnapshot(
            pair.Key.Name, pair.Key.Table, pair.Value.Keys, pair.Value.Unique, pair.Value.Filter,
            pair.Value.Includes, pair.Value.Kind, pair.Value.IsUsable)).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, RelationalDiagnosticDefinitionSnapshot>> ReadDefinitionsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT stream_id, schema_version, definition_fingerprint, algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable};";
        var definitions = new Dictionary<string, RelationalDiagnosticDefinitionSnapshot>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            definitions.Add(reader.GetString(0), new(reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        return definitions;
    }

    private static string SqlType(RelationalDiagnosticColumnDefinition column)
    {
        if (column.Type == RelationalDiagnosticColumnType.Int64)
            return "bigint";
        return column.Name switch
        {
            "payload_json" or "result_json" or "canonical_value" or "comparison_key" or
                "search_key" or "algorithm_manifest" or "canonical_definition" => "varchar(max)",
            "comparison_key_prefix" => "varchar(256)",
            "comparison_key_hash" or "definition_fingerprint" or "algorithm_manifest_fingerprint" => "varchar(64)",
            "record_id" => "varchar(128)",
            "tenant_id" or "scope_id" or "stream_id" or "field_name" or "nonce" => "varchar(64)",
            _ => "varchar(256)"
        };
    }

    private static async Task EnsureReadCommittedSnapshotAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();";
        var enabled = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        if (!enabled)
        {
            throw new InvalidOperationException(
                "SQL Server diagnostic records require READ_COMMITTED_SNAPSHOT ON so runtime admission can observe the durable snapshot without materializing schema state.");
        }
    }
}
