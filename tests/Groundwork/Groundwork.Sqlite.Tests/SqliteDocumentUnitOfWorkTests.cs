using System.Text.Json;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteDocumentUnitOfWorkTests
{
    private static readonly DocumentCommitScope Scope = DocumentCommitScope.Of("widget");

    [Fact]
    public async Task RelationalStoreDeclaresCrossUnitAtomicBoundary()
    {
        await using var harness = await TxHarness.Create();
        Assert.Equal(TransactionBoundary.CrossUnitAtomic, harness.Store.TransactionBoundary);
    }

    [Fact]
    public async Task CommitPersistsAllStagedWrites()
    {
        await using var harness = await TxHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.SaveAsync(Save("w1", "tools"));
            await unitOfWork.SaveAsync(Save("w2", "gadgets"));
            await unitOfWork.CommitAsync();
        }

        Assert.NotNull(await harness.Store.LoadAsync("widget", "w1"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task RollbackDiscardsAllStagedWrites()
    {
        await using var harness = await TxHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.SaveAsync(Save("w1", "tools"));
            await unitOfWork.SaveAsync(Save("w2", "gadgets"));
            await unitOfWork.RollbackAsync();
        }

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
        Assert.Null(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task DisposeWithoutCommitRollsBack()
    {
        await using var harness = await TxHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.SaveAsync(Save("w1", "tools"));
            // no commit -> dispose rolls back
        }

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
    }

    [Fact]
    public async Task LoadWithinUnitOfWorkSeesStagedWrite()
    {
        await using var harness = await TxHarness.Create();

        await using var unitOfWork = await harness.Store.BeginAsync(Scope);
        await unitOfWork.SaveAsync(Save("w1", "tools"));

        var staged = await unitOfWork.LoadAsync("widget", "w1");
        Assert.NotNull(staged);
        Assert.Equal(1, staged!.Version);

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task CallerRollsBackOnNonSuccessForAllOrNothing()
    {
        await using var harness = await TxHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            var first = await unitOfWork.SaveAsync(Save("w1", "tools"));
            Assert.Equal(DocumentStoreWriteStatus.Saved, first.Status);

            // Expected-version mismatch against a non-existent document => not a success.
            var conflict = await unitOfWork.SaveAsync(new SaveDocumentRequest("widget", "w2", "1.0.0", """{"category":"gadgets"}""", ExpectedVersion: 7));
            Assert.Equal(DocumentStoreWriteStatus.NotFound, conflict.Status);

            await unitOfWork.RollbackAsync();
        }

        // Because the caller rolled back, the earlier successful save is also discarded.
        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
    }

    [Fact]
    public async Task ExpectedVersionZeroWithinUnitOfWorkCreatesOnly()
    {
        await using var harness = await TxHarness.Create();

        // Create-only inside a unit of work: expected version 0 against an absent document inserts version 1.
        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            var created = await unitOfWork.SaveAsync(new SaveDocumentRequest(
                "widget", "w1", "1.0.0", """{"category":"tools"}""", ExpectedVersion: 0));

            Assert.Equal(DocumentStoreWriteStatus.Saved, created.Status);
            Assert.Equal(1, created.Document!.Version);

            await unitOfWork.CommitAsync();
        }

        Assert.NotNull(await harness.Store.LoadAsync("widget", "w1"));

        // Create-only against the now-existing document is refused; the committed document is untouched.
        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            var refused = await unitOfWork.SaveAsync(new SaveDocumentRequest(
                "widget", "w1", "1.0.0", """{"category":"clobber"}""", ExpectedVersion: 0));

            Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, refused.Status);

            await unitOfWork.RollbackAsync();
        }

        var loaded = await harness.Store.LoadAsync("widget", "w1");
        Assert.Equal(1, loaded!.Version);
        using var content = JsonDocument.Parse(loaded.ContentJson);
        Assert.Equal("tools", content.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public async Task OperationsAfterCompletionThrow()
    {
        await using var harness = await TxHarness.Create();

        var unitOfWork = await harness.Store.BeginAsync(Scope);
        await unitOfWork.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.SaveAsync(Save("w1", "tools")));
        await unitOfWork.DisposeAsync();
    }

    [Fact]
    public async Task StoreUsableAfterUnitOfWorkReleasesConnection()
    {
        await using var harness = await TxHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.SaveAsync(Save("w1", "tools"));
            await unitOfWork.CommitAsync();
        }

        // The connection gate must be released so ordinary store operations work afterwards.
        await harness.Store.SaveAsync(Save("w2", "gadgets"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task SaveAllCommitsWhenAllSavesSucceed()
    {
        await using var harness = await TxHarness.Create();

        await harness.Store.SaveAllAsync(Scope, [Save("w1", "tools"), Save("w2", "gadgets")]);

        Assert.NotNull(await harness.Store.LoadAsync("widget", "w1"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task SaveAllRollsBackWhenStagedSaveReturnsNonSuccess()
    {
        await using var harness = await TxHarness.Create();

        var exception = await Assert.ThrowsAsync<DocumentAtomicWriteException>(() =>
            harness.Store.SaveAllAsync(
                Scope,
                [
                    Save("w1", "tools"),
                    new SaveDocumentRequest("widget", "w2", "1.0.0", """{"category":"gadgets"}""", ExpectedVersion: 7)
                ]));

        Assert.Equal(DocumentWriteOperationKind.Save, exception.Operation);
        Assert.Equal("widget", exception.DocumentKind);
        Assert.Equal("w2", exception.Id);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, exception.Status);
        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
        Assert.Null(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task WriteAllSupportsMixedSaveAndDelete()
    {
        await using var harness = await TxHarness.Create();
        await harness.Store.SaveAsync(Save("w1", "tools"));

        await harness.Store.WriteAllAsync(
            Scope,
            [
                DocumentWriteOperation.Delete(new DeleteDocumentRequest("widget", "w1")),
                DocumentWriteOperation.Save(Save("w2", "gadgets"))
            ]);

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task WriteAllRollsBackWhenStagedDeleteReturnsNonSuccess()
    {
        await using var harness = await TxHarness.Create();
        await harness.Store.SaveAsync(Save("w1", "tools"));

        var exception = await Assert.ThrowsAsync<DocumentAtomicWriteException>(() =>
            harness.Store.WriteAllAsync(
                Scope,
                [
                    DocumentWriteOperation.Save(Save("w2", "gadgets")),
                    DocumentWriteOperation.Delete(new DeleteDocumentRequest("widget", "missing"))
                ]));

        Assert.Equal(DocumentWriteOperationKind.Delete, exception.Operation);
        Assert.Equal("missing", exception.Id);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, exception.Status);
        Assert.NotNull(await harness.Store.LoadAsync("widget", "w1"));
        Assert.Null(await harness.Store.LoadAsync("widget", "w2"));
    }

    [Fact]
    public async Task WriteAllRollsBackWhenStagedOperationThrows()
    {
        await using var harness = await TxHarness.Create();

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            harness.Store.SaveAllAsync(
                Scope,
                [
                    Save("w1", "tools"),
                    new SaveDocumentRequest("widget", "broken", "1.0.0", "{")
                ]));

        Assert.Null(await harness.Store.LoadAsync("widget", "w1"));
        Assert.Null(await harness.Store.LoadAsync("widget", "broken"));
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
