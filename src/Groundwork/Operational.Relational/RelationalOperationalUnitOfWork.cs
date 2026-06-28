using Groundwork.Core.Identity;
using Groundwork.Operational.Leases;
using Groundwork.Operational.Outbox;
using Groundwork.Operational.UnitOfWork;
using Groundwork.Operational.WorkQueue;
using Groundwork.Provider.Relational;

namespace Groundwork.Operational.Relational;

/// <summary>
/// A relational cross-unit operational transaction. Wraps a reusable <see cref="RelationalUnitOfWork"/>;
/// all enlisted store operations run inside one transaction and become durable only on
/// <see cref="CommitAsync"/>.
/// </summary>
internal sealed class RelationalOperationalUnitOfWork : IOperationalUnitOfWork
{
    private readonly RelationalUnitOfWork unitOfWork;

    public RelationalOperationalUnitOfWork(
        RelationalUnitOfWork unitOfWork,
        IOperationalClock clock,
        IIdentityGenerator identityGenerator)
    {
        this.unitOfWork = unitOfWork;

        var executor = unitOfWork.Executor;
        WorkQueue = new RelationalWorkQueueStore(executor, clock, identityGenerator);
        Leases = new RelationalLeaseStore(executor, clock, identityGenerator);
        Outbox = new RelationalOutboxStore(executor, clock, identityGenerator);
    }

    public IWorkQueueStore WorkQueue { get; }

    public ILeaseStore Leases { get; }

    public IOutboxStore Outbox { get; }

    public Task CommitAsync(CancellationToken cancellationToken = default) => unitOfWork.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default) => unitOfWork.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => unitOfWork.DisposeAsync();
}
