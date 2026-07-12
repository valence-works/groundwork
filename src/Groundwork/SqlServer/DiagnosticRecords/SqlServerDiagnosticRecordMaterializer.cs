using Groundwork.DiagnosticRecords.Relational;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.DiagnosticRecords;

public static class SqlServerDiagnosticRecordMaterializer
{
    private const string BinaryUtf8Collation = "Latin1_General_100_BIN2_UTF8";

    public static async Task MaterializeAsync(
        string connectionString,
        RelationalDiagnosticRecordSchema? schema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        schema ??= RelationalDiagnosticRecordSchema.Standard;
        RelationalDiagnosticRecordSchemaValidator.ValidateAndThrow(schema);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureReadCommittedSnapshotAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "DECLARE @lock_result INT; EXEC @lock_result = sp_getapplock @Resource = N'groundwork:diagnostic-record-schema:v1', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000; IF @lock_result < 0 THROW 51000, 'Could not acquire the diagnostic-record schema materialization lock.', 1;",
            cancellationToken);
        foreach (var table in schema.Tables)
            await ExecuteAsync(connection, transaction, CreateTableSql(table), cancellationToken);
        foreach (var index in schema.Indexes)
            await ExecuteAsync(connection, transaction, CreateIndexSql(index), cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            $"IF NOT EXISTS (SELECT 1 FROM {RelationalDiagnosticRecordSchema.ProviderStateTable} WHERE id = 1) INSERT INTO {RelationalDiagnosticRecordSchema.ProviderStateTable} (id, clock_high_water_ticks) VALUES (1, 0);",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string CreateTableSql(RelationalDiagnosticTableDefinition table)
    {
        var body = table.Columns.Select(ColumnSql).ToList();
        body.Add($"CONSTRAINT [pk_{table.Name}] PRIMARY KEY NONCLUSTERED ({string.Join(", ", table.PrimaryKey.Select(Quote))})");
        return $"IF OBJECT_ID(N'{table.Name}', N'U') IS NULL CREATE TABLE {table.Name} ({string.Join(", ", body)});";
    }

    private static string ColumnSql(RelationalDiagnosticColumnDefinition column)
    {
        var type = column.Type == RelationalDiagnosticColumnType.Int64
            ? "BIGINT"
            : TextType(column.Name);
        var collation = column.Type == RelationalDiagnosticColumnType.Text && column.UsesBinaryTextSemantics
            ? $" COLLATE {BinaryUtf8Collation}"
            : "";
        return $"{Quote(column.Name)} {type}{collation}{(column.IsNullable ? " NULL" : " NOT NULL")}";
    }

    private static bool IsLargeText(string column) => column is "payload_json" or "result_json" or "canonical_value";

    private static string TextType(string column) => column switch
    {
        _ when IsLargeText(column) => "VARCHAR(MAX)",
        "comparison_key" => "VARCHAR(512)",
        "record_id" => "VARCHAR(128)",
        "tenant_id" or "scope_id" or "stream_id" or "field_name" or "nonce" => "VARCHAR(64)",
        _ => "VARCHAR(256)"
    };

    private static string CreateIndexSql(RelationalDiagnosticIndexDefinition index)
    {
        var isLatest = index.Name == "ix_groundwork_diagnostic_fields_scope_latest";
        var columns = isLatest ? index.Columns.Where(column => column != "value_ordinal") : index.Columns;
        return $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index.Name}' AND object_id = OBJECT_ID(N'{index.Table}')) CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX [{index.Name}] ON {index.Table} ({string.Join(", ", columns.Select(Quote))}){(isLatest ? " WHERE [value_ordinal] = 0" : "")};";
    }

    private static string Quote(string identifier) => $"[{identifier}]";

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReadCommittedSnapshotAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();";
        var enabled = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!enabled)
        {
            throw new InvalidOperationException(
                "SQL Server diagnostic records require READ_COMMITTED_SNAPSHOT ON so inspection can observe the durable snapshot while retention is staged. Configure the database before materialization.");
        }
    }
}
