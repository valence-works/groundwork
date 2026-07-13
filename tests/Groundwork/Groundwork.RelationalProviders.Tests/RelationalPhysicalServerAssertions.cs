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
            CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(target, cancellationToken);

        public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            CancellationToken cancellationToken)
        {
            var acknowledgement = await inner.ApplyOperationAsync(target, operation, cancellationToken);
            if (Interlocked.Exchange(ref lost, 1) == 0)
                throw new SimulatedAcknowledgementLossException();
            return acknowledgement;
        }

        public ValueTask RecordAppliedStateAsync(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            CancellationToken cancellationToken) =>
            inner.RecordAppliedStateAsync(state, expectedAppliedTargetFingerprint, cancellationToken);
    }

    private sealed class SimulatedAcknowledgementLossException : Exception;
}
