using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbBoundedMutationTests : IAsyncLifetime
{
    private const string DocumentKind = "workItem";
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-mutations-rs")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Transition_updates_canonical_document_and_linked_projection()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var route = Assert.Single(model.Routes);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            route,
            model.Provider);
        await SaveAsync(documents, "pending-a", "pending");
        await SaveAsync(documents, "pending-b", "pending");
        await SaveAsync(documents, "active", "active");

        var result = await mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "revoke-pending",
            "revoke-1"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Equal("revoked", Status((await documents.LoadAsync(DocumentKind, "pending-a"))!.ContentJson));
        Assert.Equal("revoked", Status((await documents.LoadAsync(DocumentKind, "pending-b"))!.ContentJson));
        Assert.Equal(2, await documents.CountAsync(Query("revoked").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(0, await documents.CountAsync(Query("pending").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(1, await documents.CountAsync(Query("active").Select(BoundedQueryResultOperation.Count)));
    }

    [Fact]
    public async Task Delete_is_exact_idempotent_and_rejects_operation_reuse()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var route = Assert.Single(model.Routes);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            route,
            model.Provider);
        await SaveAsync(documents, "stale-a", "stale");
        await SaveAsync(documents, "stale-b", "stale");
        await SaveAsync(documents, "current", "current");
        var request = Delete("prune-1", "stale");

        var completed = await mutations.ExecuteAsync(request);
        var replayed = await mutations.ExecuteAsync(request);

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), completed);
        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Replayed, 2), replayed);
        Assert.Null(await documents.LoadAsync(DocumentKind, "stale-a"));
        Assert.Null(await documents.LoadAsync(DocumentKind, "stale-b"));
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "current"));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() =>
            mutations.ExecuteAsync(Delete("prune-1", "current")));
    }

    [Fact]
    public async Task Runtime_rejects_a_provider_that_does_not_match_the_compiled_store()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                documents,
                model.Manifest,
                Assert.Single(model.Routes),
                new(model.Provider.Name, "incompatible")));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    private static MongoDbPhysicalStorageModel Model()
    {
        var template = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.DedicatedDocumentTable,
            path: "status");
        var unit = Assert.Single(template.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        var manifest = template.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        storage.NameOverrides,
                        [
                            new BoundedMutationDeclaration(
                                "prune-by-status",
                                "list-by-status",
                                BoundedMutationAction.Delete()),
                            new BoundedMutationDeclaration(
                                "revoke-pending",
                                "list-by-status",
                                BoundedMutationAction.Transition("status", ["pending"], "revoked"))
                        ])
                }
            ]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static DocumentQuery Query(string status) =>
        new(
            DocumentKind,
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", status))]);

    private static DocumentMutation Delete(string operationId, string status) =>
        new(
            DocumentKind,
            "prune-by-status",
            operationId,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", status))]);

    private static string Status(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;

    private static async Task SaveAsync(
        MongoDbPhysicalDocumentStore documents,
        string id,
        string status)
    {
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await documents.SaveAsync(new SaveDocumentRequest(
                DocumentKind,
                id,
                "1",
                $$"""{"status":"{{status}}","rank":1}""",
                ExpectedVersion: 0))).Status);
    }
}
