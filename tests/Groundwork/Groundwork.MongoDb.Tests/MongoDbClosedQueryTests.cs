using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbClosedQueryTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task ClosedQueryHonoursClosedContractServerSide()
    {
        await using var harness = await Harness.CreateWidgets(container.GetConnectionString());
        var store = harness.Store;

        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "w1", "1.0.0", """{"key":"Alpha Widget","category":"tools","sort":"003"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "w2", "1.0.0", """{"key":"Beta Widget","category":"tools","sort":"001"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "w3", "1.0.0", """{"key":"Gamma Gadget","category":"gadgets","sort":"002"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "w4", "1.0.0", """{"key":"alpha gadget","category":"gadgets","sort":"004"}"""));

        // In + empty In.
        Assert.Equal(2, (await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.Of(QueryComparison.In("by-category", ["tools"]))]))).TotalCount);
        Assert.Equal(0, (await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.Of(QueryComparison.In("by-category", Array.Empty<string>()))]))).TotalCount);

        // Contains case-insensitive.
        var contains = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.Of(QueryComparison.Contains("by-key", "ALPHA"))]));
        Assert.Equal(new[] { "w1", "w4" }, contains.Documents.Select(d => d.Id).OrderBy(x => x));

        // OR within clause; AND across clauses.
        var orAnd = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [
                QueryClause.Of(QueryComparison.In("by-category", ["tools", "gadgets"])),
                QueryClause.AnyOf(QueryComparison.Contains("by-key", "Gamma"), QueryComparison.Contains("by-key", "Beta"))
            ]));
        Assert.Equal(new[] { "w2", "w3" }, orAnd.Documents.Select(d => d.Id).OrderBy(x => x));

        // Constant-false clause.
        Assert.Equal(0, (await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.MatchNone]))).TotalCount);

        // Ordering + offset paging + total count.
        var page = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", order: new QueryOrder("by-sort"), skip: 1, take: 2));
        Assert.Equal(new[] { "w3", "w1" }, page.Documents.Select(d => d.Id));
        Assert.Equal(4, page.TotalCount);

        var descending = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument", order: new QueryOrder("by-sort", Descending: true)));
        Assert.Equal(new[] { "w4", "w1", "w3", "w2" }, descending.Documents.Select(d => d.Id));

        // FirstOrDefault honours ordering; Any existence.
        Assert.Equal("w2", (await store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "configurationDocument", order: new QueryOrder("by-sort"))))!.Id);
        Assert.True(await store.AnyAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.Of(QueryComparison.Equal("by-category", "tools"))])));
        Assert.False(await store.AnyAsync(new PortableDocumentQuery(
            "configurationDocument", [QueryClause.Of(QueryComparison.Equal("by-category", "none"))])));
    }

    [Fact]
    public async Task TenantAwareFiltersAndAgnosticBypasses()
    {
        await using var harness = await Harness.CreateTenant(container.GetConnectionString(), "tenant-a");
        var store = harness.Store;

        await store.SaveAsync(new SaveDocumentRequest("scopedDocument", "s1", "1.0.0", """{"tenantId":"tenant-a","name":"A1"}"""));
        await store.SaveAsync(new SaveDocumentRequest("scopedDocument", "s2", "1.0.0", """{"tenantId":"tenant-b","name":"B1"}"""));
        await store.SaveAsync(new SaveDocumentRequest("scopedDocument", "s3", "1.0.0", """{"tenantId":"tenant-a","name":"A2"}"""));

        var aware = await store.QueryAsync(new PortableDocumentQuery("scopedDocument"));
        Assert.Equal(new[] { "s1", "s3" }, aware.Documents.Select(d => d.Id).OrderBy(x => x));
        Assert.Equal(2, aware.TotalCount);

        var agnostic = await store.QueryAsync(new PortableDocumentQuery(
            "scopedDocument", tenantScope: QueryTenantScope.TenantAgnostic));
        Assert.Equal(3, agnostic.TotalCount);
    }

    [Fact]
    public async Task DocumentTransactionCommitsAtomicallyOrFailsLoudlyOnStandalone()
    {
        await using var harness = await Harness.CreateWidgets(container.GetConnectionString());
        var store = harness.Store;

        IDocumentTransaction transaction;
        try
        {
            transaction = await store.BeginTransactionAsync();
        }
        catch (UnsupportedDocumentTransactionException exception)
        {
            // On a standalone deployment the contract is a loud failure, not silent non-atomic writes.
            Assert.Contains("replica set", exception.Reason);
            return;
        }

        await using (transaction)
        {
            await transaction.SaveAsync(new SaveDocumentRequest("configurationDocument", "t1", "1.0.0", """{"key":"K1","category":"tools","sort":"001"}"""));
            await transaction.SaveAsync(new SaveDocumentRequest("configurationDocument", "t2", "1.0.0", """{"key":"K2","category":"tools","sort":"002"}"""));
            await transaction.CommitAsync();
        }

        Assert.NotNull(await store.LoadAsync("configurationDocument", "t1"));
        Assert.NotNull(await store.LoadAsync("configurationDocument", "t2"));

        await using (var rollback = await store.BeginTransactionAsync())
        {
            await rollback.SaveAsync(new SaveDocumentRequest("configurationDocument", "t3", "1.0.0", """{"key":"K3","category":"tools","sort":"003"}"""));
            await rollback.RollbackAsync();
        }

        Assert.Null(await store.LoadAsync("configurationDocument", "t3"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(IMongoClient client, IMongoDatabase database, MongoDbDocumentStore store)
        {
            this.client = client;
            this.database = database;
            Store = store;
        }

        private readonly IMongoClient client;
        private readonly IMongoDatabase database;
        public MongoDbDocumentStore Store { get; }

        public static async Task<Harness> CreateWidgets(string connectionString)
        {
            var (client, database, manifest) = await Materialize(connectionString, MongoDbTestManifests.MetadataManifest());
            return new Harness(client, database, new MongoDbDocumentStore(database, manifest));
        }

        public static async Task<Harness> CreateTenant(string connectionString, string ambientTenantId)
        {
            var (client, database, manifest) = await Materialize(connectionString, MongoDbTestManifests.TenantManifest());
            return new Harness(client, database, new MongoDbDocumentStore(database, manifest, () => ambientTenantId));
        }

        private static async Task<(IMongoClient, IMongoDatabase, Groundwork.Core.Manifests.StorageManifest)> Materialize(
            string connectionString, Groundwork.Core.Manifests.StorageManifest manifest)
        {
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);
            return (client, database, manifest);
        }

        public async ValueTask DisposeAsync() =>
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
    }
}
