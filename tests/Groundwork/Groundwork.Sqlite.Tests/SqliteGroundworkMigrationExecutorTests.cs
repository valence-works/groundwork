using Groundwork.Core.Capabilities;
using Groundwork.Core.Migrations;
using Groundwork.Sqlite.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteGroundworkMigrationExecutorTests
{
    [Fact]
    public async Task RunnerAppliesAndRecordsSqliteMigrationsOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var executor = new SqliteGroundworkMigrationExecutor(connection, Provider);
        var runner = new GroundworkMigrationRunner(executor);
        var migrations = new[]
        {
            new GroundworkMigration(
                "create-widget-table",
                1,
                "Create widget table",
                [GroundworkMigrationOperation.ProviderSql("create-table", "CREATE TABLE widgets (id TEXT PRIMARY KEY);")]),
            new GroundworkMigration(
                "insert-widget",
                2,
                "Insert widget row",
                [GroundworkMigrationOperation.ProviderSql("insert-row", "INSERT INTO widgets (id) VALUES ('one');")])
        };

        var first = await runner.RunAsync(migrations);
        var second = await runner.RunAsync(migrations);

        Assert.False(first.HasErrors);
        Assert.Equal(["create-widget-table", "insert-widget"], first.Applied.Select(record => record.Identity));
        Assert.Empty(second.Applied);
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM widgets;"));
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM groundwork_migration_history;"));
    }

    [Fact]
    public async Task FailedSqliteMigrationIsNotRecorded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var executor = new SqliteGroundworkMigrationExecutor(connection, Provider);
        var runner = new GroundworkMigrationRunner(executor);

        await Assert.ThrowsAsync<SqliteException>(() => runner.RunAsync(
            [
                new GroundworkMigration(
                    "bad-sql",
                    1,
                    "Bad SQL",
                    [GroundworkMigrationOperation.ProviderSql("bad", "CREATE TABLE broken (id TEXT PRIMARY KEY); INSERT INTO missing_table VALUES (1);")])
            ]));

        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM groundwork_migration_history;"));
    }

    [Fact]
    public async Task DryRunDoesNotCreateSqliteLedger()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var executor = new SqliteGroundworkMigrationExecutor(connection, Provider);
        var runner = new GroundworkMigrationRunner(executor);

        var result = await runner.RunAsync(
            [
                new GroundworkMigration(
                    "create-widget-table",
                    1,
                    "Create widget table",
                    [GroundworkMigrationOperation.ProviderSql("create-table", "CREATE TABLE widgets (id TEXT PRIMARY KEY);")])
            ],
            new GroundworkMigrationExecutionOptions(DryRun: true));

        Assert.False(result.HasErrors);
        Assert.Single(result.Pending);
        Assert.False(await TableExistsAsync(connection, "groundwork_migration_history"));
        Assert.False(await TableExistsAsync(connection, "widgets"));
    }

    [Fact]
    public async Task ExecutorClaimsMigrationBeforeExecutingOperations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var executor = new SqliteGroundworkMigrationExecutor(connection, Provider);
        var migration = new GroundworkMigration(
            "create-widget-table",
            1,
            "Create widget table",
            [GroundworkMigrationOperation.ProviderSql("create-table", "CREATE TABLE widgets (id TEXT PRIMARY KEY);")]);

        await executor.EnsureLedgerAsync();
        var firstApplied = await executor.ExecuteAsync(migration, DateTimeOffset.UtcNow);
        var secondApplied = await executor.ExecuteAsync(migration, DateTimeOffset.UtcNow);

        Assert.True(firstApplied);
        Assert.False(secondApplied);
        Assert.True(await TableExistsAsync(connection, "widgets"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM groundwork_migration_history;"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static ProviderIdentity Provider { get; } = new("groundwork-sqlite", "1.0.0");

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
