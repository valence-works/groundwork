using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
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

    private IMongoDatabase Database(string name) =>
        new MongoClient(container.GetConnectionString()).GetDatabase(name);
}
