using System.Text.Json;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class RelationalProviderContractTests
{
    [Fact]
    public async Task MaterializeCreatesSchemaHistoryIdempotently()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.MaterializeAsync();
        await harness.MaterializeAsync();

        Assert.Equal(1, await harness.CountSchemaHistoryRowsAsync());
    }

    [Fact]
    public async Task SaveLoadUpdateQueryAndDeleteMaintainIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var store = harness.Store;
        var id = NewId();
        var firstKey = NewValue("alpha");
        var secondKey = NewValue("beta");

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{firstKey}}","category":"system","value":1}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);

        var loaded = await store.LoadAsync("configurationDocument", id);
        Assert.NotNull(loaded);
        using var loadedContent = JsonDocument.Parse(loaded.ContentJson);
        Assert.Equal(firstKey, loadedContent.RootElement.GetProperty("key").GetString());

        var byKey = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", firstKey));
        Assert.Single(byKey);

        var updated = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{secondKey}}","category":"application","value":2}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(2, updated.Document!.Version);

        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", firstKey)));
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
        Assert.Contains(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "application")), document => document.Id == id);

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 2));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Null(await store.LoadAsync("configurationDocument", id));
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
    }

    [Fact]
    public async Task UndeclaredIndexQueryFailsClearly()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();

        var exception = await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "missing-index", NewValue("alpha"))));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("missing-index", exception.IndexName);
    }

    [Fact]
    public async Task UniqueIndexesAreEnforcedByProvider()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var key = NewValue("unique");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}"""));

        var duplicate = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, duplicate.Status);
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotUpdateDocumentOrIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var firstKey = NewValue("alpha");
        var secondKey = NewValue("beta");
        var staleKey = NewValue("gamma");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{firstKey}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{secondKey}}","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{staleKey}}","category":"system"}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", staleKey)));
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotDeleteDocumentOrIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var key = NewValue("beta");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{NewValue("alpha")}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", id));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", key)));
    }

    protected abstract Task<IRelationalProviderHarness> CreateHarnessAsync();

    [Fact]
    public async Task ClosedQueryHonoursClosedContractServerSide()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var store = harness.Store;
        var tag = NewValue("cq");

        var c1 = NewId();
        var c2 = NewId();
        var c3 = NewId();
        var c4 = NewId();

        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c1, "1.0.0", $$"""{"key":"Alpha-{{c1}}","category":"{{tag}}","sort":"001"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c2, "1.0.0", $$"""{"key":"beta-{{c2}}","category":"{{tag}}","sort":"002"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c3, "1.0.0", $$"""{"key":"alpha-{{c3}}","category":"{{tag}}","sort":"003"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c4, "1.0.0", $$"""{"key":"Gamma-{{c4}}","category":"{{tag}}","sort":"004"}"""));

        var tagClause = QueryClause.Of(QueryComparison.Equal("by-category", tag));

        // In: set membership over the category tag isolates this test's documents.
        var inResult = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [QueryClause.Of(QueryComparison.In("by-category", [tag]))]));
        Assert.Equal(4, inResult.TotalCount);

        // Empty In => no match.
        var emptyIn = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [QueryClause.Of(QueryComparison.In("by-category", Array.Empty<string>()))]));
        Assert.Equal(0, emptyIn.TotalCount);

        // Contains is case-insensitive: "ALPHA" matches "Alpha-..." and "alpha-...".
        var contains = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.Of(QueryComparison.Contains("by-key", "ALPHA"))]));
        Assert.Equal(new[] { c1, c3 }.OrderBy(x => x), contains.Documents.Select(d => d.Id).OrderBy(x => x));

        // OR within a clause, AND across clauses.
        var orResult = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [
                tagClause,
                QueryClause.AnyOf(
                    QueryComparison.Contains("by-key", "Gamma"),
                    QueryComparison.Contains("by-key", "beta"))
            ]));
        Assert.Equal(new[] { c2, c4 }.OrderBy(x => x), orResult.Documents.Select(d => d.Id).OrderBy(x => x));

        // Constant-false clause => empty.
        var none = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.MatchNone]));
        Assert.Equal(0, none.TotalCount);

        // Ordering + offset paging + total count.
        var page = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause],
            order: new QueryOrder("by-sort"),
            skip: 1,
            take: 2));
        Assert.Equal(new[] { c2, c3 }, page.Documents.Select(d => d.Id));
        Assert.Equal(4, page.TotalCount);

        var descending = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause],
            order: new QueryOrder("by-sort", Descending: true)));
        Assert.Equal(new[] { c4, c3, c2, c1 }, descending.Documents.Select(d => d.Id));

        // FirstOrDefault honours ordering; Any reports existence.
        var first = await store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "configurationDocument", [tagClause], order: new QueryOrder("by-sort")));
        Assert.Equal(c1, first!.Id);

        Assert.True(await store.AnyAsync(new PortableDocumentQuery("configurationDocument", [tagClause])));
        Assert.False(await store.AnyAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.Of(QueryComparison.Equal("by-key", "no-such-key"))])));
    }

    private static string NewId() => $"doc-{Guid.NewGuid():N}";

    private static string NewValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

public interface IRelationalProviderHarness : IAsyncDisposable
{
    IDocumentStore Store { get; }
    Task MaterializeAsync();
    Task<long> CountSchemaHistoryRowsAsync();
}
