using Groundwork.Operational.Leases;
using Groundwork.Operational.Outbox;
using Groundwork.Operational.WorkQueue;

namespace Groundwork.Operational.UnitOfWork;

/// <summary>
/// Opens an <see cref="IOperationalUnitOfWork"/> spanning a declared set of operational storage
/// units that must commit as one logical transaction. Maps to storage requirement
/// <c>AtomicCommit</c>.
/// </summary>
public interface IOperationalSessionFactory
{
    /// <summary>
    /// Begins a unit of work over the units named in <paramref name="scope"/>. A provider that
    /// cannot honor cross-unit atomicity for the requested scope throws
    /// <see cref="UnsupportedAtomicCommitException"/> rather than silently degrading.
    /// </summary>
    Task<IOperationalUnitOfWork> BeginAsync(OperationalCommitScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// A cross-unit operational transaction. The operational stores exposed here enlist in a single
/// commit boundary; nothing is durable until <see cref="CommitAsync"/> succeeds. Disposing without
/// committing rolls back.
/// </summary>
public interface IOperationalUnitOfWork : IAsyncDisposable
{
    IWorkQueueStore WorkQueue { get; }

    ILeaseStore Leases { get; }

    IOutboxStore Outbox { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
