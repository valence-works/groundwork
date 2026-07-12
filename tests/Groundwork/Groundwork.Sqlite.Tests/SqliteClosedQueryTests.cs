using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteClosedQueryTests
{
    [Fact]
    public async Task InMembershipMatchesDeclaredSet()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.In("by-category", ["tools", "gadgets"]))]));

        Assert.Equal(new[] { "w1", "w2", "w3", "w4" }, Ids(result));
        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public async Task EmptyInSetMatchesNothing()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.In("by-category", Array.Empty<string>()))]));

        Assert.Empty(result.Documents);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ContainsIsCaseInsensitiveAndNullFieldSafe()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.Contains("by-name", "alpha"))]));

        // w1 "Alpha Widget" and w4 "alpha gadget" match case-insensitively; w5 (no name) does not throw or match.
        Assert.Equal(new[] { "w1", "w4" }, Ids(result));
    }

    [Fact]
    public async Task ContainsMatchesSubstringRegardlessOfCasing()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.Contains("by-name", "WIDGET"))]));

        Assert.Equal(new[] { "w1", "w2" }, Ids(result));
    }

    [Fact]
    public async Task OrComposesComparisonsWithinAClause()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.AnyOf(
                QueryComparison.Contains("by-name", "Gamma"),
                QueryComparison.Equal("by-category", "tools"))]));

        Assert.Equal(new[] { "w1", "w2", "w3" }, Ids(result));
    }

    [Fact]
    public async Task AndComposesAcrossClauses()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [
                QueryClause.Of(QueryComparison.In("by-category", ["tools", "gadgets"])),
                QueryClause.Of(QueryComparison.Contains("by-name", "alpha"))
            ]));

        Assert.Equal(new[] { "w1", "w4" }, Ids(result));
    }

    [Fact]
    public async Task ConstantFalseClauseMatchesNothing()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [
                QueryClause.Of(QueryComparison.Equal("by-category", "tools")),
                QueryClause.MatchNone
            ]));

        Assert.Empty(result.Documents);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ZeroClausesMatchAll()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery("widget"));

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(5, result.Documents.Count);
    }

    [Fact]
    public async Task EqualWithNullMatchesDocumentsWhoseFieldIsNull()
    {
        await using var harness = await WidgetHarness.Create();

        var nullColor = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.Equal("by-color", null))]));

        Assert.Equal(new[] { "w4" }, Ids(nullColor));

        var redColor = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.Equal("by-color", "red"))]));

        Assert.Equal(new[] { "w1", "w3" }, Ids(redColor));
    }

    [Fact]
    public async Task OrderByAscendingAndDescendingOverStringField()
    {
        await using var harness = await WidgetHarness.Create();

        var ascending = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            order: new QueryOrder("by-sort-key")));

        Assert.Equal(new[] { "w2", "w3", "w1", "w4", "w5" }, OrderedIds(ascending));

        var descending = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            order: new QueryOrder("by-sort-key", Descending: true)));

        Assert.Equal(new[] { "w5", "w4", "w1", "w3", "w2" }, OrderedIds(descending));
    }

    [Fact]
    public async Task OffsetPagingReturnsWindowWithTotalCount()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "widget",
            order: new QueryOrder("by-sort-key"),
            skip: 1,
            take: 2));

        Assert.Equal(new[] { "w3", "w1" }, OrderedIds(result));
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task TakeZeroReturnsEmptyWindowButFullTotalCount()
    {
        await using var harness = await WidgetHarness.Create();

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery("widget", take: 0));

        Assert.Empty(result.Documents);
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task FirstOrDefaultHonoursOrdering()
    {
        await using var harness = await WidgetHarness.Create();

        var first = await harness.Store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "widget",
            order: new QueryOrder("by-sort-key")));
        Assert.Equal("w2", first!.Id);

        var last = await harness.Store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "widget",
            order: new QueryOrder("by-sort-key", Descending: true)));
        Assert.Equal("w5", last!.Id);

        var none = await harness.Store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "widget",
            [QueryClause.Of(QueryComparison.Equal("by-category", "nope"))]));
        Assert.Null(none);
    }

    [Fact]
    public async Task AnyReportsExistence()
    {
        await using var harness = await WidgetHarness.Create();

        Assert.True(await harness.Store.AnyAsync(new PortableDocumentQuery(
            "widget", [QueryClause.Of(QueryComparison.Equal("by-category", "tools"))])));

        Assert.False(await harness.Store.AnyAsync(new PortableDocumentQuery(
            "widget", [QueryClause.Of(QueryComparison.Equal("by-category", "nope"))])));

        Assert.False(await harness.Store.AnyAsync(new PortableDocumentQuery(
            "widget", [QueryClause.Of(QueryComparison.In("by-category", Array.Empty<string>()))])));
    }

    [Fact]
    public async Task UndeclaredOperatorThrows()
    {
        await using var harness = await WidgetHarness.Create();

        await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new PortableDocumentQuery(
                "widget",
                [QueryClause.Of(QueryComparison.Contains("by-color", "red"))])));
    }

    [Fact]
    public async Task OrderingOnNonSortableIndexThrows()
    {
        await using var harness = await WidgetHarness.Create();

        await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new PortableDocumentQuery(
                "widget",
                order: new QueryOrder("by-category"))));
    }

    [Fact]
    public async Task ScopedQueriesUseTheBoundScopeAndPrivilegedSessionCanCrossScopes()
    {
        await using var harness = await TenantHarness.Create("tenant-a");

        await Save(harness.Store, "s1", """{"tenantId":"tenant-a","name":"A1"}""");
        await Save(harness.OtherScopeStore, "s2", """{"tenantId":"tenant-a","name":"B1"}""");
        await Save(harness.Store, "s3", """{"tenantId":"tenant-a","name":"A2"}""");

        var aware = await harness.Store.QueryAsync(new PortableDocumentQuery("scopedDocument"));
        Assert.Equal(new[] { "s1", "s3" }, aware.Documents.Select(d => d.Id).OrderBy(id => id));
        Assert.Equal(2, aware.TotalCount);

        var agnostic = await harness.PrivilegedStore.QueryAsync(new PortableDocumentQuery("scopedDocument"));
        Assert.Equal(3, agnostic.TotalCount);
    }

    [Fact]
    public async Task TenantAwareComposesWithUserClauses()
    {
        await using var harness = await TenantHarness.Create("tenant-a");

        await Save(harness.Store, "s1", """{"tenantId":"tenant-a","name":"Apple"}""");
        await Save(harness.OtherScopeStore, "s2", """{"tenantId":"tenant-a","name":"Apple"}""");
        await Save(harness.Store, "s3", """{"tenantId":"tenant-a","name":"Banana"}""");

        var result = await harness.Store.QueryAsync(new PortableDocumentQuery(
            "scopedDocument",
            [QueryClause.Of(QueryComparison.Contains("by-name", "app"))]));

        Assert.Equal(new[] { "s1" }, result.Documents.Select(d => d.Id));
        Assert.Equal(1, result.TotalCount);
    }

    private static string[] Ids(DocumentQueryResult result) =>
        result.Documents.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private static string[] OrderedIds(DocumentQueryResult result) =>
        result.Documents.Select(document => document.Id).ToArray();

    private static Task Save(IDocumentStore store, string id, string json) =>
        store.SaveAsync(new SaveDocumentRequest(id.StartsWith('w') ? "widget" : "scopedDocument", id, "1.0.0", json));

    private sealed class WidgetHarness : IAsyncDisposable
    {
        private WidgetHarness(SqliteConnection connection, IDocumentStore store)
        {
            this.connection = connection;
            Store = store;
        }

        private readonly SqliteConnection connection;
        public IDocumentStore Store { get; }

        public static async Task<WidgetHarness> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            var manifest = ClosedQueryManifests.WidgetManifest();
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, ClosedQueryManifests.Provider);
            var store = new SqliteDocumentStore(connection, manifest, Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            await store.SaveAsync(new SaveDocumentRequest("widget", "w1", "1.0.0", """{"name":"Alpha Widget","category":"tools","color":"red","sortKey":"003"}"""));
            await store.SaveAsync(new SaveDocumentRequest("widget", "w2", "1.0.0", """{"name":"Beta Widget","category":"tools","color":"blue","sortKey":"001"}"""));
            await store.SaveAsync(new SaveDocumentRequest("widget", "w3", "1.0.0", """{"name":"Gamma Gadget","category":"gadgets","color":"red","sortKey":"002"}"""));
            await store.SaveAsync(new SaveDocumentRequest("widget", "w4", "1.0.0", """{"name":"alpha gadget","category":"gadgets","sortKey":"004"}"""));
            await store.SaveAsync(new SaveDocumentRequest("widget", "w5", "1.0.0", """{"category":"misc","color":"green","sortKey":"005"}"""));

            return new WidgetHarness(connection, store);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class TenantHarness : IAsyncDisposable
    {
        private TenantHarness(
            SqliteConnection connection,
            IDocumentStore store,
            IDocumentStore otherScopeStore,
            IDocumentStore privilegedStore)
        {
            this.connection = connection;
            Store = store;
            OtherScopeStore = otherScopeStore;
            PrivilegedStore = privilegedStore;
        }

        private readonly SqliteConnection connection;
        public IDocumentStore Store { get; }
        public IDocumentStore OtherScopeStore { get; }
        public IDocumentStore PrivilegedStore { get; }

        public static async Task<TenantHarness> Create(string storageScope)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            var manifest = ClosedQueryManifests.TenantManifest();
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, ClosedQueryManifests.Provider);
            var store = new SqliteDocumentStore(
                connection,
                manifest,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Scoped(new Groundwork.Core.Scoping.StorageScope(storageScope)));
            var otherScopeStore = new SqliteDocumentStore(
                connection,
                manifest,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Scoped(new Groundwork.Core.Scoping.StorageScope("tenant-b")));
            var privilegedStore = new SqliteDocumentStore(
                connection,
                manifest,
                Groundwork.Documents.Scoping.DocumentStoreAccess.PrivilegedAcrossScopes(
                    new Groundwork.Documents.Scoping.PrivilegedStorageAccess("closed-query conformance")));
            return new TenantHarness(connection, store, otherScopeStore, privilegedStore);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
