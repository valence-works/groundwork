using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Transactions;
using Groundwork.Core.Queries;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoDbPhysicalTopologyCollection
{
    public const string Name = "MongoDB physical standalone topology";
}

[Collection(MongoDbPhysicalTopologyCollection.Name)]
public sealed class MongoDbPhysicalTopologyTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Physical_factory_rejects_a_fresh_standalone_before_creating_database_state()
    {
        var databaseName = $"groundwork_standalone_fresh_{Guid.NewGuid():N}";
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        var exception = await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() =>
            MongoDbDocumentStoreFactory.CreatePhysicalAsync(
                container.GetConnectionString(),
                databaseName,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Equal(["workItem"], exception.Units);
        using var names = await Database(databaseName).ListCollectionNamesAsync();
        Assert.Empty(await names.ToListAsync());
    }

    [Fact]
    public async Task Physical_factory_rejects_standalone_before_reading_or_changing_seeded_applied_state()
    {
        var databaseName = $"groundwork_standalone_applied_{Guid.NewGuid():N}";
        var database = Database(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            model.Target,
            PhysicalSchemaHistoryState.Empty,
            DateTimeOffset.UtcNow);
        var acknowledgements = plan.Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.RecordAppliedState)
            .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                operation.Identity,
                operation.Fingerprint,
                DateTimeOffset.UtcNow))
            .ToArray();
        var applied = plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
        await database.CreateCollectionAsync("groundwork_physical_schema_state");
        var stateCollection = database.GetCollection<BsonDocument>("groundwork_physical_schema_state");
        var state = new BsonDocument
        {
            ["_id"] = MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity),
            ["target_fingerprint"] = applied.TargetFingerprint,
            ["state"] = PhysicalSchemaAppliedStateSerializer.Serialize(applied)
        };
        await stateCollection.InsertOneAsync(state);

        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() =>
            MongoDbDocumentStoreFactory.CreatePhysicalAsync(
                container.GetConnectionString(),
                databaseName,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        using var names = await database.ListCollectionNamesAsync();
        Assert.Equal(["groundwork_physical_schema_state"], await names.ToListAsync());
        Assert.Equal(state, await stateCollection.Find(Builders<BsonDocument>.Filter.Empty).SingleAsync());
    }

    [Fact]
    public async Task Direct_materializer_rejects_a_fresh_standalone_before_creating_database_state()
    {
        var databaseName = $"gw_st_dm_{Guid.NewGuid():N}";
        var database = Database(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model));

        using var names = await database.ListCollectionNamesAsync();
        Assert.Empty(await names.ToListAsync());
    }

    [Fact]
    public async Task Direct_materializer_rejects_standalone_before_reading_or_changing_seeded_applied_state()
    {
        var databaseName = $"gw_st_da_{Guid.NewGuid():N}";
        var database = Database(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            model.Target,
            PhysicalSchemaHistoryState.Empty,
            DateTimeOffset.UtcNow);
        var acknowledgements = plan.Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.RecordAppliedState)
            .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                operation.Identity,
                operation.Fingerprint,
                DateTimeOffset.UtcNow))
            .ToArray();
        var applied = plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
        await database.CreateCollectionAsync("groundwork_physical_schema_state");
        var stateCollection = database.GetCollection<BsonDocument>("groundwork_physical_schema_state");
        var state = new BsonDocument
        {
            ["_id"] = MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity),
            ["target_fingerprint"] = applied.TargetFingerprint,
            ["state"] = PhysicalSchemaAppliedStateSerializer.Serialize(applied)
        };
        await stateCollection.InsertOneAsync(state);

        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model));

        using var names = await database.ListCollectionNamesAsync();
        Assert.Equal(["groundwork_physical_schema_state"], await names.ToListAsync());
        Assert.Equal(state, await stateCollection.Find(Builders<BsonDocument>.Filter.Empty).SingleAsync());
    }

    [Fact]
    public async Task Direct_store_rejects_transactional_entries_on_standalone_without_creating_state()
    {
        var databaseName = $"gw_st_ds_{Guid.NewGuid():N}";
        var database = Database(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        Assert.Equal(TransactionBoundary.PerOperation, store.TransactionBoundary);
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "save", "1", "{}")));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.DeleteAsync(new DeleteDocumentRequest(
            "workItem", "delete")));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.LoadAsync(
            "workItem", "load"));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.BeginAsync(DocumentCommitScope.Of("workItem")));
        var query = new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]);
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.QueryAsync(query));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.CountAsync(
            query.Select(BoundedQueryResultOperation.Count)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.AnyAsync(
            query.Select(BoundedQueryResultOperation.Any)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.FirstOrDefaultAsync(
            query.Select(BoundedQueryResultOperation.First)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.ExplainAsync(query));
        Assert.Equal(TransactionBoundary.PerOperation, store.TransactionBoundary);

        using var names = await database.ListCollectionNamesAsync();
        Assert.Empty(await names.ToListAsync());
    }

    private IMongoDatabase Database(string name) =>
        new MongoClient(container.GetConnectionString()).GetDatabase(name);
}
