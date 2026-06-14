using Groundwork.Operational;
using Groundwork.Sqlite.Operational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Tests;

internal sealed class OperationalHarness : IAsyncDisposable
{
    private OperationalHarness(SqliteConnection connection, SqliteOperationalStore store)
    {
        Connection = connection;
        Store = store;
    }

    public SqliteConnection Connection { get; }

    public SqliteOperationalStore Store { get; }

    public static async Task<OperationalHarness> Create(IOperationalClock? clock = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await new SqliteOperationalMaterializer(connection).MaterializeAsync();
        return new OperationalHarness(connection, new SqliteOperationalStore(connection, clock));
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
