using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.DiagnosticRecords;

public static class SqliteDiagnosticRecordMaterializer
{
    public static async Task MaterializeAsync(
        string connectionString,
        RelationalDiagnosticRecordSchema? schema = null,
        CancellationToken cancellationToken = default)
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
        await transaction.CommitAsync(cancellationToken);
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
