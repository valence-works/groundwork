using Groundwork.Core.SchemaEvolution;

namespace Groundwork.TestInfrastructure;

/// <summary>Simulates response loss only after a backfill acknowledgement is durable.</summary>
public sealed class BackfillAcknowledgementLosingExecutor(IPhysicalSchemaExecutor inner) : IPhysicalSchemaExecutor
{
    private int lost;

    public BackfillCanonicalJsonOperation? Backfill { get; private set; }
    public PhysicalSchemaOperationAcknowledgement? Acknowledgement { get; private set; }

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
        if (operation is BackfillCanonicalJsonOperation backfill && Interlocked.Exchange(ref lost, 1) == 0)
        {
            Backfill = backfill;
            Acknowledgement = acknowledgement;
            throw new SimulatedBackfillAcknowledgementLossException();
        }
        return acknowledgement;
    }

    public ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        inner.RecordAppliedStateAsync(state, expectedAppliedTargetFingerprint, applicationLock, cancellationToken);
}

public sealed class SimulatedBackfillAcknowledgementLossException : Exception;
