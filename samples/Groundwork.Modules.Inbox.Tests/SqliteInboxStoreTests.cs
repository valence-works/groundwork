using Groundwork.Modules.Inbox;
using Groundwork.Modules.Inbox.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Modules.Inbox.Tests;

/// <summary>
/// Proves the Inbox SQLite store provides idempotent (at-most-once) message admission, built on the
/// reusable relational toolkit.
/// </summary>
public sealed class SqliteInboxStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    private async Task<SqliteInboxStore> CreateStoreAsync()
    {
        await connection.OpenAsync();
        await new SqliteInboxMaterializer(connection).MaterializeAsync();
        return new SqliteInboxStore(connection);
    }

    [Fact]
    public async Task FirstAdmitSucceedsAndRedeliveryIsDuplicate()
    {
        var inbox = await CreateStoreAsync();

        Assert.Equal(InboxAdmission.Admitted, await inbox.TryAdmitAsync("billing", "evt-1"));
        Assert.Equal(InboxAdmission.Duplicate, await inbox.TryAdmitAsync("billing", "evt-1"));
        Assert.Equal(InboxAdmission.Duplicate, await inbox.TryAdmitAsync("billing", "evt-1"));
    }

    [Fact]
    public async Task DifferentConsumersAndKeysAreIndependent()
    {
        var inbox = await CreateStoreAsync();

        Assert.Equal(InboxAdmission.Admitted, await inbox.TryAdmitAsync("billing", "evt-1"));
        Assert.Equal(InboxAdmission.Admitted, await inbox.TryAdmitAsync("audit", "evt-1"));
        Assert.Equal(InboxAdmission.Admitted, await inbox.TryAdmitAsync("billing", "evt-2"));
    }

    [Fact]
    public async Task ProcessedStateIsTracked()
    {
        var inbox = await CreateStoreAsync();

        await inbox.TryAdmitAsync("billing", "evt-1");
        Assert.False(await inbox.IsProcessedAsync("billing", "evt-1"));

        await inbox.MarkProcessedAsync("billing", "evt-1");
        Assert.True(await inbox.IsProcessedAsync("billing", "evt-1"));
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
