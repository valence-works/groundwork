using Groundwork.Core.Manifests;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Proves that MongoDB needs no explicit index-projection backfill: because the provider indexes the
/// document content directly (rather than a separate projection collection), an index added to a unit that
/// already holds documents covers those pre-existing documents implicitly.
/// </summary>
public sealed class MongoDbAddedIndexBackfillTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task AddedIndexCoversPreexistingDocumentsWithoutExplicitBackfill()
    {
        await RunAsync(MongoDbTestManifests.MetadataManifest());
    }

    [Fact]
    public async Task AddedIndexCoversPreexistingDocumentsAcrossManifestVersionBump()
    {
        await RunAsync(MongoDbTestManifests.MetadataManifest() with { Version = new StorageManifestVersion("1.1.0") });
    }

    private async Task RunAsync(StorageManifest withCategory)
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        try
        {
            // Phase 1: materialize a manifest whose unit has no "by-category" index and save documents.
            var withoutCategory = WithoutIndex(withCategory, "by-category");
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(withoutCategory, MongoDbTestManifests.Provider);
            var initialStore = new MongoDbDocumentStore(database, withoutCategory, Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            var systemA = NewId();
            var systemB = NewId();
            var other = NewId();
            await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", systemA, "1.0.0", """{"key":"alpha","category":"system"}"""));
            await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", systemB, "1.0.0", """{"key":"beta","category":"system"}"""));
            await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", other, "1.0.0", """{"key":"gamma","category":"other"}"""));

            // Phase 2: add the "by-category" index (a server-side index over content) to the populated unit.
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(withCategory, MongoDbTestManifests.Provider);
            var store = new MongoDbDocumentStore(database, withCategory, Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            var system = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "system"));
            Assert.Equal(new[] { systemA, systemB }.OrderBy(id => id), system.Select(document => document.Id).OrderBy(id => id));

            var otherResult = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "other"));
            Assert.Equal(new[] { other }, otherResult.Select(document => document.Id));
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    private static StorageManifest WithoutIndex(StorageManifest manifest, string indexIdentity)
    {
        var unit = manifest.StorageUnits.Single();
        return manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    Indexes = unit.Indexes.Where(index => index.Identity != indexIdentity).ToList(),
                    Queries = unit.Queries.Where(query => query.IndexIdentity != indexIdentity).ToList()
                }
            ]
        };
    }

    private static string NewId() => $"doc-{Guid.NewGuid():N}";
}
