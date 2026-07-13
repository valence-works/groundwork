using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalPhysicalServerAssertions
{
    public static async Task LostOperationLockCannotPublishEvidenceAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countOperationEvidence,
        Func<string, Task<bool>> tableExists)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, null);
        await using var staleLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(model.Target.Identity, staleLock, CancellationToken.None);
        var operation = PhysicalSchemaDiffPlanner.Plan(model.Target, history, DateTimeOffset.UtcNow).Operations
            .First(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation);
        var staleApplication = executor.ApplyOperationAsync(
            model.Target.Identity,
            operation,
            staleLock,
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => staleApplication);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countOperationEvidence(operation.Identity, operation.Fingerprint));
        Assert.False(await tableExists(model.Target.Routes.Single().PrimaryStorage.Name.Identifier));

        var successor = createExecutor(null, null);
        await using (var successorLock = await successor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
            var acknowledgement = await successor.ApplyOperationAsync(
                model.Target.Identity,
                operation,
                successorLock,
                CancellationToken.None);
            Assert.Equal(operation.Identity, acknowledgement.Identity);
        }
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor(null, null))).Outcome);
    }

    public static async Task LostStateLockCannotPublishAppliedStateAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countAppliedState)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(null, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await using var staleLock = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(model.Target.Identity, staleLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(model.Target, history, DateTimeOffset.UtcNow);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            acknowledgements.Add(await executor.ApplyOperationAsync(
                model.Target.Identity,
                operation,
                staleLock,
                CancellationToken.None));
        }
        var state = plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
        var stalePublish = executor.RecordAppliedStateAsync(
            state,
            plan.ExpectedAppliedTargetFingerprint,
            staleLock,
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stalePublish);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countAppliedState(model.Target.ManifestIdentity.Value, model.Target.Provider.Name));

        await using (await createExecutor(null, null).AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
        }
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor(null, null))).Outcome);
    }

    public static async Task NonProviderFailureAfterRealLockLossUsesStableErrorAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<PhysicalSchemaAppliedState, CancellationToken, Task>?, IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countAppliedState)
    {
        const string injectedMessage = "simulated invalid transaction after relational lock loss";
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        long sessionId = 0;
        var executor = createExecutor(async (_, _) =>
        {
            await terminateSession(sessionId);
            throw new InvalidOperationException(injectedMessage);
        });
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);
        sessionId = readSessionId(applicationLock);
        var state = await PreparePendingStateAsync(model.Target, executor, applicationLock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecordAppliedStateAsync(
            state,
            null,
            applicationLock,
            CancellationToken.None).AsTask());

        Assert.Contains("lock session", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(injectedMessage, exception.ToString(), StringComparison.Ordinal);
        Assert.True(applicationLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countAppliedState(model.Target.ManifestIdentity.Value, model.Target.Provider.Name));
    }

    public static async Task OrdinaryInvalidOperationPreservesOwnedLockAndOriginalErrorAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<Func<PhysicalSchemaAppliedState, CancellationToken, Task>?, IPhysicalSchemaExecutor> createExecutor)
    {
        const string injectedMessage = "ordinary schema validation failure";
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var remainingFailures = 1;
        using var callerCancellation = new CancellationTokenSource();
        var executor = createExecutor((_, _) =>
        {
            if (Interlocked.Exchange(ref remainingFailures, 0) != 1)
                return Task.CompletedTask;
            callerCancellation.Cancel();
            return Task.FromException(new InvalidOperationException(injectedMessage));
        });
        await using var applicationLock = await executor.AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);
        var state = await PreparePendingStateAsync(model.Target, executor, applicationLock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecordAppliedStateAsync(
            state,
            null,
            applicationLock,
            callerCancellation.Token).AsTask());

        Assert.Equal(injectedMessage, exception.Message);
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
        await executor.RecordAppliedStateAsync(state, null, applicationLock, CancellationToken.None);
        Assert.False(applicationLock.OwnershipLost.IsCancellationRequested);
    }

    public static async Task LostBackfillLockCannotCommitDataOrEvidenceAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<
            Func<PhysicalSchemaOperation, CancellationToken, Task>?,
            Func<PhysicalSchemaAppliedState, CancellationToken, Task>?,
            IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession,
        Func<string, string, Task<long>> countOperationEvidence,
        Func<string, string, Task<long>> countProjectedValues)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor(null, null));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await createStore(initial.Manifest, initial.Target.Routes).SaveAsync(
            new SaveDocumentRequest(
                "configurationDocument",
                "backfill-lock-loss",
                "1",
                "{\"category\":\"tools\",\"priority\":42}",
                0))).Status);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = createExecutor(async (operation, _) =>
        {
            if (operation is not BackfillCanonicalJsonOperation)
                return;
            entered.TrySetResult();
            await release.Task;
        }, null);
        await using var staleLock = await executor.AcquireApplicationLockAsync(additive.Target.Identity, CancellationToken.None);
        var history = await executor.ReadHistoryAsync(additive.Target.Identity, staleLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(additive.Target, history, DateTimeOffset.UtcNow);
        Task<PhysicalSchemaOperationAcknowledgement>? staleBackfill = null;
        BackfillCanonicalJsonOperation? backfill = null;
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            if (operation is BackfillCanonicalJsonOperation candidate)
            {
                backfill = candidate;
                staleBackfill = executor.ApplyOperationAsync(
                    additive.Target.Identity,
                    operation,
                    staleLock,
                    CancellationToken.None).AsTask();
                break;
            }
            await executor.ApplyOperationAsync(additive.Target.Identity, operation, staleLock, CancellationToken.None);
        }
        Assert.NotNull(backfill);
        Assert.NotNull(staleBackfill);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await terminateSession(readSessionId(staleLock));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => staleBackfill);
        Assert.True(staleLock.OwnershipLost.IsCancellationRequested);
        Assert.Equal(0, await countOperationEvidence(backfill.Identity, backfill.Fingerprint));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal(0, await countProjectedValues(table, priority.Column.Identifier));

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor(null, null))).Outcome);
        Assert.Equal(1, await countProjectedValues(table, priority.Column.Identifier));
    }

    public static async Task TerminatedApplicationLockDisposalIsIdempotentAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<IPhysicalSchemaApplicationLock, long> readSessionId,
        Func<long, Task> terminateSession)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var applicationLock = await createExecutor().AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None);

        await terminateSession(readSessionId(applicationLock));
        await Task.WhenAll(
            applicationLock.DisposeAsync().AsTask(),
            applicationLock.DisposeAsync().AsTask());

        await using var successor = await createExecutor().AcquireApplicationLockAsync(
            model.Target.Identity,
            CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static async Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor)
    {
        var concurrent = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        var outcomes = await Task.WhenAll(
            PhysicalSchemaApplication.ApplyAsync(concurrent.Target, createExecutor()),
            PhysicalSchemaApplication.ApplyAsync(concurrent.Target, createExecutor()));
        Assert.Equal(1, outcomes.Count(result => result.Outcome == PhysicalSchemaApplicationOutcome.Applied));
        Assert.Equal(1, outcomes.Count(result => result.Outcome == PhysicalSchemaApplicationOutcome.NoChanges));

        var lost = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        await Assert.ThrowsAsync<SimulatedAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(lost.Target, new AcknowledgementLosingExecutor(createExecutor())));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(lost.Target, createExecutor())).Outcome);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(lost.Target, createExecutor())).Outcome);
    }

    public static async Task UnpublishedBackfillAcknowledgementLossReplaysInterleavedWritesAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        string handlerPrefix)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor());
        var oldRouteStore = createStore(initial.Manifest, initial.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("before-loss", 41))).Status);

        var acknowledgementLosing = new BackfillAcknowledgementLosingExecutor(createExecutor());
        await Assert.ThrowsAsync<SimulatedBackfillAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                acknowledgementLosing));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("between-attempts", 42))).Status);

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor())).Outcome);
        var currentStore = createStore(additive.Manifest, additive.Target.Routes);
        var route = additive.Target.Routes.Single();
        var queries = RelationalPhysicalQueryRuntime.Create(
            currentStore,
            additive.Manifest,
            route,
            provider,
            handlerPrefix);
        Assert.Equal(1, await CountPriorityAsync(queries, "41"));
        Assert.Equal(1, await CountPriorityAsync(queries, "42"));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor())).Outcome);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("after-publication", 43))).Status);
        var publishedExecutor = createExecutor();
        await using (var applicationLock = await publishedExecutor.AcquireApplicationLockAsync(
                         additive.Target.Identity,
                         CancellationToken.None))
        {
            var acknowledgement = await publishedExecutor.ApplyOperationAsync(
                additive.Target.Identity,
                acknowledgementLosing.Backfill!,
                applicationLock,
                CancellationToken.None);
            Assert.Equal(acknowledgementLosing.Acknowledgement, acknowledgement);
        }
        Assert.Equal(0, await CountPriorityAsync(queries, "43"));

        static SaveDocumentRequest Save(string id, int priority) =>
            new("configurationDocument", id, "1", $"{{\"category\":\"tools\",\"priority\":{priority}}}", 0);

        static Task<long> CountPriorityAsync(IBoundedDocumentStore queries, string priority) =>
            queries.CountAsync(new DocumentQuery(
                "configurationDocument",
                "find-by-category-priority",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", priority))
                ],
                resultOperation: BoundedQueryResultOperation.Count));
    }

    public static async Task ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            normalizer: normalizer);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var applicationLock = await createExecutor().AcquireApplicationLockAsync(
                model.Target.Identity,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(90));
            await Task.WhenAll(
                applicationLock.DisposeAsync().AsTask(),
                applicationLock.DisposeAsync().AsTask());

            var successor = await createExecutor().AcquireApplicationLockAsync(
                model.Target.Identity,
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await successor.DisposeAsync();
        }
    }

    public static async Task TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore,
        string handlerPrefix,
        PortablePhysicalType type,
        string backfilledJsonValue,
        string liveJsonValue,
        string queryValue,
        int? precision = null,
        int? scale = null)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: true,
            priorityType: type,
            priorityPrecision: precision,
            priorityScale: scale,
            instance: instance,
            normalizer: normalizer);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, createExecutor());
        var initialStore = createStore(initial.Manifest, initial.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "backfilled",
            "1",
            $"{{\"category\":\"tools\",\"priority\":{backfilledJsonValue}}}",
            0))).Status);

        await PhysicalSchemaApplication.ApplyAsync(additive.Target, createExecutor());
        var additiveStore = createStore(additive.Manifest, additive.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await additiveStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "live",
            "1",
            $"{{\"category\":\"tools\",\"priority\":{liveJsonValue}}}",
            0))).Status);
        var route = additive.Target.Routes.Single();
        var queries = RelationalPhysicalQueryRuntime.Create(
            additiveStore,
            additive.Manifest,
            route,
            additive.Target.Provider,
            handlerPrefix);
        var count = await queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", queryValue))
            ],
            resultOperation: BoundedQueryResultOperation.Count));
        Assert.Equal(2, count);
    }

    public static async Task NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        Func<IPhysicalSchemaExecutor> createExecutor,
        Func<Groundwork.Core.Manifests.StorageManifest, IReadOnlyList<ExecutableStorageRoute>, RelationalPhysicalDocumentStore> createStore)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            normalizer: normalizer,
            categoryUnique: true,
            categoryNullable: true);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor());
        var store = createStore(model.Manifest, model.Target.Routes);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-a", "1", "{}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-b", "1", "{}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "present-a", "1", "{\"category\":\"same\"}", 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "present-b", "1", "{\"category\":\"same\"}", 0))).Status);
    }

    private static async Task<PhysicalSchemaAppliedState> PreparePendingStateAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var history = await executor.ReadHistoryAsync(target.Identity, applicationLock, CancellationToken.None);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations.Where(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation))
        {
            acknowledgements.Add(await executor.ApplyOperationAsync(
                target.Identity,
                operation,
                applicationLock,
                CancellationToken.None));
        }
        return plan.Complete(acknowledgements, DateTimeOffset.UtcNow);
    }

    private sealed class AcknowledgementLosingExecutor(IPhysicalSchemaExecutor inner) : IPhysicalSchemaExecutor
    {
        private int lost;

        public ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
            PhysicalSchemaTargetIdentity target,
            CancellationToken cancellationToken) =>
            inner.AcquireApplicationLockAsync(target, cancellationToken);

        public ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(target, applicationLock, cancellationToken);

        public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            var acknowledgement = await inner.ApplyOperationAsync(target, operation, applicationLock, cancellationToken);
            if (Interlocked.Exchange(ref lost, 1) == 0)
                throw new SimulatedAcknowledgementLossException();
            return acknowledgement;
        }

        public ValueTask RecordAppliedStateAsync(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.RecordAppliedStateAsync(state, expectedAppliedTargetFingerprint, applicationLock, cancellationToken);
    }

    private sealed class SimulatedAcknowledgementLossException : Exception;
}
