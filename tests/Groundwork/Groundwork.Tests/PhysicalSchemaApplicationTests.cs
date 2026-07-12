using System.Collections.Concurrent;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalSchemaApplicationTests
{
    [Fact]
    public void Plan_backfills_existing_canonical_documents_before_creating_physical_indexes()
    {
        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(includeSecondProjection: true),
            PhysicalSchemaHistoryState.Empty,
            DateTimeOffset.UtcNow);

        var lastBackfill = plan.Operations
            .Select((operation, index) => (operation, index))
            .Where(item => item.operation.Kind == PhysicalSchemaOperationKind.BackfillCanonicalJson)
            .Max(item => item.index);
        var firstIndex = plan.Operations
            .Select((operation, index) => (operation, index))
            .Where(item => item.operation.Kind == PhysicalSchemaOperationKind.CreatePhysicalIndex)
            .Min(item => item.index);

        Assert.True(lastBackfill < firstIndex);
    }

    [Fact]
    public async Task ApplyIsIdempotentAcrossRestart()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor();

        var applied = await PhysicalSchemaApplication.ApplyAsync(target, executor);
        var restart = await PhysicalSchemaApplication.ApplyAsync(target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        Assert.Equal(1, executor.RecordedStateCount);
        Assert.All(executor.ActualApplicationCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(target.Fingerprint, executor.AppliedState!.TargetFingerprint);
    }

    [Fact]
    public async Task PartialFailureNeverRecordsTargetAndRetryResumesIdempotently()
    {
        var target = CreateTarget(includeSecondProjection: true);
        var executor = new FakePhysicalSchemaExecutor { FailBeforeOperationNumber = 3 };

        await Assert.ThrowsAsync<InjectedExecutionException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.Null(executor.AppliedState);
        Assert.Equal(2, executor.DurableOperations.Count);

        executor.FailBeforeOperationNumber = null;
        var retry = await PhysicalSchemaApplication.ApplyAsync(target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, retry.Outcome);
        Assert.NotNull(executor.AppliedState);
        Assert.All(executor.ActualApplicationCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task OperationAcknowledgementLossNeverRecordsTargetAndRetryReconcilesDurableOperation()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor { LoseNextOperationAcknowledgement = true };

        await Assert.ThrowsAsync<InjectedAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.Null(executor.AppliedState);
        Assert.Single(executor.DurableOperations);

        var retry = await PhysicalSchemaApplication.ApplyAsync(target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, retry.Outcome);
        Assert.NotNull(executor.AppliedState);
        Assert.All(executor.ActualApplicationCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task AppliedStateAcknowledgementLossIsRecoveredAsNoChangeOnRestart()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor { LoseNextStateAcknowledgement = true };

        await Assert.ThrowsAsync<InjectedAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.Equal(target.Fingerprint, executor.AppliedState!.TargetFingerprint);

        var restart = await PhysicalSchemaApplication.ApplyAsync(target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        Assert.Equal(1, executor.RecordedStateCount);
    }

    [Fact]
    public async Task ExecutorFingerprintConflictNeverRecordsTargetState()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor();
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow);
        var first = plan.Operations.First(operation => operation is not RecordPhysicalSchemaAppliedStateOperation);
        executor.SeedConflictingOperation(first.Identity, new string('f', 64));

        var exception = await Assert.ThrowsAsync<PhysicalSchemaFingerprintConflictException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.Equal(first.Identity, exception.OperationIdentity);
        Assert.Null(executor.AppliedState);
    }

    [Fact]
    public async Task CancellationNeverRecordsUnappliedTargetState()
    {
        var target = CreateTarget(includeSecondProjection: false);
        using var cancellation = new CancellationTokenSource();
        var executor = new FakePhysicalSchemaExecutor { CancelBeforeOperationNumber = 2, Cancellation = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor, cancellationToken: cancellation.Token));

        Assert.Null(executor.AppliedState);
        Assert.Single(executor.DurableOperations);
    }

    [Fact]
    public async Task LeaseLossCancelsInFlightOperationAndNeverRecordsTargetState()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor { LoseLeaseDuringOperationNumber = 1 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.True(executor.OperationObservedLeaseLoss);
        Assert.Null(executor.AppliedState);
        Assert.Equal(0, executor.RecordedStateCount);
    }

    [Fact]
    public async Task ConcurrentApplicationsAreExcludedByProviderManifestLock()
    {
        var target = CreateTarget(includeSecondProjection: false);
        var executor = new FakePhysicalSchemaExecutor { OperationDelay = TimeSpan.FromMilliseconds(5) };

        var results = await Task.WhenAll(
            PhysicalSchemaApplication.ApplyAsync(target, executor),
            PhysicalSchemaApplication.ApplyAsync(target, executor));

        Assert.Contains(results, result => result.Outcome == PhysicalSchemaApplicationOutcome.Applied);
        Assert.Contains(results, result => result.Outcome == PhysicalSchemaApplicationOutcome.NoChanges);
        Assert.Equal(1, executor.MaximumConcurrentLockHolders);
        Assert.Equal(2, executor.LockAcquisitionCount);
        Assert.Equal(1, executor.RecordedStateCount);
    }

    private static PhysicalSchemaTarget CreateTarget(bool includeSecondProjection)
    {
        var template = SampleManifests.MetadataManifest();
        var projectedColumns = new List<ProjectedColumnDefinition>
        {
            new("category", "category", PortablePhysicalType.String, IsNullable: false)
        };
        var indexes = new List<PhysicalIndexDefinition>
        {
            new(
                "by-category",
                [new PhysicalIndexColumnDefinition("storage_scope", 0), new PhysicalIndexColumnDefinition("category", 1)])
        };
        if (includeSecondProjection)
        {
            projectedColumns.Add(new ProjectedColumnDefinition("priority", "priority", PortablePhysicalType.Int32));
            indexes.Add(new PhysicalIndexDefinition(
                "by-priority",
                [new PhysicalIndexColumnDefinition("storage_scope", 0), new PhysicalIndexColumnDefinition("priority", 1)]));
        }

        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configuration_entities",
            projectedColumns,
            indexes: indexes);
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition))
                }
            ]
        };
        var resolved = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolved.IsValid, string.Join("; ", resolved.Diagnostics.Select(x => x.Message)));
        var compiled = ExecutableStorageRouteCompiler.Compile(resolved.Definitions);
        Assert.True(compiled.IsValid, string.Join("; ", compiled.Diagnostics.Select(x => x.Message)));
        return new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            new ProviderIdentity("fake-provider", "1.0.0"),
            compiled.Routes);
    }

    private sealed class FakePhysicalSchemaExecutor : IPhysicalSchemaExecutor
    {
        private readonly SemaphoreSlim applicationLock = new(1, 1);
        private readonly ConcurrentDictionary<string, string> durableOperations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> actualApplicationCounts = new(StringComparer.Ordinal);
        private int currentLockHolders;
        private int maximumConcurrentLockHolders;
        private int operationNumber;
        private int lockAcquisitionCount;
        private int recordedStateCount;

        public int? FailBeforeOperationNumber { get; set; }
        public int? CancelBeforeOperationNumber { get; set; }
        public int? LoseLeaseDuringOperationNumber { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public bool LoseNextOperationAcknowledgement { get; set; }
        public bool LoseNextStateAcknowledgement { get; set; }
        public TimeSpan OperationDelay { get; set; }
        public PhysicalSchemaAppliedState? AppliedState { get; private set; }
        public IReadOnlyDictionary<string, string> DurableOperations => durableOperations;
        public IReadOnlyDictionary<string, int> ActualApplicationCounts => actualApplicationCounts;
        public int MaximumConcurrentLockHolders => maximumConcurrentLockHolders;
        public int LockAcquisitionCount => lockAcquisitionCount;
        public int RecordedStateCount => recordedStateCount;
        public bool OperationObservedLeaseLoss { get; private set; }
        private CancellationTokenSource? leaseLoss;

        public async ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
            PhysicalSchemaTargetIdentity target,
            CancellationToken cancellationToken)
        {
            await applicationLock.WaitAsync(cancellationToken);
            Interlocked.Increment(ref lockAcquisitionCount);
            var holders = Interlocked.Increment(ref currentLockHolders);
            maximumConcurrentLockHolders = Math.Max(maximumConcurrentLockHolders, holders);
            leaseLoss = new CancellationTokenSource();
            return new LockLease(target, leaseLoss, () =>
            {
                Interlocked.Decrement(ref currentLockHolders);
                applicationLock.Release();
            });
        }

        public ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            AssertLockHeld();
            return ValueTask.FromResult(AppliedState is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(AppliedState));
        }

        public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            AssertLockHeld();
            var number = Interlocked.Increment(ref operationNumber);
            if (LoseLeaseDuringOperationNumber == number)
            {
                leaseLoss!.Cancel();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(250));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    OperationObservedLeaseLoss = true;
                    throw;
                }
                catch (TimeoutException)
                {
                    // The coordinator failed to bind the in-flight operation to lease ownership.
                }
            }
            if (CancelBeforeOperationNumber == number)
            {
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (FailBeforeOperationNumber == number)
                throw new InjectedExecutionException();
            if (OperationDelay > TimeSpan.Zero)
                await Task.Delay(OperationDelay, cancellationToken);

            if (durableOperations.TryGetValue(operation.Identity, out var fingerprint))
            {
                if (fingerprint != operation.Fingerprint)
                    throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, fingerprint);
            }
            else
            {
                durableOperations[operation.Identity] = operation.Fingerprint;
                actualApplicationCounts.AddOrUpdate(operation.Identity, 1, (_, count) => count + 1);
            }

            if (LoseNextOperationAcknowledgement)
            {
                LoseNextOperationAcknowledgement = false;
                throw new InjectedAcknowledgementLossException();
            }

            return new PhysicalSchemaOperationAcknowledgement(operation.Identity, operation.Fingerprint, DateTimeOffset.UtcNow);
        }

        public ValueTask RecordAppliedStateAsync(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            AssertLockHeld();
            if (AppliedState?.TargetFingerprint != expectedAppliedTargetFingerprint)
                throw new InvalidOperationException("Applied-state compare-and-swap conflict.");

            AppliedState = state;
            Interlocked.Increment(ref recordedStateCount);
            if (LoseNextStateAcknowledgement)
            {
                LoseNextStateAcknowledgement = false;
                throw new InjectedAcknowledgementLossException();
            }
            return ValueTask.CompletedTask;
        }

        public void SeedConflictingOperation(string identity, string fingerprint) =>
            durableOperations[identity] = fingerprint;

        private void AssertLockHeld() => Assert.True(Volatile.Read(ref currentLockHolders) == 1);

        private sealed class LockLease(
            PhysicalSchemaTargetIdentity target,
            CancellationTokenSource leaseLoss,
            Action release) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public CancellationToken OwnershipLost => leaseLoss.Token;

            public ValueTask DisposeAsync()
            {
                leaseLoss.Dispose();
                release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class InjectedExecutionException : Exception;
    private sealed class InjectedAcknowledgementLossException : Exception;
}
