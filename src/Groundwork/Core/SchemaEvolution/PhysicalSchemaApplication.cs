using Groundwork.Core.Validation;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Provider execution boundary for physical schema application. Implementations must persist
/// operation identity and fingerprint durably enough that applying the same pair is idempotent.
/// Reusing an identity with another fingerprint must raise
/// <see cref="PhysicalSchemaFingerprintConflictException"/>. Returning an acknowledgement means
/// the operation is durably observable; if acknowledgement delivery is lost after durability, a
/// retry must return the same acknowledgement. An unpublished, idempotent operation may be
/// reconciled before returning that acknowledgement when writes since the first attempt must be
/// included; evidence already represented by published applied state remains a skip token.
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
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies or reconciles one semantic operation while the matching application lock is held.
    /// The target applied state is not written by this method.
    /// </summary>
    ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records the complete target snapshot using compare-and-swap against the expected
    /// prior target fingerprint. Implementations must treat an identical already-recorded state as
    /// success, which makes acknowledgement loss after the write recoverable on restart.
    /// </summary>
    ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken);
}

public interface IPhysicalSchemaApplicationLock : IAsyncDisposable
{
    PhysicalSchemaTargetIdentity Target { get; }

    /// <summary>
    /// Signals that the provider can no longer guarantee exclusive application ownership. The
    /// coordinator binds all history, operation, validation, and state-recording work to this
    /// token so a lost renewable lease cannot continue applying or publish target state.
    /// </summary>
    CancellationToken OwnershipLost { get; }
}

public enum PhysicalSchemaApplicationOutcome
{
    Applied,
    NoChanges,
    Rejected,
    AuthorizationRequired
}

public sealed record PhysicalSchemaApplicationResult(
    PhysicalSchemaApplicationOutcome Outcome,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaAppliedState? AppliedState)
{
    public IReadOnlyList<GroundworkDiagnostic> AuthorizationDiagnostics { get; init; } = [];
}

/// <summary>
/// Result of evaluating one exact plan while its provider/manifest application lock is held.
/// A denied result prevents every operation and applied-state write for that plan.
/// </summary>
public sealed record PhysicalSchemaPlanAuthorization(
    bool IsAuthorized,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public static PhysicalSchemaPlanAuthorization Allow { get; } = new(true, []);

    public static PhysicalSchemaPlanAuthorization Deny(IReadOnlyList<GroundworkDiagnostic> diagnostics) =>
        new(false, diagnostics ?? throw new ArgumentNullException(nameof(diagnostics)));
}

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
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization = null,
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

        using var applicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            applicationLock.OwnershipLost);
        var applicationToken = applicationCancellation.Token;
        applicationToken.ThrowIfCancellationRequested();

        var history = await executor.ReadHistoryAsync(target.Identity, applicationLock, applicationToken);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            target,
            history,
            timeProvider.GetUtcNow(),
            legacyHistoryPolicy);
        if (!plan.IsApplicable)
            return new PhysicalSchemaApplicationResult(PhysicalSchemaApplicationOutcome.Rejected, plan, null);
        var authorization = planAuthorization?.Invoke(plan) ?? PhysicalSchemaPlanAuthorization.Allow;
        if (!authorization.IsAuthorized)
        {
            return new PhysicalSchemaApplicationResult(
                PhysicalSchemaApplicationOutcome.AuthorizationRequired,
                plan,
                history.AppliedState)
            {
                AuthorizationDiagnostics = authorization.Diagnostics
            };
        }
        if (plan.Operations.Count == 0)
        {
            var validation = new ValidatePhysicalSchemaOperation(
                target.Fingerprint,
                target.Routes,
                target.ProviderDefinitions);
            var acknowledgement = await executor.ApplyOperationAsync(
                target.Identity,
                validation,
                applicationLock,
                applicationToken);
            applicationToken.ThrowIfCancellationRequested();
            EnsureAcknowledges(validation, acknowledgement);
            return new PhysicalSchemaApplicationResult(
                PhysicalSchemaApplicationOutcome.NoChanges,
                plan,
                history.AppliedState);
        }

        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>();
        foreach (var operation in plan.Operations)
        {
            if (operation is RecordPhysicalSchemaAppliedStateOperation)
                continue;

            applicationToken.ThrowIfCancellationRequested();
            var acknowledgement = await executor.ApplyOperationAsync(
                target.Identity,
                operation,
                applicationLock,
                applicationToken);
            applicationToken.ThrowIfCancellationRequested();
            EnsureAcknowledges(operation, acknowledgement);
            acknowledgements.Add(acknowledgement);
        }

        applicationToken.ThrowIfCancellationRequested();
        var appliedState = plan.Complete(acknowledgements, timeProvider.GetUtcNow());
        await executor.RecordAppliedStateAsync(
            appliedState,
            plan.ExpectedAppliedTargetFingerprint,
            applicationLock,
            applicationToken);
        applicationToken.ThrowIfCancellationRequested();
        return new PhysicalSchemaApplicationResult(
            PhysicalSchemaApplicationOutcome.Applied,
            plan,
            appliedState);
    }

    private static void EnsureAcknowledges(
        PhysicalSchemaOperation operation,
        PhysicalSchemaOperationAcknowledgement acknowledgement)
    {
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
    }
}
