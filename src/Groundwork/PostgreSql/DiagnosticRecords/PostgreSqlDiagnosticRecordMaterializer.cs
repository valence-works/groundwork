using Groundwork.DiagnosticRecords.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.DiagnosticRecords;

public static class PostgreSqlDiagnosticRecordMaterializer
{
    public static async Task MaterializeAsync(
        string connectionString,
        RelationalDiagnosticRecordSchema? schema = null,
        CancellationToken cancellationToken = default)
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
        await transaction.CommitAsync(cancellationToken);
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
