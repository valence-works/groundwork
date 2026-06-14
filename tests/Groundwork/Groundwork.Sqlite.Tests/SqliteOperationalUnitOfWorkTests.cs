using Groundwork.Operational.Outbox;
using Groundwork.Operational.UnitOfWork;
using Groundwork.Operational.WorkQueue;
using Groundwork.Operational.Relational;
using Groundwork.Sqlite.Operational;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteOperationalUnitOfWorkTests
{
    private static readonly OperationalCommitScope Scope = OperationalCommitScope.Of("scheduler", "outbox");

    [Fact]
    public async Task CommitPersistsEveryUnitAtomically()
    {
        await using var harness = await OperationalHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.WorkQueue.EnqueueAsync(new EnqueueRequest("scheduler", "p1", "work"));
            await unitOfWork.Outbox.AppendAsync(new OutboxAppendRequest("outbox", "s1", "event"));
            await unitOfWork.CommitAsync();
        }

        var work = await harness.Store.WorkQueue.DequeueAsync(new DequeueRequest("scheduler", "p1", "k1"));
        var events = await harness.Store.Outbox.GetDeliverableAsync(new GetDeliverableRequest("outbox", TimeSpan.FromMinutes(5)));

        Assert.Equal("work", work.Message!.Payload);
        Assert.Single(events);
        Assert.Equal("event", events[0].Payload);
    }

    [Fact]
    public async Task RollbackDiscardsEveryUnitsWrites()
    {
        await using var harness = await OperationalHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.WorkQueue.EnqueueAsync(new EnqueueRequest("scheduler", "p1", "work"));
            await unitOfWork.Outbox.AppendAsync(new OutboxAppendRequest("outbox", "s1", "event"));
            // Dispose without commit -> rollback.
        }

        var work = await harness.Store.WorkQueue.DequeueAsync(new DequeueRequest("scheduler", "p1", "k1"));
        var events = await harness.Store.Outbox.GetDeliverableAsync(new GetDeliverableRequest("outbox", TimeSpan.FromMinutes(5)));

        Assert.Equal(DequeueStatus.Empty, work.Status);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ExplicitRollbackDiscardsWrites()
    {
        await using var harness = await OperationalHarness.Create();

        await using (var unitOfWork = await harness.Store.BeginAsync(Scope))
        {
            await unitOfWork.WorkQueue.EnqueueAsync(new EnqueueRequest("scheduler", "p1", "work"));
            await unitOfWork.RollbackAsync();
        }

        var work = await harness.Store.WorkQueue.DequeueAsync(new DequeueRequest("scheduler", "p1", "k1"));
        Assert.Equal(DequeueStatus.Empty, work.Status);
    }

    [Fact]
    public async Task PerOperationBoundaryRejectsCrossUnitCommitLoudly()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await new SqliteOperationalMaterializer(connection).MaterializeAsync();
        var store = new RelationalOperationalStore(connection, boundary: TransactionBoundary.PerOperation);

        var exception = await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.BeginAsync(Scope));
        Assert.Equal(Scope.Units, exception.Scope.Units);

        await connection.DisposeAsync();
    }
}
