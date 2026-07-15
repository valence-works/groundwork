using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbPhysicalStoreAdmissionTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-admission-rs")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task OpenPhysicalAsync_opens_an_exactly_applied_target_without_applying_schema()
    {
        var databaseName = $"groundwork_{Guid.NewGuid():N}";
        var database = new MongoClient(container.GetConnectionString()).GetDatabase(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);

        await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
            container.GetConnectionString(),
            databaseName,
            model.Manifest,
            model.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var saved = await handle.Store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            "1",
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));

        Assert.True(handle.SchemaInspection.IsAppliedSchemaValid);
        Assert.Equal(model.Target.Fingerprint, handle.SchemaInspection.History.AppliedState!.TargetFingerprint);
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.NotNull(await handle.Store.LoadAsync("workItem", "1"));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_a_pending_target_without_creating_database_state()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("requires the exact target", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await CollectionNamesAsync(database));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_a_changed_target_without_applying_pending_changes()
    {
        var database = Database();
        var applied = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(applied);
        var pending = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            path: "priority");
        var beforeCollections = await CollectionNamesAsync(database);
        var beforeState = await AppliedStateAsync(database);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                pending.Manifest,
                pending.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("requires the exact target", exception.Message, StringComparison.Ordinal);
        Assert.Equal(beforeCollections, await CollectionNamesAsync(database));
        Assert.Equal(beforeState, await AppliedStateAsync(database));
        var route = Assert.Single(applied.Routes);
        var indexes = await IndexNamesAsync(database, route.PrimaryStorage.Name.Identifier);
        Assert.DoesNotContain(Assert.Single(pending.Routes).Indexes.Single().Name.Identifier, indexes);
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_physical_drift_without_repairing_or_changing_history()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        await collection.Indexes.DropOneAsync(index.Name.Identifier);
        var beforeState = await AppliedStateAsync(database);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("found drift", exception.Message, StringComparison.Ordinal);
        Assert.Equal(beforeState, await AppliedStateAsync(database));
        Assert.DoesNotContain(index.Name.Identifier, await IndexNamesAsync(
            database,
            route.PrimaryStorage.Name.Identifier));
    }

    [Fact]
    public async Task OpenPhysicalAsync_compiles_and_admits_the_configured_name_policy()
    {
        var database = Database();
        var defaultModel = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var names = new DelegatePhysicalNamePolicy(context => $"support_{context.FeatureDefaultLogicalName}");
        var namedModel = MongoDbPhysicalStorageModel.Compile(
            defaultModel.Manifest,
            defaultModel.Provider,
            names);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(namedModel);

        await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
            database,
            namedModel.Manifest,
            namedModel.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            namePolicy: names);

        Assert.Equal(namedModel.Target.Fingerprint, handle.Model.Target.Fingerprint);
        Assert.All(handle.Model.Routes, route =>
            Assert.StartsWith("support_", route.PrimaryStorage.Name.Identifier, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_typed_identity_route_drift_without_changing_history()
    {
        var database = Database();
        var applied = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(applied);
        var changedIdentity = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            identityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var beforeState = await AppliedStateAsync(database);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                changedIdentity.Manifest,
                changedIdentity.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("requires the exact target", exception.Message, StringComparison.Ordinal);
        Assert.Equal(beforeState, await AppliedStateAsync(database));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_provider_version_drift_without_changing_history()
    {
        var database = Database();
        var applied = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(applied);
        var upgradedProvider = new Groundwork.Core.Capabilities.ProviderIdentity(
            applied.Provider.Name,
            "next");
        var beforeState = await AppliedStateAsync(database);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                applied.Manifest,
                upgradedProvider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("requires the exact target", exception.Message, StringComparison.Ordinal);
        Assert.Equal(beforeState, await AppliedStateAsync(database));
    }

    private IMongoDatabase Database() =>
        new MongoClient(container.GetConnectionString()).GetDatabase($"groundwork_{Guid.NewGuid():N}");

    private static async Task<string[]> CollectionNamesAsync(IMongoDatabase database)
    {
        using var cursor = await database.ListCollectionNamesAsync();
        return (await cursor.ToListAsync()).Order(StringComparer.Ordinal).ToArray();
    }

    private static Task<BsonDocument> AppliedStateAsync(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();

    private static async Task<string[]> IndexNamesAsync(IMongoDatabase database, string collectionName)
    {
        using var cursor = await database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync();
        return (await cursor.ToListAsync())
            .Select(index => index["name"].AsString)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
