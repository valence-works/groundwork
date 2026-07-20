using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.DiagnosticRecords;

/// <summary>Performs a non-mutating PostgreSQL inspection before a diagnostic-record session opens.</summary>
public sealed class PostgreSqlDiagnosticRecordDeploymentInspector(string connectionString)
    : IDiagnosticRecordDeploymentInspector
{
    public string Provider => "postgresql";

    private readonly string connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
        : connectionString;

    public async ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        foreach (var stream in deployment.Streams)
            PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(stream);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentInspection.Ready(Provider);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
                column.Type == RelationalDiagnosticColumnType.Int64 ? "int8" : "text",
                column.IsNullable,
                column.Type == RelationalDiagnosticColumnType.Text && column.UsesBinaryTextSemantics ? "C" : null)).ToArray(),
            table.PrimaryKey,
            "table",
            "btree",
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
                "btree",
                true);
        }).ToArray();

    private static async Task<IReadOnlyList<RelationalDiagnosticTableSnapshot>> ReadTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT table_ref.relname, attribute.attname, type_ref.typname, NOT attribute.attnotnull,
                   COALESCE(collation_ref.collname, ''), attribute.attnum, table_ref.relkind::text
            FROM pg_class AS table_ref
            JOIN pg_namespace AS schema_ref ON schema_ref.oid = table_ref.relnamespace
            JOIN pg_attribute AS attribute ON attribute.attrelid = table_ref.oid
            JOIN pg_type AS type_ref ON type_ref.oid = attribute.atttypid
            LEFT JOIN pg_collation AS collation_ref ON collation_ref.oid = attribute.attcollation
            WHERE schema_ref.nspname = current_schema()
              AND table_ref.relkind IN ('r', 'v', 'm', 'p', 'f')
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
            ORDER BY table_ref.relname, attribute.attnum;

            SELECT table_ref.relname, attribute.attname, key_column.ordinality, access_method.amname,
                   index_ref.indisvalid AND index_ref.indisready AND index_ref.indislive
            FROM pg_index AS index_ref
            JOIN pg_class AS table_ref ON table_ref.oid = index_ref.indrelid
            JOIN pg_class AS index_table ON index_table.oid = index_ref.indexrelid
            JOIN pg_namespace AS schema_ref ON schema_ref.oid = table_ref.relnamespace
            JOIN pg_am AS access_method ON access_method.oid = index_table.relam
            JOIN LATERAL unnest(index_ref.indkey) WITH ORDINALITY AS key_column(attnum, ordinality) ON TRUE
            JOIN pg_attribute AS attribute ON attribute.attrelid = table_ref.oid AND attribute.attnum = key_column.attnum
            WHERE schema_ref.nspname = current_schema() AND index_ref.indisprimary
            ORDER BY table_ref.relname, key_column.ordinality;
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
            kinds[table] = reader.GetString(6) == "r" ? "table" : reader.GetString(6);
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
            primaryKeyUsability[table] = reader.GetBoolean(4);
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
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT index_table.relname, table_ref.relname, index_ref.indisunique,
                   COALESCE(pg_get_expr(index_ref.indpred, index_ref.indrelid), ''),
                   key_column.ordinality, attribute.attname, index_ref.indnkeyatts,
                   CASE WHEN key_column.ordinality <= index_ref.indnkeyatts
                        THEN (index_ref.indoption[(key_column.ordinality - 1)::integer] & 1) <> 0
                        ELSE FALSE END,
                   access_method.amname,
                   index_ref.indisvalid AND index_ref.indisready AND index_ref.indislive
            FROM pg_index AS index_ref
            JOIN pg_class AS index_table ON index_table.oid = index_ref.indexrelid
            JOIN pg_class AS table_ref ON table_ref.oid = index_ref.indrelid
            JOIN pg_namespace AS schema_ref ON schema_ref.oid = table_ref.relnamespace
            JOIN pg_am AS access_method ON access_method.oid = index_table.relam
            JOIN LATERAL unnest(index_ref.indkey) WITH ORDINALITY AS key_column(attnum, ordinality) ON TRUE
            LEFT JOIN pg_attribute AS attribute ON attribute.attrelid = table_ref.oid AND attribute.attnum = key_column.attnum
            WHERE schema_ref.nspname = current_schema() AND NOT index_ref.indisprimary
            ORDER BY index_table.relname, key_column.ordinality;
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
                indexes[key] = index = (reader.GetBoolean(2), reader.GetString(3), reader.GetString(8), reader.GetBoolean(9), [], []);
            var column = reader.IsDBNull(5) ? "<expression>" : reader.GetString(5);
            if (reader.GetInt64(4) <= reader.GetInt16(6))
                index.Keys.Add(new(column, reader.GetBoolean(7)));
            else
                index.Includes.Add(column);
        }
        return indexes.Select(pair => new RelationalDiagnosticIndexSnapshot(
            pair.Key.Name, pair.Key.Table, pair.Value.Keys, pair.Value.Unique, pair.Value.Filter,
            pair.Value.Includes, pair.Value.Kind, pair.Value.IsUsable)).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, RelationalDiagnosticDefinitionSnapshot>> ReadDefinitionsAsync(
        NpgsqlConnection connection,
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
}
