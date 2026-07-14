using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.MongoDb;
using Groundwork.MongoDb.Documents;
using Groundwork.TestInfrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbDocumentIdentityAcceptanceContainer : IAsyncLifetime
{
    public MongoDbContainer Container { get; } = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-identity-acceptance-rs")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class MongoDbDocumentIdentityAcceptanceTests(MongoDbDocumentIdentityAcceptanceContainer fixture)
    : DocumentIdentityAcceptanceConformance, IClassFixture<MongoDbDocumentIdentityAcceptanceContainer>
{
    protected override async Task<DocumentIdentityAcceptanceFixture> CreateIdentityFixtureAsync(
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        DocumentIdentityAcceptanceSurface surface = DocumentIdentityAcceptanceSurface.Exact)
    {
        var handle = await MongoDbDocumentStoreFactory.CreatePhysicalAsync(
            fixture.Container.GetConnectionString(),
            $"groundwork_identity_acceptance_{Guid.NewGuid():N}",
            DocumentIdentityAcceptanceModel.Manifest(
                form,
                stringCasePolicy,
                surface,
                Guid.NewGuid().ToString("N")[..8]),
            MongoDbGroundworkCapabilities.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        return new DocumentIdentityAcceptanceFixture(
            handle.Store,
            handle.Store,
            Assert.Single(handle.Model.Routes),
            handle.DisposeAsync,
            surface == DocumentIdentityAcceptanceSurface.Mutation
                ? MongoDbPhysicalMutationRuntime.Create(
                    handle.Store,
                    handle.Model.Manifest,
                    Assert.Single(handle.Model.Routes),
                    handle.Model.Provider)
                : null,
            query => ExplainQueryAsync(handle.Store, Assert.Single(handle.Model.Routes), query),
            mutation => ExplainMutationAsync(handle.Store, handle.Model, mutation));
    }

    private static async Task<DocumentIdentityNativePlanEvidence> ExplainQueryAsync(
        MongoDbPhysicalDocumentStore documents,
        ExecutableStorageRoute route,
        Groundwork.Documents.Store.DocumentQuery query)
    {
        var explanation = await documents.ExplainAsync(query);
        var expectedIndex = route.Indexes.Single(candidate => candidate.Identity.StartsWith(
            DocumentIdentityAcceptanceModel.ExactIndexIdentity,
            StringComparison.Ordinal));
        var filter = explanation.ToJson();
        var index = await ReadIndexAsync(
            documents.Database,
            route.PrimaryStorage.Name.Identifier,
            expectedIndex.Name.Identifier);
        return Evidence(route, filter, expectedIndex.Name.Identifier, index);
    }

    private static async Task<DocumentIdentityNativePlanEvidence> ExplainMutationAsync(
        MongoDbPhysicalDocumentStore documents,
        MongoDbPhysicalStorageModel model,
        Groundwork.Documents.Store.DocumentMutation mutation)
    {
        var route = Assert.Single(model.Routes);
        var explanation = await MongoDbPhysicalMutationRuntime.ExplainAsync(
            documents,
            model.Manifest,
            route,
            model.Provider,
            mutation);
        var primary = explanation["primary"].AsBsonDocument;
        var expectedIndex = primary["indexName"].AsString;
        var index = await ReadIndexAsync(
            documents.Database,
            route.PrimaryStorage.Name.Identifier,
            expectedIndex);
        return Evidence(route, primary.ToJson(), expectedIndex, index);
    }

    private static DocumentIdentityNativePlanEvidence Evidence(
        ExecutableStorageRoute route,
        string plan,
        string expectedIndex,
        BsonDocument index)
    {
        var lookup = route.Envelope.Identity.LookupKey.Identifier;
        var comparison = route.Envelope.Identity.ComparisonKey.Identifier;
        var keys = index["key"].AsBsonDocument.Names.ToHashSet(StringComparer.Ordinal);
        return new DocumentIdentityNativePlanEvidence(
            plan.Contains(expectedIndex, StringComparison.Ordinal) &&
            plan.Contains("IXSCAN", StringComparison.Ordinal),
            plan.Contains("COLLSCAN", StringComparison.Ordinal),
            plan.Contains(lookup, StringComparison.Ordinal),
            plan.Contains(comparison, StringComparison.Ordinal),
            keys.Contains(lookup) && keys.Contains(comparison),
            $"INDEX:{Environment.NewLine}{index.ToJson()}{Environment.NewLine}PLAN:{Environment.NewLine}{plan}");
    }

    private static async Task<BsonDocument> ReadIndexAsync(
        IMongoDatabase database,
        string collectionName,
        string indexName)
    {
        var indexes = await (await database.GetCollection<BsonDocument>(collectionName)
            .Indexes.ListAsync()).ToListAsync();
        return Assert.Single(indexes, index => index["name"].AsString == indexName);
    }
}
