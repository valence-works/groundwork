using System.Data.Common;
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

internal sealed record RelationalLockContentionProbe(
    Func<DbConnection, DbTransaction, CancellationToken, ValueTask<int>> ReadSessionId,
    Func<int, int, CancellationToken, Task> WaitUntilBlockedAsync);

internal sealed record RelationalMutationServerHarness<TStore>(
    ProviderIdentity Provider,
    string HandlerPrefix,
    IProviderPhysicalNameNormalizer Normalizer,
    Func<IPhysicalSchemaExecutor> CreateExecutor,
    Func<StorageManifest, IReadOnlyList<ExecutableStorageRoute>, DocumentStoreAccess, TStore> StoreFactory,
    Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentMutationStore> MutationRuntimeFactory,
    Func<TStore, StorageManifest, ExecutableStorageRoute, ProviderIdentity, IBoundedDocumentStore> QueryRuntimeFactory,
    Func<DbConnection> ConnectionFactory,
    Func<RelationalPhysicalDocumentDialect> DialectFactory,
    RelationalLockContentionProbe Contention)
    where TStore : RelationalPhysicalDocumentStore
{
    public TStore CreateStore(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess? access = null) =>
        StoreFactory(manifest, routes, access ?? DocumentStoreAccess.Global);

    public IBoundedDocumentMutationStore CreateMutationRuntime(
        TStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        MutationRuntimeFactory(store, manifest, route, provider);

    public IBoundedDocumentStore CreateQueryRuntime(
        TStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        QueryRuntimeFactory(store, manifest, route, provider);

    public DbConnection CreateConnection() => ConnectionFactory();

    public RelationalPhysicalDocumentDialect CreateDialect() => DialectFactory();
}

internal static class RelationalBoundedMutationServerAssertions
{
    public static async Task TransitionUpdatesExactIndexedIdentitySetAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        await SaveAsync(store, "pending-a", "pending");
        await SaveAsync(store, "pending-b", "pending");
        await SaveAsync(store, "active", "active");

        var result = await harness.CreateMutationRuntime(
                store,
                model.Manifest,
                model.Target.Routes.Single(),
                model.Target.Provider)
            .ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{harness.Provider.Name}-transition-tracer"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Equal("revoked", await ReadCategoryAsync(store, "pending-a"));
        Assert.Equal("revoked", await ReadCategoryAsync(store, "pending-b"));
        Assert.Equal("active", await ReadCategoryAsync(store, "active"));
    }

    public static async Task ConcurrentRetryReplaysExactResultAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var firstStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        var secondStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        for (var index = 0; index < 5; index++)
            await SaveAsync(firstStore, $"pending-{index}", "pending");
        var request = new DocumentMutation(
            "configurationDocument",
            "revoke-pending",
            $"{harness.Provider.Name}-concurrent-retry");

        var results = await Task.WhenAll(
            harness.CreateMutationRuntime(firstStore, model.Manifest, model.Target.Routes.Single(), model.Target.Provider)
                .ExecuteAsync(request),
            harness.CreateMutationRuntime(secondStore, model.Manifest, model.Target.Routes.Single(), model.Target.Provider)
                .ExecuteAsync(request));

        Assert.Equal(
            [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
            results.Select(result => result.Status).Order().ToArray());
        Assert.All(results, result => Assert.Equal(5, result.AffectedCount));
        for (var index = 0; index < 5; index++)
            Assert.Equal("revoked", await ReadCategoryAsync(firstStore, $"pending-{index}"));
    }

    public static async Task ConcurrentDistinctTransitionsSerializeSelectedSetAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var firstStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        var secondStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        await AssertDistinctTransitionRaceAsync(
            harness.Provider,
            harness.HandlerPrefix,
            model,
            route,
            firstStore,
            secondStore,
            harness.Contention);
    }

    public static async Task DirectConnectionDistinctTransitionSerializesSelectedSetAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        await using var firstConnection = harness.CreateConnection();
        await using var secondConnection = harness.CreateConnection();
        var firstStore = new RelationalPhysicalDocumentStore(
            firstConnection,
            model.Manifest,
            model.Target.Routes,
            harness.CreateDialect(),
            DocumentStoreAccess.Global);
        var secondStore = new RelationalPhysicalDocumentStore(
            secondConnection,
            model.Manifest,
            model.Target.Routes,
            harness.CreateDialect(),
            DocumentStoreAccess.Global);
        await AssertDistinctTransitionRaceAsync(
            harness.Provider,
            harness.HandlerPrefix,
            model,
            route,
            firstStore,
            secondStore,
            harness.Contention);
    }

    private static async Task AssertDistinctTransitionRaceAsync<TStore>(
        ProviderIdentity provider,
        string handlerPrefix,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        ExecutableStorageRoute route,
        TStore firstStore,
        TStore secondStore,
        RelationalLockContentionProbe contention)
        where TStore : RelationalPhysicalDocumentStore
    {
        await SaveAsync(firstStore, "distinct-transition-race", "pending");
        var firstSelected = NewSignal<int>();
        var releaseFirst = NewSignal();
        var secondAttempt = NewSignal<int>();
        var firstRuntime = CreateRuntime(firstStore, model, route, provider, handlerPrefix, async (point, connection, transaction, ct) =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                return;
            firstSelected.TrySetResult(await contention.ReadSessionId(connection, transaction, ct));
            await releaseFirst.Task;
        });
        var secondRuntime = CreateRuntime(secondStore, model, route, provider, handlerPrefix, async (point, connection, transaction, ct) =>
        {
            if (point == RelationalPhysicalMutationExecutionPoint.BeforeSelection)
                secondAttempt.TrySetResult(await contention.ReadSessionId(connection, transaction, ct));
        });

        var first = firstRuntime.ExecuteAsync(Transition($"{provider.Name}-distinct-transition-first"));
        var blocker = await firstSelected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = secondRuntime.ExecuteAsync(Transition($"{provider.Name}-distinct-transition-second"));
        var blocked = await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await contention.WaitUntilBlockedAsync(blocked, blocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);

        Assert.Equal(new long[] { 0, 1 }, results.Select(result => result.AffectedCount).Order().ToArray());
        Assert.Equal("revoked", await ReadCategoryAsync(firstStore, "distinct-transition-race"));
    }

    public static async Task ConcurrentDistinctDeletesSerializeSelectedSetAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: true,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var firstStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        var secondStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        await SaveAsync(firstStore, "distinct-delete-race", "authorization-a", 1);
        var firstSelected = NewSignal<int>();
        var releaseFirst = NewSignal();
        var secondAttempt = NewSignal<int>();
        var firstRuntime = CreateRuntime(firstStore, model, route, harness.Provider, harness.HandlerPrefix, async (point, connection, transaction, ct) =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                return;
            firstSelected.TrySetResult(await harness.Contention.ReadSessionId(connection, transaction, ct));
            await releaseFirst.Task;
        });
        var secondRuntime = CreateRuntime(secondStore, model, route, harness.Provider, harness.HandlerPrefix, async (point, connection, transaction, ct) =>
        {
            if (point == RelationalPhysicalMutationExecutionPoint.BeforeSelection)
                secondAttempt.TrySetResult(await harness.Contention.ReadSessionId(connection, transaction, ct));
        });

        var first = firstRuntime.ExecuteAsync(Delete($"{harness.Provider.Name}-distinct-delete-first"));
        var blocker = await firstSelected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = secondRuntime.ExecuteAsync(Delete($"{harness.Provider.Name}-distinct-delete-second"));
        var blocked = await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await harness.Contention.WaitUntilBlockedAsync(blocked, blocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);

        Assert.Equal(new long[] { 0, 1 }, results.Select(result => result.AffectedCount).Order().ToArray());
        Assert.Null(await firstStore.LoadAsync("configurationDocument", "distinct-delete-race"));
    }

    public static async Task OrdinaryCrudSerializesWithSelectedSetAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var mutationStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        var ordinaryStore = harness.CreateStore(model.Manifest, model.Target.Routes);

        await SaveAsync(mutationStore, "ordinary-save-race", "pending");
        var saveMutation = HoldAfterSelection(
            mutationStore,
            model,
            route,
            harness.Provider,
            harness.HandlerPrefix,
            harness.Contention,
            out var saveSelected,
            out var releaseSave);
        var saveAttempt = NewSignal<int>();
        ordinaryStore.WriteInterceptor = async (point, operation, connection, transaction, ct) =>
        {
            if (point == RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock &&
                operation == RelationalPhysicalWriteOperation.Save)
                saveAttempt.TrySetResult(await harness.Contention.ReadSessionId(connection, transaction, ct));
        };
        var mutationBeforeSave = saveMutation.ExecuteAsync(Transition($"{harness.Provider.Name}-ordinary-save-race"));
        var saveBlocker = await saveSelected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var save = ordinaryStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "ordinary-save-race",
            "1",
            "{\"category\":\"active\",\"priority\":1}"));
        var blockedSave = await saveAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await harness.Contention.WaitUntilBlockedAsync(blockedSave, saveBlocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseSave.TrySetResult();
        }
        var saved = await save;
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(3, saved.Document!.Version);
        Assert.Equal(1, (await mutationBeforeSave).AffectedCount);
        var persistedAfterSave = await mutationStore.LoadAsync("configurationDocument", "ordinary-save-race");
        Assert.Equal(3, persistedAfterSave!.Version);
        Assert.Equal("active", Category(persistedAfterSave));

        await SaveAsync(mutationStore, "ordinary-delete-race", "pending");
        var deleteMutation = HoldAfterSelection(
            mutationStore,
            model,
            route,
            harness.Provider,
            harness.HandlerPrefix,
            harness.Contention,
            out var deleteSelected,
            out var releaseDelete);
        var deleteAttempt = NewSignal<int>();
        ordinaryStore.WriteInterceptor = async (point, operation, connection, transaction, ct) =>
        {
            if (point == RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock &&
                operation == RelationalPhysicalWriteOperation.Delete)
                deleteAttempt.TrySetResult(await harness.Contention.ReadSessionId(connection, transaction, ct));
        };
        var mutationBeforeDelete = deleteMutation.ExecuteAsync(Transition($"{harness.Provider.Name}-ordinary-delete-race"));
        var deleteBlocker = await deleteSelected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var delete = ordinaryStore.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            "ordinary-delete-race"));
        var blockedDelete = await deleteAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await harness.Contention.WaitUntilBlockedAsync(blockedDelete, deleteBlocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseDelete.TrySetResult();
        }
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await delete).Status);
        Assert.Equal(1, (await mutationBeforeDelete).AffectedCount);
        Assert.Null(await mutationStore.LoadAsync("configurationDocument", "ordinary-delete-race"));
    }

    public static async Task LinkedOrdinaryCrudInterleavingsSerializeAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        foreach (var form in new[]
                 {
                     PhysicalStorageForm.SharedDocuments,
                     PhysicalStorageForm.DedicatedDocumentTable
                 })
        {
            var model = RelationalPhysicalStorageTestModels.Create(
                form,
                harness.Provider,
                includePriority: true,
                normalizer: harness.Normalizer,
                mutationOptions: new(IncludeCategoryTransition: true, IncludeRangeDelete: true));
            await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
            var route = model.Target.Routes.Single();
            await AssertLinkedOrdinaryCrudInterleavingsAsync(
                new LinkedCrudInterleavingContext<TStore>(
                    harness.Provider,
                    harness.HandlerPrefix,
                    model,
                    route,
                    harness.CreateStore(model.Manifest, model.Target.Routes),
                    harness.CreateStore(model.Manifest, model.Target.Routes),
                    $"{form}-pooled",
                    harness.Contention));

            await using var mutationConnection = harness.CreateConnection();
            await using var ordinaryConnection = harness.CreateConnection();
            var mutationStore = new RelationalPhysicalDocumentStore(
                mutationConnection,
                model.Manifest,
                model.Target.Routes,
                harness.CreateDialect(),
                DocumentStoreAccess.Global);
            var ordinaryStore = new RelationalPhysicalDocumentStore(
                ordinaryConnection,
                model.Manifest,
                model.Target.Routes,
                harness.CreateDialect(),
                DocumentStoreAccess.Global);
            await AssertLinkedOrdinaryCrudInterleavingsAsync(
                new LinkedCrudInterleavingContext<RelationalPhysicalDocumentStore>(
                    harness.Provider,
                    harness.HandlerPrefix,
                    model,
                    route,
                    mutationStore,
                    ordinaryStore,
                    $"{form}-direct",
                    harness.Contention));
        }
    }

    public static async Task LargeSelectionUsesConstantSetBasedLockCommandsAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        for (var index = 0; index < 128; index++)
            await SaveAsync(store, $"large-selection-{index}", "pending");
        var lockCommands = 0;
        var runtime = CreateRuntime(
            store,
            model,
            route,
            harness.Provider,
            harness.HandlerPrefix,
            (point, _, _, _) =>
            {
                if (point == RelationalPhysicalMutationExecutionPoint.BeforeRowLockCommand)
                    Interlocked.Increment(ref lockCommands);
                return ValueTask.CompletedTask;
            });

        var result = await runtime.ExecuteAsync(
            Transition($"{harness.Provider.Name}-large-set-based-lock-selection"));

        Assert.Equal(128, result.AffectedCount);
        Assert.Equal(2, lockCommands);
        Assert.Equal(128, await CountAsync(
            RelationalPhysicalQueryRuntime.Create(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                harness.HandlerPrefix),
            "revoked"));
    }

    private static async Task AssertLinkedOrdinaryCrudInterleavingsAsync<TStore>(
        LinkedCrudInterleavingContext<TStore> context)
        where TStore : RelationalPhysicalDocumentStore
    {
        var mutationFirstId = $"mutation-first-save-{context.Scenario}";
        await SaveAsync(context.MutationStore, mutationFirstId, "pending");
        var mutationFirst = HoldAfterSelection(
            context.MutationStore,
            context.Model,
            context.Route,
            context.Provider,
            context.HandlerPrefix,
            context.Contention,
            out var selected,
            out var releaseMutation);
        var saveAttempt = NewSignal<int>();
        context.OrdinaryStore.WriteInterceptor = async (point, operation, connection, transaction, ct) =>
        {
            if (point == RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock &&
                operation == RelationalPhysicalWriteOperation.Save)
                saveAttempt.TrySetResult(await context.Contention.ReadSessionId(connection, transaction, ct));
        };
        var transition = mutationFirst.ExecuteAsync(
            Transition($"{context.Provider.Name}-{context.Scenario}-mutation-first-save"));
        var blocker = await selected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var save = context.OrdinaryStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            mutationFirstId,
            "1",
            "{\"category\":\"active\",\"priority\":1}"));
        var blocked = await saveAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await context.Contention.WaitUntilBlockedAsync(blocked, blocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseMutation.TrySetResult();
        }
        var saved = await save.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(3, saved.Document!.Version);
        Assert.Equal(1, (await transition.WaitAsync(TimeSpan.FromSeconds(30))).AffectedCount);
        await AssertDocumentAndProjectionAsync(
            context.MutationStore,
            context.Model,
            context.Route,
            context.Provider,
            context.HandlerPrefix,
            mutationFirstId,
            "active",
            3);

        await AssertCrudFirstMutationAsync(
            context,
            $"save-transition-{context.Scenario}",
            "pending",
            RelationalPhysicalWriteOperation.Save,
            Transition($"{context.Provider.Name}-{context.Scenario}-save-transition"));
        await AssertCrudFirstMutationAsync(
            context,
            $"delete-transition-{context.Scenario}",
            "pending",
            RelationalPhysicalWriteOperation.Delete,
            Transition($"{context.Provider.Name}-{context.Scenario}-delete-transition"));
        await AssertCrudFirstMutationAsync(
            context,
            $"save-delete-{context.Scenario}",
            "authorization-a",
            RelationalPhysicalWriteOperation.Save,
            Delete($"{context.Provider.Name}-{context.Scenario}-save-delete"));
        await AssertCrudFirstMutationAsync(
            context,
            $"delete-delete-{context.Scenario}",
            "authorization-a",
            RelationalPhysicalWriteOperation.Delete,
            Delete($"{context.Provider.Name}-{context.Scenario}-delete-delete"));
    }

    private static async Task AssertCrudFirstMutationAsync<TStore>(
        LinkedCrudInterleavingContext<TStore> context,
        string id,
        string initialCategory,
        RelationalPhysicalWriteOperation ordinaryOperation,
        DocumentMutation mutation)
        where TStore : RelationalPhysicalDocumentStore
    {
        await SaveAsync(context.MutationStore, id, initialCategory);
        var ordinaryLocked = NewSignal<int>();
        var releaseOrdinary = NewSignal();
        context.OrdinaryStore.WriteInterceptor = async (point, operation, connection, transaction, ct) =>
        {
            if (point != RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock || operation != ordinaryOperation)
                return;
            ordinaryLocked.TrySetResult(await context.Contention.ReadSessionId(connection, transaction, ct));
            await releaseOrdinary.Task;
        };
        var mutationAttempt = NewSignal<int>();
        var runtime = CreateRuntime(
            context.MutationStore,
            context.Model,
            context.Route,
            context.Provider,
            context.HandlerPrefix,
            async (point, connection, transaction, ct) =>
            {
                if (point == RelationalPhysicalMutationExecutionPoint.AfterCandidateDiscovery)
                    mutationAttempt.TrySetResult(await context.Contention.ReadSessionId(connection, transaction, ct));
            });
        var ordinary = ordinaryOperation == RelationalPhysicalWriteOperation.Save
            ? context.OrdinaryStore.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                id,
                "1",
                "{\"category\":\"active\",\"priority\":1}"))
            : context.OrdinaryStore.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id));
        var blocker = await ordinaryLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var bounded = runtime.ExecuteAsync(mutation);
        var blocked = await mutationAttempt.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await context.Contention.WaitUntilBlockedAsync(blocked, blocker, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseOrdinary.TrySetResult();
        }
        var ordinaryResult = await ordinary.WaitAsync(TimeSpan.FromSeconds(30));
        var boundedResult = await bounded.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, boundedResult.AffectedCount);
        if (ordinaryOperation == RelationalPhysicalWriteOperation.Delete)
        {
            Assert.Equal(DocumentStoreWriteStatus.Deleted, ordinaryResult.Status);
            Assert.Null(await context.MutationStore.LoadAsync("configurationDocument", id));
            return;
        }

        Assert.Equal(DocumentStoreWriteStatus.Saved, ordinaryResult.Status);
        Assert.Equal(2, ordinaryResult.Document!.Version);
        await AssertDocumentAndProjectionAsync(
            context.MutationStore,
            context.Model,
            context.Route,
            context.Provider,
            context.HandlerPrefix,
            id,
            "active",
            2);
    }

    private static async Task AssertDocumentAndProjectionAsync(
        RelationalPhysicalDocumentStore store,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string handlerPrefix,
        string id,
        string expectedCategory,
        long expectedVersion)
    {
        var persisted = await store.LoadAsync("configurationDocument", id);
        Assert.NotNull(persisted);
        Assert.Equal(expectedVersion, persisted.Version);
        Assert.Equal(expectedCategory, Category(persisted));
        var query = RelationalPhysicalQueryRuntime.Create(
            store,
            model.Manifest,
            route,
            provider,
            handlerPrefix);
        var projected = await QueryCategoryAsync(query, expectedCategory);
        Assert.Contains(projected.Documents, document => document.Id == id);
        foreach (var staleCategory in new[] { "pending", "revoked", "authorization-a" }
                     .Where(category => category != expectedCategory))
        {
            var stale = await QueryCategoryAsync(query, staleCategory);
            Assert.DoesNotContain(stale.Documents, document => document.Id == id);
        }
    }

    public static async Task PhysicalFormsExecuteTransitionAndRangeDeleteAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        foreach (var form in Enum.GetValues<PhysicalStorageForm>())
        {
            var model = RelationalPhysicalStorageTestModels.Create(
                form,
                harness.Provider,
                includePriority: true,
                normalizer: harness.Normalizer,
                mutationOptions: new(IncludeCategoryTransition: true, IncludeRangeDelete: true));
            await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
            var store = harness.CreateStore(model.Manifest, model.Target.Routes);
            await SaveAsync(store, "pending", "pending", 1);
            await SaveAsync(store, "expired-a", "authorization-a", 1);
            await SaveAsync(store, "expired-b", "authorization-a", 9);
            await SaveAsync(store, "future", "authorization-a", 10);
            await SaveAsync(store, "other-authorization", "authorization-b", 1);
            var route = model.Target.Routes.Single();
            var mutations = harness.CreateMutationRuntime(store, model.Manifest, route, model.Target.Provider);

            var transitioned = await mutations.ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{harness.Provider.Name}-{form}-transition"));
            var deleted = await mutations.ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "prune-by-category-cutoff",
                $"{harness.Provider.Name}-{form}-delete",
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
            var query = harness.CreateQueryRuntime(store, model.Manifest, route, model.Target.Provider);
            Assert.Equal(1, await query.CountAsync(new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "revoked"))],
                resultOperation: BoundedQueryResultOperation.Count)));
        }
    }

    public static async Task TypedTransitionsPreserveCanonicalAndProjectedValuesAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        foreach (var form in Enum.GetValues<PhysicalStorageForm>())
        {
            var model = RelationalPhysicalStorageTestModels.Create(
                form,
                harness.Provider,
                includePriority: true,
                normalizer: harness.Normalizer,
                mutationOptions: new(IncludeTypedTransitions: true));
            await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
            var store = harness.CreateStore(model.Manifest, model.Target.Routes);
            var saved = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                "typed",
                "1",
                TypedContent(1, true, "alpha", "TOKEN_A", "2026-01-01T00:00:00Z", "11111111-1111-1111-1111-111111111111")));
            Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
            Assert.Equal(1, saved.Document!.Version);
            var route = model.Target.Routes.Single();
            var mutations = harness.CreateMutationRuntime(store, model.Manifest, route, model.Target.Provider);
            var queries = harness.CreateQueryRuntime(store, model.Manifest, route, model.Target.Provider);
            var expectedVersion = 1L;

            await TransitionAndAssertAsync("raise-priority", "priority", "2", JsonValueKind.Number);
            await TransitionAndAssertAsync("transition-enabled", "enabled", "false", JsonValueKind.False);
            await TransitionAndAssertAsync("transition-title", "title", "bravo", JsonValueKind.String);
            await TransitionAndAssertAsync("transition-token", "token", "TOKEN_B", JsonValueKind.String);
            await TransitionAndAssertAsync("transition-dueAt", "dueAt", "2026-02-02T00:00:00Z", JsonValueKind.String);
            await TransitionAndAssertAsync(
                "transition-externalId",
                "externalId",
                "22222222-2222-2222-2222-222222222222",
                JsonValueKind.String);

            var resaved = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                "typed",
                "1",
                TypedContent(3, true, "charlie", "TOKEN_C", "2026-03-03T00:00:00Z", "33333333-3333-3333-3333-333333333333"),
                expectedVersion));
            Assert.Equal(DocumentStoreWriteStatus.Saved, resaved.Status);
            Assert.Equal(++expectedVersion, resaved.Document!.Version);
            var reloaded = await store.LoadAsync("configurationDocument", "typed");
            Assert.NotNull(reloaded);
            Assert.Equal(expectedVersion, reloaded.Version);
            using var canonical = JsonDocument.Parse(reloaded.ContentJson);
            Assert.Equal(JsonValueKind.Number, canonical.RootElement.GetProperty("priority").ValueKind);
            Assert.Equal(3, canonical.RootElement.GetProperty("priority").GetInt32());
            Assert.Equal(JsonValueKind.True, canonical.RootElement.GetProperty("enabled").ValueKind);
            Assert.Equal("charlie", canonical.RootElement.GetProperty("title").GetString());
            Assert.Equal("TOKEN_C", canonical.RootElement.GetProperty("token").GetString());
            Assert.Equal("2026-03-03T00:00:00Z", canonical.RootElement.GetProperty("dueAt").GetString());
            Assert.Equal(
                "33333333-3333-3333-3333-333333333333",
                canonical.RootElement.GetProperty("externalId").GetString());
            Assert.Equal(1, await CountFieldAsync(queries, "priority", "3"));
            Assert.Equal(1, await CountFieldAsync(queries, "enabled", "true"));
            Assert.Equal(1, await CountFieldAsync(queries, "title", "charlie"));
            Assert.Equal(1, await CountFieldAsync(queries, "token", "TOKEN_C"));
            Assert.Equal(1, await CountFieldAsync(queries, "dueAt", "2026-03-03T00:00:00Z"));
            Assert.Equal(1, await CountFieldAsync(
                queries,
                "externalId",
                "33333333-3333-3333-3333-333333333333"));

            async Task TransitionAndAssertAsync(
                string mutation,
                string field,
                string target,
                JsonValueKind valueKind)
            {
                var result = await mutations.ExecuteAsync(new DocumentMutation(
                    "configurationDocument",
                    mutation,
                    $"{harness.Provider.Name}-{form}-{mutation}"));
                Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
                var document = await store.LoadAsync("configurationDocument", "typed");
                Assert.NotNull(document);
                Assert.Equal(++expectedVersion, document.Version);
                using var json = JsonDocument.Parse(document.ContentJson);
                var value = json.RootElement.GetProperty(field);
                Assert.Equal(valueKind, value.ValueKind);
                Assert.Equal(target, valueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? value.GetRawText()
                    : value.GetString());
                Assert.Equal(1, await CountFieldAsync(queries, field, target));
            }
        }
    }

    public static async Task MutationScopeIsInheritedFromStoreSessionAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            harness.Provider,
            includePriority: false,
            scoped: true,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var tenantA = harness.CreateStore(
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = harness.CreateStore(
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));
        await SaveAsync(tenantA, "same-id", "pending");
        await SaveAsync(tenantB, "same-id", "pending");

        var result = await harness.CreateMutationRuntime(tenantA, model.Manifest, route, model.Target.Provider)
            .ExecuteAsync(new DocumentMutation(
                "configurationDocument",
                "revoke-pending",
                $"{harness.Provider.Name}-tenant-a-transition"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.Equal("revoked", await ReadCategoryAsync(tenantA, "same-id"));
        Assert.Equal("pending", await ReadCategoryAsync(tenantB, "same-id"));
    }

    public static async Task FailureBeforeCommitRollsBackAndRetryCompletesAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        await SaveAsync(store, "pending", "pending");
        var request = Transition($"{harness.Provider.Name}-rollback");
        var mutations = RelationalPhysicalMutationRuntime.Create(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                harness.Provider.Name,
                harness.HandlerPrefix),
            point => point == RelationalPhysicalMutationExecutionPoint.BeforeCommit
                ? ValueTask.FromException(new SimulatedMutationFailureException())
                : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<SimulatedMutationFailureException>(() => mutations.ExecuteAsync(request));

        Assert.Equal("pending", await ReadCategoryAsync(store, "pending"));
        Assert.Equal(1, await CountAsync(harness.CreateQueryRuntime(store, model.Manifest, route, model.Target.Provider), "pending"));
        Assert.Equal(0, await CountAsync(harness.CreateQueryRuntime(store, model.Manifest, route, model.Target.Provider), "revoked"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await harness.CreateMutationRuntime(store, model.Manifest, route, model.Target.Provider).ExecuteAsync(request));
    }

    public static async Task CancellationBeforeCommitRollsBackAndPreservesTokenAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            harness.Provider,
            includePriority: false,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var store = harness.CreateStore(model.Manifest, model.Target.Routes);
        await SaveAsync(store, "pending", "pending");
        var request = Transition($"{harness.Provider.Name}-cancellation");
        using var cancellation = new CancellationTokenSource();
        var mutations = RelationalPhysicalMutationRuntime.Create(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                harness.Provider.Name,
                harness.HandlerPrefix),
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
            await harness.CreateMutationRuntime(store, model.Manifest, route, model.Target.Provider).ExecuteAsync(request));
    }

    public static async Task AcknowledgementLossRestartAndProviderUpgradeReplayAsync<TStore>(
        RelationalMutationServerHarness<TStore> harness)
        where TStore : RelationalPhysicalDocumentStore
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            harness.Provider,
            includePriority: true,
            normalizer: harness.Normalizer,
            mutationOptions: new(IncludeCategoryTransition: true, IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(model.Target, harness.CreateExecutor());
        var route = model.Target.Routes.Single();
        var firstStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        await SaveAsync(firstStore, "pending-a", "pending");
        await SaveAsync(firstStore, "pending-b", "pending");
        var operationId = $"{harness.Provider.Name}-ack-loss-upgrade";
        var request = Transition(operationId);
        var loseAcknowledgement = true;
        var mutations = RelationalPhysicalMutationRuntime.Create(
            new RelationalPhysicalMutationRuntimeContext(
                firstStore,
                model.Manifest,
                route,
                model.Target.Provider,
                harness.Provider.Name,
                harness.HandlerPrefix),
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

        var restartedStore = harness.CreateStore(model.Manifest, model.Target.Routes);
        var upgradedProvider = new ProviderIdentity(harness.Provider.Name, "2.0.0");
        var restarted = harness.CreateMutationRuntime(restartedStore, model.Manifest, route, upgradedProvider);
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
        return Category(document);
    }

    private static string Category(DocumentEnvelope document) =>
        JsonDocument.Parse(document.ContentJson).RootElement.GetProperty("category").GetString()!;

    private static Task<long> CountAsync(IBoundedDocumentStore store, string category) =>
        store.CountAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))],
            resultOperation: BoundedQueryResultOperation.Count));

    private static Task<DocumentQueryResult> QueryCategoryAsync(IBoundedDocumentStore store, string category) =>
        store.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))]));

    private static Task<long> CountFieldAsync(IBoundedDocumentStore store, string field, string value) =>
        store.CountAsync(new DocumentQuery(
            "configurationDocument",
            $"list-by-{field}",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(field, value))],
            resultOperation: BoundedQueryResultOperation.Count));

    private static string TypedContent(
        int priority,
        bool enabled,
        string title,
        string token,
        string dueAt,
        string externalId) => JsonSerializer.Serialize(new
        {
            category = "typed",
            priority,
            enabled,
            title,
            token,
            dueAt,
            externalId
        });

    private static DocumentMutation Transition(string operationId) =>
        new("configurationDocument", "revoke-pending", operationId);

    private static DocumentMutation Delete(string operationId) =>
        new(
            "configurationDocument",
            "prune-by-category-cutoff",
            operationId,
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "authorization-a")),
                DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
            ]);

    private static IBoundedDocumentMutationStore HoldAfterSelection<TStore>(
        TStore store,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string handlerPrefix,
        RelationalLockContentionProbe contention,
        out TaskCompletionSource<int> selected,
        out TaskCompletionSource release)
        where TStore : RelationalPhysicalDocumentStore
    {
        selected = NewSignal<int>();
        release = NewSignal();
        var selectedSignal = selected;
        var releaseSignal = release;
        return CreateRuntime(store, model, route, provider, handlerPrefix, async (point, connection, transaction, ct) =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                return;
            selectedSignal.TrySetResult(await contention.ReadSessionId(connection, transaction, ct));
            await releaseSignal.Task;
        });
    }

    private static IBoundedDocumentMutationStore CreateRuntime<TStore>(
        TStore store,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string handlerPrefix,
        RelationalPhysicalMutationInterceptor intercept)
        where TStore : RelationalPhysicalDocumentStore =>
        RelationalPhysicalMutationRuntime.CreateWithInterceptor(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                model.Manifest,
                route,
                model.Target.Provider,
                provider.Name,
                handlerPrefix),
            intercept);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record LinkedCrudInterleavingContext<TStore>(
        ProviderIdentity Provider,
        string HandlerPrefix,
        (StorageManifest Manifest, PhysicalSchemaTarget Target) Model,
        ExecutableStorageRoute Route,
        TStore MutationStore,
        TStore OrdinaryStore,
        string Scenario,
        RelationalLockContentionProbe Contention)
        where TStore : RelationalPhysicalDocumentStore;

    private sealed class SimulatedMutationFailureException : Exception;

    private sealed class SimulatedMutationAcknowledgementLossException : Exception;
}
