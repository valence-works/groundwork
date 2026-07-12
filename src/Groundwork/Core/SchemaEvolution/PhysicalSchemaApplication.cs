namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Provider execution boundary for physical schema application. Implementations must persist
/// operation identity and fingerprint durably enough that applying the same pair is idempotent.
/// Reusing an identity with another fingerprint must raise
/// <see cref="PhysicalSchemaFingerprintConflictException"/>. Returning an acknowledgement means
/// the operation is durably observable; if acknowledgement delivery is lost after durability, a
/// retry must return the same acknowledgement without applying the operation again.
/// </summary>
public interface IPhysicalSchemaExecutor
{
    /// <summary>
    /// Acquires exclusive application ownership for exactly one provider/manifest target. The
    /// lease must exclude history reads, operation application, validation, and state recording by
    /// every competing applicant until it is disposed.
    /// </summary>
    ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken);

    /// <summary>Reads durable state while the matching application lock is held.</summary>
    ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies or reconciles one semantic operation while the matching application lock is held.
    /// The target applied state is not written by this method.
    /// </summary>
    ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records the complete target snapshot using compare-and-swap against the expected
    /// prior target fingerprint. Implementations must treat an identical already-recorded state as
    /// success, which makes acknowledgement loss after the write recoverable on restart.
    /// </summary>
    ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        CancellationToken cancellationToken);
}

public interface IPhysicalSchemaApplicationLock : IAsyncDisposable
{
    PhysicalSchemaTargetIdentity Target { get; }
}

public enum PhysicalSchemaApplicationOutcome
{
    Applied,
    NoChanges,
    Rejected
}

public sealed record PhysicalSchemaApplicationResult(
    PhysicalSchemaApplicationOutcome Outcome,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaAppliedState? AppliedState);

/// <summary>
/// Safe application coordinator. It never records a target snapshot until every non-recording
/// operation has returned the exact expected durable acknowledgement.
/// </summary>
public static class PhysicalSchemaApplication
{
    public static async Task<PhysicalSchemaApplicationResult> ApplyAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        TimeProvider? timeProvider = null,
        LegacyPhysicalSchemaHistoryPolicy legacyHistoryPolicy = LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executor);
        timeProvider ??= TimeProvider.System;

        await using var applicationLock = await executor.AcquireApplicationLockAsync(target.Identity, cancellationToken);
        if (applicationLock.Target != target.Identity)
        {
            throw new InvalidOperationException(
                $"Executor returned lock '{applicationLock.Target}' for requested target '{target.Identity}'.");
        }

        var history = await executor.ReadHistoryAsync(target.Identity, cancellationToken);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            target,
            history,
            timeProvider.GetUtcNow(),
            legacyHistoryPolicy);
        if (!plan.IsApplicable)
            return new PhysicalSchemaApplicationResult(PhysicalSchemaApplicationOutcome.Rejected, plan, null);
        if (plan.Operations.Count == 0)
            return new PhysicalSchemaApplicationResult(
                PhysicalSchemaApplicationOutcome.NoChanges,
                plan,
                history.AppliedState);

        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations)
        {
            if (operation is RecordPhysicalSchemaAppliedStateOperation)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var acknowledgement = await executor.ApplyOperationAsync(operation, cancellationToken);
            if (acknowledgement.Identity != operation.Identity)
            {
                throw new InvalidOperationException(
                    $"Executor acknowledged operation '{acknowledgement.Identity}' while '{operation.Identity}' was expected.");
            }
            if (acknowledgement.Fingerprint != operation.Fingerprint)
            {
                throw new PhysicalSchemaFingerprintConflictException(
                    operation.Identity,
                    operation.Fingerprint,
                    acknowledgement.Fingerprint);
            }
            acknowledgements.Add(acknowledgement);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var appliedState = plan.Complete(acknowledgements, timeProvider.GetUtcNow());
        await executor.RecordAppliedStateAsync(
            appliedState,
            plan.ExpectedAppliedTargetFingerprint,
            cancellationToken);
        return new PhysicalSchemaApplicationResult(
            PhysicalSchemaApplicationOutcome.Applied,
            plan,
            appliedState);
    }
}
