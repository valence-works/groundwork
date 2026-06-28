using System.Data.Common;
using Groundwork.Core.Identity;
using Groundwork.Core.Transactions;
using Groundwork.Operational.Leases;
using Groundwork.Operational.Outbox;
using Groundwork.Operational.UnitOfWork;
using Groundwork.Operational.WorkQueue;
using Groundwork.Provider.Relational;

namespace Groundwork.Operational.Relational;

/// <summary>
/// Relational implementation of the operational store family. Owns a single <see cref="DbConnection"/>
/// via a reusable <see cref="RelationalSession"/>; exposes autonomous <see cref="IWorkQueueStore"/>,
/// <see cref="ILeaseStore"/>, and <see cref="IOutboxStore"/> stores, and acts as the
/// <see cref="IOperationalSessionFactory"/> for cross-unit atomic commit.
/// </summary>
public class RelationalOperationalStore : IOperationalSessionFactory
{
    private readonly RelationalSession session;
    private readonly IOperationalClock clock;
    private readonly TransactionBoundary boundary;
    private readonly IIdentityGenerator identityGenerator;

    public RelationalOperationalStore(
        DbConnection connection,
        IOperationalClock? clock = null,
        TransactionBoundary boundary = TransactionBoundary.CrossUnitAtomic,
        IIdentityGenerator? identityGenerator = null)
    {
        this.session = new RelationalSession(connection);
        this.clock = clock ?? SystemOperationalClock.Instance;
        this.boundary = boundary;
        this.identityGenerator = identityGenerator ?? new ShortIdentityGenerator();

        var executor = session.AutonomousExecutor;
        WorkQueue = new RelationalWorkQueueStore(executor, this.clock, this.identityGenerator);
        Leases = new RelationalLeaseStore(executor, this.clock, this.identityGenerator);
        Outbox = new RelationalOutboxStore(executor, this.clock, this.identityGenerator);
    }

    public IWorkQueueStore WorkQueue { get; }

    public ILeaseStore Leases { get; }

    public IOutboxStore Outbox { get; }

    public TransactionBoundary Boundary => boundary;

    public async Task<IOperationalUnitOfWork> BeginAsync(OperationalCommitScope scope, CancellationToken cancellationToken = default)
    {
        if (boundary != TransactionBoundary.CrossUnitAtomic)
            throw new UnsupportedAtomicCommitException(scope.Units);

        var unitOfWork = await session.BeginUnitOfWorkAsync(cancellationToken);
        return new RelationalOperationalUnitOfWork(unitOfWork, clock, identityGenerator);
    }
}
