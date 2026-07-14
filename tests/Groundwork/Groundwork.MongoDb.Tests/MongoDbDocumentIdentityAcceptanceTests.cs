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

internal static class MongoDbDocumentIdentityAcceptanceEvidence
{
    public static DocumentIdentityNativePlanEvidence Create(
        DocumentIdentityExpectedIndex expectedIndex,
        BsonDocument winningPlan,
        BsonDocument materializedIndex)
    {
        var observation = MongoDbWinningPlanInspector.Inspect(winningPlan);
        var accessPaths = observation.IndexScans
            .Select(scan => new DocumentIdentityAccessPath(scan.IndexName, false))
            .Concat(observation.HasCollectionScan
                ? [new DocumentIdentityAccessPath(null, true)]
                : [])
            .ToArray();
        var indexName = materializedIndex["name"].AsString;
        var keyFields = materializedIndex["key"].AsBsonDocument.Names.ToArray();
        var selectorFields = observation.IndexScans
            .Where(scan => scan.IndexName.Equals(expectedIndex.Name, StringComparison.Ordinal))
            .SelectMany(scan => scan.Bounds
                .Where(bound => bound.IsConstrained)
                .Select(bound => bound.Field))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var isPartial = materializedIndex.Contains("partialFilterExpression");
        var isSparse = IsTrue(materializedIndex, "sparse");
        var isHidden = IsTrue(materializedIndex, "hidden");
        return DocumentIdentityNativePlanEvidence.Create(
            expectedIndex,
            accessPaths,
            [new DocumentIdentityMaterializedIndex(
                indexName,
                keyFields,
                IsPartial: isPartial,
                IsSparse: isSparse,
                IsHidden: isHidden)],
            selectorFields,
            $"INDEX:{Environment.NewLine}{materializedIndex.ToJson()}{Environment.NewLine}" +
            $"WINNING PLAN:{Environment.NewLine}{winningPlan.ToJson()}");
    }

    private static bool IsTrue(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsBoolean && value.AsBoolean;

    public static DocumentIdentityNativePlanEvidence CreateMutation(
        DocumentIdentityExpectedIndex expectedIndex,
        BsonDocument primaryEvidence,
        BsonDocument materializedIndex) =>
        Create(expectedIndex, primaryEvidence["winningPlan"].AsBsonDocument, materializedIndex);
}

public sealed class MongoDbDocumentIdentityAcceptanceEvidenceTests
{
    [Fact]
    public void Rejected_plan_index_cannot_hide_a_winning_collection_scan()
    {
        var explanation = new BsonDocument("queryPlanner", new BsonDocument
        {
            ["winningPlan"] = new BsonDocument("stage", "COLLSCAN"),
            ["rejectedPlans"] = new BsonArray
            {
                new BsonDocument
                {
                    ["stage"] = "IXSCAN",
                    ["indexName"] = "ix_identity",
                    ["indexBounds"] = new BsonDocument
                    {
                        ["id_lookup_key"] = new BsonArray(),
                        ["id_comparison_key"] = new BsonArray()
                    }
                }
            }
        });
        var winningPlan = MongoDbWinningPlanInspector.ExactWinningPlan(explanation);

        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.Create(
            ExpectedIndex(),
            winningPlan,
            Index("ix_identity", "storage_scope", "id_lookup_key", "id_comparison_key"));

        Assert.False(evidence.UsesExpectedIndex);
        Assert.True(evidence.HasFullScan);
    }

    [Fact]
    public void Same_name_index_with_wrong_list_indexes_shape_does_not_cover_the_selector()
    {
        var winningPlan = new BsonDocument
        {
            ["stage"] = "IXSCAN",
            ["indexName"] = "ix_identity",
            ["indexBounds"] = new BsonDocument
            {
                ["id_lookup_key"] = new BsonArray(),
                ["id_comparison_key"] = new BsonArray()
            }
        };

        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.Create(
            ExpectedIndex(),
            winningPlan,
            Index("ix_identity", "storage_scope", "id_comparison_key", "id_lookup_key"));

        Assert.True(evidence.UsesExpectedIndex);
        Assert.False(evidence.IndexCoversSelectorFields);
    }

    [Fact]
    public void Mutation_evidence_uses_the_independent_expectation_not_the_reported_index_name()
    {
        var primary = new BsonDocument
        {
            ["indexName"] = "production-reported-decoy",
            ["winningPlan"] = new BsonDocument
            {
                ["stage"] = "IXSCAN",
                ["indexName"] = "ix_identity",
                ["indexBounds"] = new BsonDocument
                {
                    ["id_lookup_key"] = Bounded("lookup"),
                    ["id_comparison_key"] = Bounded("comparison")
                }
            }
        };

        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.CreateMutation(
            ExpectedIndex(),
            primary,
            Index("ix_identity", "storage_scope", "id_lookup_key", "id_comparison_key"));

        Assert.True(evidence.UsesExpectedIndex);
        Assert.True(evidence.IndexCoversSelectorFields);
    }

    [Fact]
    public void Unbounded_winning_index_bounds_do_not_count_as_selector_constraints()
    {
        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.Create(
            ExpectedIndex(),
            WinningIndexPlan(
                new BsonArray("[MinKey, MaxKey]"),
                new BsonArray("[MinKey, MaxKey]")),
            Index("ix_identity", "storage_scope", "id_lookup_key", "id_comparison_key"));

        Assert.True(evidence.UsesExpectedIndex);
        Assert.False(evidence.SelectorUsesLookupKey);
        Assert.False(evidence.SelectorUsesComparisonKey);
    }

    [Fact]
    public void Same_name_index_with_an_extra_leading_key_does_not_cover_the_selector()
    {
        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.Create(
            ExpectedIndex(),
            WinningIndexPlan(Bounded("lookup"), Bounded("comparison")),
            Index("ix_identity", "decoy", "storage_scope", "id_lookup_key", "id_comparison_key"));

        Assert.True(evidence.UsesExpectedIndex);
        Assert.False(evidence.IndexCoversSelectorFields);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("sparse")]
    [InlineData("hidden")]
    public void Non_mandatory_mongodb_index_variants_do_not_cover_the_selector(string variant)
    {
        var index = Index("ix_identity", "storage_scope", "id_lookup_key", "id_comparison_key");
        if (variant == "partial")
            index["partialFilterExpression"] = new BsonDocument("status", "pending");
        else
            index[variant] = true;

        var evidence = MongoDbDocumentIdentityAcceptanceEvidence.Create(
            ExpectedIndex(),
            WinningIndexPlan(Bounded("lookup"), Bounded("comparison")),
            index);

        Assert.False(evidence.IndexCoversSelectorFields);
    }

    private static BsonDocument WinningIndexPlan(BsonArray lookupBounds, BsonArray comparisonBounds) => new()
    {
        ["stage"] = "IXSCAN",
        ["indexName"] = "ix_identity",
        ["indexBounds"] = new BsonDocument
        {
            ["id_lookup_key"] = lookupBounds,
            ["id_comparison_key"] = comparisonBounds
        }
    };

    private static BsonArray Bounded(string value) => [$"[\"{value}\", \"{value}\"]"];

    private static DocumentIdentityExpectedIndex ExpectedIndex() => new(
        "ix_identity",
        ["id_lookup_key", "id_comparison_key"],
        ["storage_scope", "id_lookup_key", "id_comparison_key"]);

    private static BsonDocument Index(string name, params string[] fields) => new()
    {
        ["name"] = name,
        ["key"] = new BsonDocument(fields.Select(field => new BsonElement(field, 1)))
    };
}

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
        var expectedIndex = DocumentIdentityAcceptanceModel.ExactIndex(route);
        var winningPlan = MongoDbWinningPlanInspector.ExactWinningPlan(explanation);
        var index = await ReadIndexAsync(
            documents.Database,
            route.PrimaryStorage.Name.Identifier,
            expectedIndex.Name);
        return MongoDbDocumentIdentityAcceptanceEvidence.Create(expectedIndex, winningPlan, index);
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
        var exactIndex = DocumentIdentityAcceptanceModel.ExactIndex(route);
        var storage = model.StorageByStorageUnit[route.StorageUnit.Value];
        var declaration = storage.BoundedMutations.Single(candidate =>
            candidate.Identity == mutation.MutationIdentity);
        var predicate = storage.BoundedQueries.Single(candidate =>
            candidate.Identity == declaration.PredicateQueryIdentity);
        var expectedIndex = new DocumentIdentityExpectedIndex(
            MongoDbPhysicalMutationStorage.IndexName(
                route,
                predicate,
                ExecutableStorageObjectRole.PrimaryStorage),
            exactIndex.SelectorFields,
            [
                route.Envelope.DocumentKind.Identifier,
                route.Envelope.StorageScope.Identifier,
                .. exactIndex.SelectorFields
            ]);
        var index = await ReadIndexAsync(
            documents.Database,
            route.PrimaryStorage.Name.Identifier,
            expectedIndex.Name);
        return MongoDbDocumentIdentityAcceptanceEvidence.CreateMutation(expectedIndex, primary, index);
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
