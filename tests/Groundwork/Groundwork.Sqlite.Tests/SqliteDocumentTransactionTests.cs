using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteDocumentTransactionTests
{
    [Fact]
    public async Task CommitPersistsAllStagedWrites()
    {
        await using var harness = await TxHarness.Create();

        await using (var tx = await harness.Store.BeginTransactionAsync())
        {
            await tx.SaveAsync(Save("w1", "tools"));
            await tx.SaveAsync(Save("w2", "gadgets"));
            await tx.CommitAsync();
        }

        Assert.NotNull(await harness.Store.LoadAsync("widget", "w1"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task RollbackDiscardsAllStagedWrites()
    {
        await using var harness = await TxHarness.Create();

        await using (var tx = await harness.Store.BeginTransactionAsync())
        {
            await tx.SaveAsync(Save("w1", "tools"));
            await tx.SaveAsync(Save("w2", "gadgets"));
            await tx.RollbackAsync();
        }

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
        Assert.Null(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task DisposeWithoutCommitRollsBack()
    {
        await using var harness = await TxHarness.Create();

        await using (var tx = await harness.Store.BeginTransactionAsync())
        {
            await tx.SaveAsync(Save("w1", "tools"));
            // no commit -> dispose rolls back
        }

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
    }

    [Fact]
    public async Task LoadWithinTransactionSeesStagedWrite()
    {
        await using var harness = await TxHarness.Create();

        await using var tx = await harness.Store.BeginTransactionAsync();
        await tx.SaveAsync(Save("w1", "tools"));

        var staged = await tx.LoadAsync("widget", "w1");
        Assert.NotNull(staged);
        Assert.Equal(1, staged!.Version);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CallerRollsBackOnNonSuccessForAllOrNothing()
    {
        await using var harness = await TxHarness.Create();

        await using (var tx = await harness.Store.BeginTransactionAsync())
        {
            var first = await tx.SaveAsync(Save("w1", "tools"));
            Assert.Equal(DocumentStoreWriteStatus.Saved, first.Status);

            // Expected-version mismatch against a non-existent document => not a success.
            var conflict = await tx.SaveAsync(new SaveDocumentRequest("widget", "w2", "1.0.0", """{"category":"gadgets"}""", ExpectedVersion: 7));
            Assert.Equal(DocumentStoreWriteStatus.NotFound, conflict.Status);

            await tx.RollbackAsync();
        }

        // Because the caller rolled back, the earlier successful save is also discarded.
        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
    }

    [Fact]
    public async Task OperationsAfterCompletionThrow()
    {
        await using var harness = await TxHarness.Create();

        var tx = await harness.Store.BeginTransactionAsync();
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.SaveAsync(Save("w1", "tools")));
        await tx.DisposeAsync();
    }

    [Fact]
    public async Task StoreUsableAfterTransactionReleasesConnection()
    {
        await using var harness = await TxHarness.Create();

        await using (var tx = await harness.Store.BeginTransactionAsync())
        {
            await tx.SaveAsync(Save("w1", "tools"));
            await tx.CommitAsync();
        }

        // The connection gate must be released so ordinary store operations work afterwards.
        await harness.Store.SaveAsync(Save("w2", "gadgets"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    private static SaveDocumentRequest Save(string id, string category) =>
        new("widget", id, "1.0.0", $$"""{"name":"{{id}}","category":"{{category}}","sortKey":"001"}""");

    private sealed class TxHarness : IAsyncDisposable
    {
        private TxHarness(SqliteConnection connection, IDocumentStore store)
        {
            this.connection = connection;
            Store = store;
        }

        private readonly SqliteConnection connection;
        public IDocumentStore Store { get; }

        public static async Task<TxHarness> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            var manifest = ClosedQueryManifests.WidgetManifest();
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, ClosedQueryManifests.Provider);
            return new TxHarness(connection, new SqliteDocumentStore(connection, manifest));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
