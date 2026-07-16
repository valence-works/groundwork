using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
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
    public async Task Admitted_handle_creates_an_additional_access_bound_store()
    {
        var initialAccess = DocumentStoreAccess.Scoped(new("tenant-a"));
        var additionalAccess = DocumentStoreAccess.Scoped(new("tenant-b"));

        await using var handle = await OpenAdmittedHandleAsync(initialAccess);
        var additionalStore = handle.CreateStore(additionalAccess);

        Assert.Same(initialAccess, handle.Store.Access);
        Assert.Same(additionalAccess, additionalStore.Access);
        Assert.NotSame(handle.Store, additionalStore);
    }

    [Fact]
    public async Task Access_bound_stores_isolate_scoped_documents()
    {
        await using var handle = await OpenAdmittedHandleAsync();
        var tenantA = handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-a")));
        var tenantB = handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-b")));

        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await tenantA.SaveAsync(new SaveDocumentRequest(
                "workItem", "shared-id", "1", """{"status":"tenant-a"}""", ExpectedVersion: 0))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await tenantB.SaveAsync(new SaveDocumentRequest(
                "workItem", "shared-id", "1", """{"status":"tenant-b"}""", ExpectedVersion: 0))).Status);

        Assert.Equal("""{"status":"tenant-a"}""", (await tenantA.LoadAsync("workItem", "shared-id"))!.ContentJson);
        Assert.Equal("""{"status":"tenant-b"}""", (await tenantB.LoadAsync("workItem", "shared-id"))!.ContentJson);
    }

    [Fact]
    public async Task CreateStore_observes_each_privileged_access_binding()
    {
        var observer = new RecordingStorageScopeObserver();
        var access = DocumentStoreAccess.PrivilegedScoped(
            new PrivilegedStorageAccess("repair tenant projection"),
            new("tenant-a"));

        await using var handle = await OpenAdmittedHandleAsync();

        var store = handle.CreateStore(access, observer);

        Assert.Same(access, store.Access);
        var audit = Assert.Single(observer.PrivilegedAcquisitions);
        Assert.Equal(DocumentStoreAccessKind.PrivilegedScoped, audit.AccessKind);
        Assert.Equal("repair tenant projection", audit.Reason);
    }

    [Fact]
    public async Task CreateStore_performs_no_provider_traffic_or_repeated_admission()
    {
        var commands = 0;
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        settings.ClusterConfigurator = builder =>
            builder.Subscribe<CommandStartedEvent>(_ => Interlocked.Increment(ref commands));
        using var client = new MongoClient(settings);
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        await using var handle = await OpenAdmittedHandleAsync(database: database);
        Interlocked.Exchange(ref commands, 0);

        handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-a")));
        handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-b")));

        Assert.Equal(0, Volatile.Read(ref commands));
    }

    [Fact]
    public async Task Admitted_handle_supports_concurrent_access_binding()
    {
        await using var handle = await OpenAdmittedHandleAsync();

        var stores = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
            handle.CreateStore(DocumentStoreAccess.Scoped(new($"tenant-{index}"))))));

        Assert.Equal(64, stores.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(
            Enumerable.Range(0, 64).Select(index => $"tenant-{index}").Order(StringComparer.Ordinal),
            stores.Select(store => store.Access.Scope!.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Disposal_waits_for_an_in_flight_access_binding_and_then_closes_the_handle()
    {
        var handle = await OpenAdmittedHandleAsync();
        using var observer = new BlockingStorageScopeObserver();
        using var disposalAttempted = new ManualResetEventSlim();
        var access = DocumentStoreAccess.PrivilegedScoped(
            new PrivilegedStorageAccess("repair tenant projection"),
            new("tenant-a"));
        var binding = Task.Run(() => handle.CreateStore(access, observer));
        Assert.True(observer.BindingEntered.Wait(TimeSpan.FromSeconds(5)));

        var disposal = Task.Run(async () =>
        {
            disposalAttempted.Set();
            await handle.DisposeAsync();
        });

        try
        {
            Assert.True(disposalAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(100))));
        }
        finally
        {
            observer.AllowBindingToComplete.Set();
        }

        Assert.Same(access, (await binding).Access);
        await disposal;
        Assert.Throws<ObjectDisposedException>(() =>
            handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-b"))));
    }

    [Fact]
    public async Task Disposed_admitted_handle_is_idempotent_and_rejects_new_stores()
    {
        var handle = await OpenAdmittedHandleAsync();

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            handle.CreateStore(DocumentStoreAccess.Scoped(new("tenant-a"))));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_a_pending_target_without_creating_database_state()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("requires the exact target", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await CollectionNamesAsync(database));
    }

    [Fact]
    public async Task OpenPhysicalAsync_auto_applies_a_safe_pending_target_when_enabled()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        await using var handle = await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
            database,
            model.Manifest,
            model.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true });

        Assert.Equal(model.Target.Fingerprint, handle.SchemaInspection.History.AppliedState!.TargetFingerprint);
        Assert.NotEmpty(await CollectionNamesAsync(database));
    }

    [Fact]
    public async Task OpenPhysicalAsync_rejects_invalid_manifest_before_auto_apply_mutates_schema()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var invalidManifest = model.Manifest with
        {
            StorageUnits =
            [
                model.Manifest.StorageUnits.Single() with { Lifecycle = null! }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.OpenPhysicalAsync(
                database,
                invalidManifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a")),
                options: new MongoDbPhysicalDocumentStoreOptions { AutoApplyOnStartup = true }));

        Assert.Contains("GW-UNIT-006", exception.Message, StringComparison.Ordinal);
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

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
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

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
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

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
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

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
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

    private async Task<MongoDbPhysicalDocumentStoreOpenHandle> OpenAdmittedHandleAsync(
        DocumentStoreAccess? access = null,
        IMongoDatabase? database = null)
    {
        database ??= Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        return await MongoDbDocumentStoreFactory.OpenPhysicalAsync(
            database,
            model.Manifest,
            model.Provider,
            access ?? DocumentStoreAccess.Scoped(new("admission")));
    }

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

    private sealed class RecordingStorageScopeObserver : IStorageScopeObserver
    {
        public List<PrivilegedStorageSessionAudit> PrivilegedAcquisitions { get; } = [];

        public void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit) =>
            PrivilegedAcquisitions.Add(audit);

        public void ScopeAccessRejected(StorageScopeAccessRejection rejection)
        {
        }
    }

    private sealed class BlockingStorageScopeObserver : IStorageScopeObserver, IDisposable
    {
        public ManualResetEventSlim BindingEntered { get; } = new();
        public ManualResetEventSlim AllowBindingToComplete { get; } = new();

        public void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit)
        {
            BindingEntered.Set();
            if (!AllowBindingToComplete.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the access binding.");
        }

        public void ScopeAccessRejected(StorageScopeAccessRejection rejection)
        {
        }

        public void Dispose()
        {
            BindingEntered.Dispose();
            AllowBindingToComplete.Dispose();
        }
    }
}
