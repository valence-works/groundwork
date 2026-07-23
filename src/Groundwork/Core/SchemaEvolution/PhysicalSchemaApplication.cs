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
    /// Applies or reconciles every non-recording operation of one authorized plan, in order, and
    /// returns exactly one acknowledgement per operation in that same order. Every per-operation
    /// contract (idempotent identity/fingerprint replay, fingerprint-conflict rejection,
    /// unpublished-operation reconciliation, durable acknowledgement) applies unchanged to each
    /// operation of the batch. The default implementation applies each operation individually
    /// through <see cref="ApplyOperationAsync"/>. Implementations may override it to bound the
    /// whole batch in a single atomic durable unit with one durability barrier; such an
    /// implementation may defer each operation's live-object validation to the batch's trailing
    /// <see cref="ValidatePhysicalSchemaOperation"/> when the batch ends with one covering the
    /// complete target, because a validation failure then rolls back the entire batch.
    /// </summary>
    async ValueTask<IReadOnlyList<PhysicalSchemaOperationAcknowledgement>> ApplyOperationBatchAsync(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>(operations.Count);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            acknowledgements.Add(await ApplyOperationAsync(target, operation, applicationLock, cancellationToken));
        }
        return acknowledgements;
    }

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
        CancellationToken cancellationToken = default,
        Func<PhysicalSchemaDiffPlan, CancellationToken, ValueTask<PhysicalSchemaPlanAuthorization>>? planAuthorizationAsync = null)
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
        if (planAuthorization is not null && planAuthorizationAsync is not null)
            throw new ArgumentException("Only one physical-schema plan authorization callback can be configured.");
        var authorization = planAuthorizationAsync is not null
            ? await planAuthorizationAsync(plan, applicationToken)
            : planAuthorization?.Invoke(plan) ?? PhysicalSchemaPlanAuthorization.Allow;
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

        var operations = plan.Operations
            .Where(operation => operation is not RecordPhysicalSchemaAppliedStateOperation)
            .ToArray();
        var acknowledgements = await executor.ApplyOperationBatchAsync(
            target.Identity,
            operations,
            applicationLock,
            applicationToken);
        applicationToken.ThrowIfCancellationRequested();
        if (acknowledgements.Count != operations.Length)
        {
            throw new InvalidOperationException(
                $"Executor acknowledged {acknowledgements.Count} operations while {operations.Length} were expected.");
        }
        for (var index = 0; index < operations.Length; index++)
            EnsureAcknowledges(operations[index], acknowledgements[index]);

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
