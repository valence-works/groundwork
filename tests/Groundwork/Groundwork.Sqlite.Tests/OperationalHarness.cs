using Groundwork.Operational;
using Groundwork.Sqlite.Operational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Tests;

internal sealed class OperationalHarness : IAsyncDisposable
{
    private readonly string databasePath;

    private OperationalHarness(string databasePath, SqliteOperationalStore store)
    {
        this.databasePath = databasePath;
        Store = store;
    }

    public SqliteOperationalStore Store { get; }

    public static async Task<OperationalHarness> Create(IOperationalClock? clock = null)
    {
        var databasePath = Path.GetTempFileName();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await new SqliteOperationalMaterializer(connection).MaterializeAsync();
        return new OperationalHarness(databasePath, new SqliteOperationalStore($"Data Source={databasePath}", clock));
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(databasePath);
        return ValueTask.CompletedTask;
    }
}
