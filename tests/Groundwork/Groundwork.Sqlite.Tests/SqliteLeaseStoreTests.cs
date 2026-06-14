using Groundwork.Operational.Leases;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteLeaseStoreTests
{
    private const string Unit = "agent-mailbox";

    [Fact]
    public async Task AcquireGrantsExclusiveOwnershipAndDeniesOthers()
    {
        await using var harness = await OperationalHarness.Create();
        var leases = harness.Store.Leases;

        var ownerA = await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "A", TimeSpan.FromMinutes(5)));
        var ownerB = await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "B", TimeSpan.FromMinutes(5)));

        var acquired = Assert.IsType<LeaseAcquisition.Acquired>(ownerA);
        Assert.Equal(1, acquired.FencingToken);

        var denied = Assert.IsType<LeaseAcquisition.Denied>(ownerB);
        Assert.Equal("A", denied.CurrentOwner);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeStolenWithMonotonicFencingToken()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var harness = await OperationalHarness.Create(clock);
        var leases = harness.Store.Leases;

        var first = Assert.IsType<LeaseAcquisition.Acquired>(
            await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "A", TimeSpan.FromSeconds(30))));

        clock.Advance(TimeSpan.FromSeconds(31));

        var stolen = Assert.IsType<LeaseAcquisition.Acquired>(
            await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "B", TimeSpan.FromSeconds(30))));

        Assert.True(stolen.FencingToken > first.FencingToken);

        var state = await leases.ReadAsync(Unit, "res-1");
        Assert.Equal("B", state!.OwnerId);
    }

    [Fact]
    public async Task RenewRequiresMatchingOwnerAndFencingToken()
    {
        await using var harness = await OperationalHarness.Create();
        var leases = harness.Store.Leases;
        var acquired = Assert.IsType<LeaseAcquisition.Acquired>(
            await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "A", TimeSpan.FromMinutes(5))));

        var renewed = await leases.RenewAsync(new RenewLeaseRequest(Unit, "res-1", "A", acquired.FencingToken, TimeSpan.FromMinutes(5)));
        Assert.IsType<LeaseAcquisition.Acquired>(renewed);

        var stale = await leases.RenewAsync(new RenewLeaseRequest(Unit, "res-1", "A", acquired.FencingToken - 1, TimeSpan.FromMinutes(5)));
        Assert.IsType<LeaseAcquisition.Denied>(stale);
    }

    [Fact]
    public async Task ReleaseFreesLeaseButKeepsFencingMonotonic()
    {
        await using var harness = await OperationalHarness.Create();
        var leases = harness.Store.Leases;
        var acquired = Assert.IsType<LeaseAcquisition.Acquired>(
            await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "A", TimeSpan.FromMinutes(5))));

        var released = await leases.ReleaseAsync(new ReleaseLeaseRequest(Unit, "res-1", "A", acquired.FencingToken));
        Assert.True(released);
        Assert.Null(await leases.ReadAsync(Unit, "res-1"));

        var reacquired = Assert.IsType<LeaseAcquisition.Acquired>(
            await leases.TryAcquireAsync(new AcquireLeaseRequest(Unit, "res-1", "B", TimeSpan.FromMinutes(5))));
        Assert.True(reacquired.FencingToken > acquired.FencingToken);
    }
}
