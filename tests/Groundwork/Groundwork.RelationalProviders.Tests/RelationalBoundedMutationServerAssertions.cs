using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalBoundedMutationServerAssertions
{
    public static async Task TransitionUpdatesExactIndexedIdentitySetAsync<TStore>(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var store = createStore(model.Manifest, model.Target.Routes);
        await SaveAsync(store, "pending-a", "pending");
        await SaveAsync(store, "pending-b", "pending");
        await SaveAsync(store, "active", "active");

        var result = await createRuntime(
                store,
                model.Manifest,
                model.Target.Routes.Single(),
                model.Target.Provider)
            .ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{provider.Name}-transition-tracer"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Equal("revoked", await ReadCategoryAsync(store, "pending-a"));
        Assert.Equal("revoked", await ReadCategoryAsync(store, "pending-b"));
        Assert.Equal("active", await ReadCategoryAsync(store, "active"));
    }

    public static async Task ConcurrentRetryReplaysExactResultAsync<TStore>(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var firstStore = createStore(model.Manifest, model.Target.Routes);
        var secondStore = createStore(model.Manifest, model.Target.Routes);
        for (var index = 0; index < 5; index++)
            await SaveAsync(firstStore, $"pending-{index}", "pending");
        var request = new DocumentMutation(
            "configurationDocument",
            "revoke-pending",
            $"{provider.Name}-concurrent-retry");

        var results = await Task.WhenAll(
            createRuntime(firstStore, model.Manifest, model.Target.Routes.Single(), model.Target.Provider)
                .ExecuteAsync(request),
            createRuntime(secondStore, model.Manifest, model.Target.Routes.Single(), model.Target.Provider)
                .ExecuteAsync(request));

        Assert.Equal(
            [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
            results.Select(result => result.Status).Order().ToArray());
        Assert.All(results, result => Assert.Equal(5, result.AffectedCount));
        for (var index = 0; index < 5; index++)
            Assert.Equal("revoked", await ReadCategoryAsync(firstStore, $"pending-{index}"));
    }

    public static async Task PhysicalFormsExecuteTransitionAndRangeDeleteAsync<TStore>(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createMutationRuntime,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentStore> createQueryRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        foreach (var form in Enum.GetValues<PhysicalStorageForm>())
        {
            var model = RelationalPhysicalStorageTestModels.Create(
                form,
                provider,
                includePriority: true,
                includeCategoryTransition: true,
                includeRangeDelete: true,
                normalizer: normalizer);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
            var store = createStore(model.Manifest, model.Target.Routes);
            await SaveAsync(store, "pending", "pending", 1);
            await SaveAsync(store, "expired-a", "authorization-a", 1);
            await SaveAsync(store, "expired-b", "authorization-a", 9);
            await SaveAsync(store, "future", "authorization-a", 10);
            await SaveAsync(store, "other-authorization", "authorization-b", 1);
            var route = model.Target.Routes.Single();
            var mutations = createMutationRuntime(store, model.Manifest, route, model.Target.Provider);

            var transitioned = await mutations.ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{provider.Name}-{form}-transition"));
            var deleted = await mutations.ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "prune-by-category-cutoff",
                $"{provider.Name}-{form}-delete",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "authorization-a")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
                ]));

            Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), transitioned);
            Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), deleted);
            Assert.Equal("revoked", await ReadCategoryAsync(store, "pending"));
            Assert.Null(await store.LoadAsync("configurationDocument", "expired-a"));
            Assert.Null(await store.LoadAsync("configurationDocument", "expired-b"));
            Assert.NotNull(await store.LoadAsync("configurationDocument", "future"));
            Assert.NotNull(await store.LoadAsync("configurationDocument", "other-authorization"));
            var query = createQueryRuntime(store, model.Manifest, route, model.Target.Provider);
            Assert.Equal(1, await query.CountAsync(new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "revoked"))],
                resultOperation: BoundedQueryResultOperation.Count)));
        }
    }

    public static async Task MutationScopeIsInheritedFromStoreSessionAsync<TStore>(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, DocumentStoreAccess, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            scoped: true,
            includeCategoryTransition: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var route = model.Target.Routes.Single();
        var tenantA = createStore(
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = createStore(
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));
        await SaveAsync(tenantA, "same-id", "pending");
        await SaveAsync(tenantB, "same-id", "pending");

        var result = await createRuntime(tenantA, model.Manifest, route, model.Target.Provider)
            .ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{provider.Name}-tenant-a-transition"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.Equal("revoked", await ReadCategoryAsync(tenantA, "same-id"));
        Assert.Equal("pending", await ReadCategoryAsync(tenantB, "same-id"));
    }

    public static async Task FailureBeforeCommitRollsBackAndRetryCompletesAsync<TStore>(
        ProviderIdentity provider,
        string handlerPrefix,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, DocumentStoreAccess, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentStore> createQueryRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var route = model.Target.Routes.Single();
        var store = createStore(model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await SaveAsync(store, "pending", "pending");
        var request = Transition($"{provider.Name}-rollback");
        var mutations = RelationalPhysicalMutationRuntime.Create(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            provider.Name,
            handlerPrefix,
            canonicalJsonValueKinds: null,
            point => point == RelationalPhysicalMutationExecutionPoint.BeforeCommit
                ? ValueTask.FromException(new SimulatedMutationFailureException())
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<SimulatedMutationFailureException>(() => mutations.ExecuteAsync(request));

        Assert.Equal("pending", await ReadCategoryAsync(store, "pending"));
        Assert.Equal(1, await CountAsync(createQueryRuntime(store, model.Manifest, route, model.Target.Provider), "pending"));
        Assert.Equal(0, await CountAsync(createQueryRuntime(store, model.Manifest, route, model.Target.Provider), "revoked"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await createRuntime(store, model.Manifest, route, model.Target.Provider).ExecuteAsync(request));
    }

    public static async Task CancellationBeforeCommitRollsBackAndPreservesTokenAsync<TStore>(
        ProviderIdentity provider,
        string handlerPrefix,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, DocumentStoreAccess, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var route = model.Target.Routes.Single();
        var store = createStore(model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await SaveAsync(store, "pending", "pending");
        var request = Transition($"{provider.Name}-cancellation");
        using var cancellation = new CancellationTokenSource();
        var mutations = RelationalPhysicalMutationRuntime.Create(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            provider.Name,
            handlerPrefix,
            canonicalJsonValueKinds: null,
            point =>
            {
                if (point != RelationalPhysicalMutationExecutionPoint.BeforeCommit)
                    return ValueTask.CompletedTask;
                cancellation.Cancel();
                return ValueTask.FromCanceled(cancellation.Token);
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutations.ExecuteAsync(request));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("pending", await ReadCategoryAsync(store, "pending"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await createRuntime(store, model.Manifest, route, model.Target.Provider).ExecuteAsync(request));
    }

    public static async Task AcknowledgementLossRestartAndProviderUpgradeReplayAsync<TStore>(
        ProviderIdentity provider,
        string handlerPrefix,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, DocumentStoreAccess, TStore> createStore,
        Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> createRuntime)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            includeCategoryTransition: true,
            includeRangeDelete: true,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var route = model.Target.Routes.Single();
        var firstStore = createStore(model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        await SaveAsync(firstStore, "pending-a", "pending");
        await SaveAsync(firstStore, "pending-b", "pending");
        var operationId = $"{provider.Name}-ack-loss-upgrade";
        var request = Transition(operationId);
        var loseAcknowledgement = true;
        var mutations = RelationalPhysicalMutationRuntime.Create(
            firstStore,
            model.Manifest,
            route,
            model.Target.Provider,
            provider.Name,
            handlerPrefix,
            canonicalJsonValueKinds: null,
            point =>
            {
                if (point != RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement ||
                    !loseAcknowledgement)
                {
                    return ValueTask.CompletedTask;
                }
                loseAcknowledgement = false;
                return ValueTask.FromException(new SimulatedMutationAcknowledgementLossException());
            });

        await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() => mutations.ExecuteAsync(request));

        var restartedStore = createStore(model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        var upgradedProvider = new ProviderIdentity(provider.Name, "2.0.0");
        var restarted = createRuntime(restartedStore, model.Manifest, route, upgradedProvider);
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 2),
            await restarted.ExecuteAsync(request));
        Assert.Equal("revoked", await ReadCategoryAsync(restartedStore, "pending-a"));
        Assert.Equal("revoked", await ReadCategoryAsync(restartedStore, "pending-b"));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() => restarted.ExecuteAsync(
            new DocumentMutation(
                "configurationDocument",
                "prune-by-category-cutoff",
                operationId,
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
                ])));
    }

    private static async Task SaveAsync(IDocumentStore store, string id, string category, int priority = 1)
    {
        var result = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1",
            $"{{\"category\":\"{category}\",\"priority\":{priority}}}"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, result.Status);
    }

    private static async Task<string> ReadCategoryAsync(IDocumentStore store, string id)
    {
        var document = await store.LoadAsync("configurationDocument", id);
        Assert.NotNull(document);
        return JsonDocument.Parse(document.ContentJson).RootElement.GetProperty("category").GetString()!;
    }

    private static Task<long> CountAsync(IBoundedDocumentStore store, string category) =>
        store.CountAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))],
            resultOperation: BoundedQueryResultOperation.Count));

    private static DocumentMutation Transition(string operationId) =>
        new("configurationDocument", "revoke-pending", operationId);

    private sealed class SimulatedMutationFailureException : Exception;

    private sealed class SimulatedMutationAcknowledgementLossException : Exception;
}
