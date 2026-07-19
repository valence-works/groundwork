using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteConnectionPragmaTests
{
    [Fact]
    public async Task FactoryConnectionOpensFileDatabaseInWalMode()
    {
        using var database = new TempDatabase();
        await using var connection = SqliteConnectionFactory.Create(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal("wal", await QueryScalarAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task WalModePersistsOnDiskAfterProviderOpen()
    {
        using var database = new TempDatabase();
        await using (var connection = SqliteConnectionFactory.Create(database.ConnectionString))
        {
            await connection.OpenAsync();
        }

        // A plain connection that applies no pragmas must still observe the persisted WAL journal.
        await using var plain = new SqliteConnection(database.ConnectionString);
        await plain.OpenAsync();
        Assert.Equal("wal", await QueryScalarAsync(plain, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task FactoryConnectionAppliesPerConnectionPragmas()
    {
        using var database = new TempDatabase();
        await using var connection = SqliteConnectionFactory.Create(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal("5000", await QueryScalarAsync(connection, "PRAGMA busy_timeout;"));
        // synchronous=NORMAL reports as 1.
        Assert.Equal("1", await QueryScalarAsync(connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public async Task FactoryHonoursCustomPragmaOptions()
    {
        using var database = new TempDatabase();
        var options = new SqliteConnectionPragmaOptions
        {
            WriteAheadLogging = false,
            Synchronous = SqliteSynchronousMode.Full,
            BusyTimeout = TimeSpan.FromMilliseconds(1234)
        };
        await using var connection = SqliteConnectionFactory.Create(database.ConnectionString, options);
        await connection.OpenAsync();

        Assert.NotEqual("wal", await QueryScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("1234", await QueryScalarAsync(connection, "PRAGMA busy_timeout;"));
        Assert.Equal("2", await QueryScalarAsync(connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public async Task InMemoryConnectionSkipsWalButAppliesBusyTimeout()
    {
        await using var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        await connection.OpenAsync();

        // In-memory databases report the "memory" journal; WAL must not be forced on them.
        Assert.Equal("memory", await QueryScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("5000", await QueryScalarAsync(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task DocumentStoreFactoryLeavesFileDatabaseInWalMode()
    {
        using var database = new TempDatabase();
        var manifest = ClosedQueryManifests.WidgetManifest();

        var store = await SqliteDocumentStoreFactory.CreateAsync(
            database.ConnectionString,
            manifest,
            ClosedQueryManifests.Provider,
            DocumentStoreAccess.Global);

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "widget", "w1", "1.0.0", """{"name":"w1","category":"tools","sortKey":"001"}"""));
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);

        await using var probe = new SqliteConnection(database.ConnectionString);
        await probe.OpenAsync();
        Assert.Equal("wal", await QueryScalarAsync(probe, "PRAGMA journal_mode;"));
    }

    [Theory]
    [InlineData("Data Source=app.db", true)]
    [InlineData("Data Source=:memory:", false)]
    [InlineData("Data Source=app.db;Mode=Memory", false)]
    public void BuildStatementsEmitsWalOnlyForWritableFileDatabases(string connectionString, bool expectWal)
    {
        var statements = SqliteConnectionPragmas.BuildStatements(
            new SqliteConnectionStringBuilder(connectionString),
            SqliteConnectionPragmaOptions.Default);

        Assert.Equal(expectWal, statements.Contains("PRAGMA journal_mode=WAL;"));
        Assert.Contains("PRAGMA busy_timeout=5000;", statements);
        Assert.Contains("PRAGMA synchronous=NORMAL;", statements);
    }

    [Fact]
    public void BuildStatementsForReadOnlyConnectionOnlyAppliesBusyTimeout()
    {
        var builder = new SqliteConnectionStringBuilder("Data Source=app.db")
        {
            Mode = SqliteOpenMode.ReadOnly
        };

        var statements = SqliteConnectionPragmas.BuildStatements(builder, SqliteConnectionPragmaOptions.Default);

        Assert.Equal(["PRAGMA busy_timeout=5000;"], statements);
    }

    [Fact]
    public void BuildStatementsOmitsBusyTimeoutWhenNonPositive()
    {
        var statements = SqliteConnectionPragmas.BuildStatements(
            new SqliteConnectionStringBuilder("Data Source=app.db"),
            new SqliteConnectionPragmaOptions { BusyTimeout = TimeSpan.Zero });

        Assert.DoesNotContain(statements, statement => statement.Contains("busy_timeout"));
        Assert.Contains("PRAGMA journal_mode=WAL;", statements);
    }

    private static async Task<string?> QueryScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gw-pragma-{Guid.NewGuid():N}.db");

        public string Path { get; }

        public string ConnectionString => $"Data Source={Path}";

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var file = Path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
    }
}
