using System.Text.RegularExpressions;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.DiagnosticRecords;

/// <summary>Performs a non-mutating SQLite inspection before a diagnostic-record session opens.</summary>
public sealed class SqliteDiagnosticRecordDeploymentInspector(string connectionString)
    : IDiagnosticRecordDeploymentInspector
{
    public string Provider => "sqlite";

    private readonly string connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString))
        : connectionString;

    public async ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        foreach (var stream in deployment.Streams)
            SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(stream);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentInspection.Ready(Provider);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (IsAbsentFile(builder))
            return DiagnosticRecordDeploymentInspection.Missing(Provider, deployment);
        if (builder.DataSource == ":memory:" ||
            builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            builder.Mode == SqliteOpenMode.Memory)
        {
            throw new NotSupportedException(
                "SQLite diagnostic-record runtime admission requires a durable database that can be reopened read-only.");
        }

        builder.Mode = SqliteOpenMode.ReadOnly;
        await using var connection = SqliteConnectionFactory.Create(builder.ConnectionString);
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
                column.Type == RelationalDiagnosticColumnType.Int64 ? "integer" : "text",
                column.IsNullable,
                column.Type == RelationalDiagnosticColumnType.Text && column.UsesBinaryTextSemantics ? "binary" : null)).ToArray(),
            table.PrimaryKey,
            "table",
            "btree",
            true)).ToArray();

    private static IReadOnlyList<RelationalDiagnosticIndexSnapshot> ExpectedIndexes() =>
        RelationalDiagnosticRecordSchema.Standard.Indexes.Select(index => new RelationalDiagnosticIndexSnapshot(
            index.Name,
            index.Table,
            index.Columns.Select(column => new RelationalDiagnosticIndexKeySnapshot(column, false)).ToArray(),
            index.IsUnique,
            null,
            [],
            "btree",
            true)).ToArray();

    private static bool IsAbsentFile(SqliteConnectionStringBuilder builder)
    {
        var dataSource = builder.DataSource;
        var isMemory = dataSource == ":memory:" ||
                       dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                       builder.Mode == SqliteOpenMode.Memory;
        return !isMemory && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !File.Exists(dataSource);
    }

    private static async Task<IReadOnlyList<RelationalDiagnosticTableSnapshot>> ReadTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var source = new Dictionary<string, (string Sql, string Kind)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql, type FROM sqlite_master WHERE type IN ('table', 'view') AND sql IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                source.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        }

        var result = new List<RelationalDiagnosticTableSnapshot>(source.Count);
        foreach (var pair in source)
        {
            var columns = new List<RelationalDiagnosticColumnSnapshot>();
            var primaryKey = new SortedDictionary<int, string>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(pair.Key)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                var storeType = reader.GetString(2).ToLowerInvariant();
                columns.Add(new(
                    name,
                    storeType,
                    reader.GetInt64(3) == 0,
                    storeType == "text" ? ColumnCollation(pair.Value.Sql, name) : null));
                if (reader.GetInt64(5) is var order && order > 0)
                    primaryKey.Add(checked((int)order), name);
            }
            result.Add(new(
                pair.Key,
                columns,
                primaryKey.Values.ToArray(),
                pair.Value.Kind,
                primaryKey.Count == 0 ? null : "btree",
                primaryKey.Count != 0));
        }
        return result;
    }

    private static async Task<IReadOnlyList<RelationalDiagnosticIndexSnapshot>> ReadIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND sql IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(reader.GetString(0));
        }

        var indexes = new List<RelationalDiagnosticIndexSnapshot>();
        foreach (var table in tables)
        {
            await using var list = connection.CreateCommand();
            list.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)});";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                var unique = reader.GetInt64(2) != 0;
                var partial = reader.FieldCount > 4 && reader.GetInt64(4) != 0;
                var keys = await ReadIndexKeysAsync(connection, name, cancellationToken);
                indexes.Add(new(name, table, keys, unique, partial ? "partial" : null, [], "btree", true));
            }
        }
        return indexes;
    }

    private static async Task<IReadOnlyList<RelationalDiagnosticIndexKeySnapshot>> ReadIndexKeysAsync(
        SqliteConnection connection,
        string index,
        CancellationToken cancellationToken)
    {
        var keys = new SortedDictionary<int, RelationalDiagnosticIndexKeySnapshot>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo({QuoteIdentifier(index)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt64(5) == 0 || reader.IsDBNull(2))
                continue;
            keys.Add(checked((int)reader.GetInt64(0)), new(reader.GetString(2), reader.GetInt64(3) != 0));
        }
        return keys.Values.ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, RelationalDiagnosticDefinitionSnapshot>> ReadDefinitionsAsync(
        SqliteConnection connection,
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

    private static string? ColumnCollation(string tableSql, string column)
    {
        var match = Regex.Match(
            tableSql,
            $@"(?im)\b{Regex.Escape(column)}\b\s+[^,]*?\bCOLLATE\s+(?<collation>\w+)");
        return match.Success ? match.Groups["collation"].Value.ToLowerInvariant() : "binary";
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
