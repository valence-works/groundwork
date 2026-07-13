using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
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
        await WaitForCancellationAsync(staleLock.OwnershipLost);
        var successor = createExecutor(null, null);
        await using (var successorLock = await successor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleApplication);
            Assert.Equal(0, await countOperationEvidence(operation.Identity, operation.Fingerprint));
            Assert.False(await tableExists(model.Target.Routes.Single().PrimaryStorage.Name.Identifier));
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
        await WaitForCancellationAsync(staleLock.OwnershipLost);
        await using (await createExecutor(null, null).AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => stalePublish);
            Assert.Equal(0, await countAppliedState(model.Target.ManifestIdentity.Value, model.Target.Provider.Name));
        }
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, createExecutor(null, null))).Outcome);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => canceled.TrySetResult());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
