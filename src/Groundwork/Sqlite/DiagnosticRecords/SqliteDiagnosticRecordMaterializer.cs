using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.DiagnosticRecords;

public static class SqliteDiagnosticRecordMaterializer
{
    internal static async Task AdmitAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        try
        {
            if (await RelationalDiagnosticRecordAdmissionState.ValidateIfPresentAsync(
                    () => new SqliteConnection(connectionString),
                    definition,
                    "SQLite",
                    cancellationToken))
                return;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            // The schema is absent; materialize it below.
        }
        await MaterializeAsync(connectionString, definition, cancellationToken: cancellationToken);
    }

    public static Task MaterializeAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        RelationalDiagnosticRecordSchema? schema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SqliteDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        var state = DiagnosticRecordPhysicalSchemaState.Capture(definition);
        return MaterializeCoreAsync(connectionString, schema, definition, state, cancellationToken);
    }

    public static async Task MaterializeAsync(
        string connectionString,
        RelationalDiagnosticRecordSchema? schema = null,
        CancellationToken cancellationToken = default) =>
        await MaterializeCoreAsync(connectionString, schema, null, null, cancellationToken);

    private static async Task MaterializeCoreAsync(
        string connectionString,
        RelationalDiagnosticRecordSchema? schema,
        DiagnosticRecordStreamDefinition? definition,
        DiagnosticRecordPhysicalSchemaState? state,
        CancellationToken cancellationToken)
    {
        schema ??= RelationalDiagnosticRecordSchema.Standard;
        RelationalDiagnosticRecordSchemaValidator.ValidateAndThrow(schema);
        SqliteRelationalSessions.ValidateStatelessConnectionString(connectionString);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var table in schema.Tables)
            await ExecuteAsync(connection, transaction, CreateTableSql(table), cancellationToken);
        foreach (var index in schema.Indexes)
            await ExecuteAsync(connection, transaction, CreateIndexSql(index), cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT OR IGNORE INTO {RelationalDiagnosticRecordSchema.ProviderStateTable} (id, clock_high_water_ticks) VALUES (1, 0);",
            cancellationToken);
        if (definition is not null)
            await EnsureDefinitionAsync(connection, transaction, definition, state!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordPhysicalSchemaState state,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {RelationalDiagnosticRecordSchema.DefinitionsTable} (stream_id, schema_version, definition_fingerprint, algorithm_manifest, algorithm_manifest_fingerprint, canonical_definition) VALUES (@stream, @version, @fingerprint, @algorithms, @algorithmFingerprint, @canonical);";
            insert.Parameters.AddWithValue("@stream", definition.Stream.Value);
            insert.Parameters.AddWithValue("@version", definition.SchemaVersion);
            insert.Parameters.AddWithValue("@fingerprint", state.DefinitionFingerprint);
            insert.Parameters.AddWithValue("@algorithms", state.ComparisonAlgorithmManifest);
            insert.Parameters.AddWithValue("@algorithmFingerprint", state.ComparisonAlgorithmManifestFingerprint);
            insert.Parameters.AddWithValue("@canonical", state.CanonicalDefinition);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"SELECT definition_fingerprint, algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        read.Parameters.AddWithValue("@stream", definition.Stream.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !StringComparer.Ordinal.Equals(reader.GetString(0), state.DefinitionFingerprint) ||
            !StringComparer.Ordinal.Equals(reader.GetString(1), state.ComparisonAlgorithmManifestFingerprint))
            throw new InvalidOperationException($"SQLite diagnostic stream '{definition.Stream.Value}' has an incompatible persisted definition or comparison-key algorithm state.");
    }

    private static string CreateTableSql(RelationalDiagnosticTableDefinition table)
    {
        var body = new List<string>(table.Columns.Count + 1);
        body.AddRange(table.Columns.Select(column =>
            $"{column.Name} {(column.Type == RelationalDiagnosticColumnType.Int64 ? "INTEGER" : "TEXT")}" +
            (column.UsesBinaryTextSemantics ? " COLLATE BINARY" : "") +
            (column.IsNullable ? " NULL" : " NOT NULL")));
        body.Add($"PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})");
        return $"CREATE TABLE IF NOT EXISTS {table.Name} ({string.Join(", ", body)});";
    }

    private static string CreateIndexSql(RelationalDiagnosticIndexDefinition index) =>
        $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {index.Name} ON {index.Table} ({string.Join(", ", index.Columns)});";

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
