using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.DiagnosticRecords;

public static class PostgreSqlDiagnosticRecordMaterializer
{
    internal static async Task AdmitAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        try
        {
            if (await RelationalDiagnosticRecordAdmissionState.ValidateIfPresentAsync(
                    () => new NpgsqlConnection(connectionString),
                    definition,
                    "PostgreSQL",
                    cancellationToken))
                return;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
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
        PostgreSqlDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        schema ??= RelationalDiagnosticRecordSchema.Standard;
        RelationalDiagnosticRecordSchemaValidator.ValidateAndThrow(schema);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtext('groundwork:diagnostic-record-schema:v1'));",
            cancellationToken);
        foreach (var table in schema.Tables)
            await ExecuteAsync(connection, transaction, CreateTableSql(table), cancellationToken);
        foreach (var index in schema.Indexes)
            await ExecuteAsync(connection, transaction, CreateIndexSql(index), cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {RelationalDiagnosticRecordSchema.ProviderStateTable} (id, clock_high_water_ticks) VALUES (1, 0) ON CONFLICT (id) DO NOTHING;",
            cancellationToken);
        if (definition is not null)
            await EnsureDefinitionAsync(connection, transaction, definition, state!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordPhysicalSchemaState state,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {RelationalDiagnosticRecordSchema.DefinitionsTable} (stream_id, schema_version, definition_fingerprint, algorithm_manifest, algorithm_manifest_fingerprint, canonical_definition) VALUES (@stream, @version, @fingerprint, @algorithms, @algorithmFingerprint, @canonical) ON CONFLICT (stream_id) DO NOTHING;";
            insert.Parameters.AddWithValue("stream", definition.Stream.Value);
            insert.Parameters.AddWithValue("version", definition.SchemaVersion);
            insert.Parameters.AddWithValue("fingerprint", state.DefinitionFingerprint);
            insert.Parameters.AddWithValue("algorithms", state.ComparisonAlgorithmManifest);
            insert.Parameters.AddWithValue("algorithmFingerprint", state.ComparisonAlgorithmManifestFingerprint);
            insert.Parameters.AddWithValue("canonical", state.CanonicalDefinition);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"SELECT definition_fingerprint, algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        read.Parameters.AddWithValue("stream", definition.Stream.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !StringComparer.Ordinal.Equals(reader.GetString(0), state.DefinitionFingerprint) ||
            !StringComparer.Ordinal.Equals(reader.GetString(1), state.ComparisonAlgorithmManifestFingerprint))
            throw new InvalidOperationException($"PostgreSQL diagnostic stream '{definition.Stream.Value}' has an incompatible persisted definition or comparison-key algorithm state.");
    }

    private static string CreateTableSql(RelationalDiagnosticTableDefinition table)
    {
        var body = table.Columns.Select(column =>
            $"{column.Name} {(column.Type == RelationalDiagnosticColumnType.Int64 ? "BIGINT" : "TEXT")}" +
            (column.Type == RelationalDiagnosticColumnType.Text && column.UsesBinaryTextSemantics ? " COLLATE \"C\"" : "") +
            (column.IsNullable ? " NULL" : " NOT NULL")).ToList();
        body.Add($"PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})");
        return $"CREATE TABLE IF NOT EXISTS {table.Name} ({string.Join(", ", body)});";
    }

    private static string CreateIndexSql(RelationalDiagnosticIndexDefinition index)
    {
        var isLatest = index.Name == "ix_groundwork_diagnostic_fields_scope_latest";
        var columns = isLatest ? index.Columns.Where(column => column != "value_ordinal") : index.Columns;
        return $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {index.Name} ON {index.Table} ({string.Join(", ", columns)}){(isLatest ? " WHERE value_ordinal = 0" : "")};";
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
