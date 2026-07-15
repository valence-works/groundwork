using Groundwork.Core.Capabilities;
using Groundwork.Core.Intents;
using Groundwork.Operational;
using Groundwork.SupportTickets.Operations;
using Xunit;

namespace Groundwork.SupportTickets.Tests;

/// <summary>
/// Showcases the operational hot-path capabilities layered beside the portable document store:
/// FIFO triage with exclusive lease-based claim, fenced ownership, an at-least-once notification
/// outbox, atomic cross-unit escalation, and capability-derived provider fit.
/// </summary>
public sealed class SupportTicketOperationsTests
{
    /// <summary>Test clock so visibility timeouts and lease expiry are deterministic.</summary>
    private sealed class MutableClock(DateTimeOffset start) : IOperationalClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private static Task<SupportTicketSampleHost> CreateHostAsync(IOperationalClock? clock = null) =>
        SupportTicketSampleHost.CreateAsync(
            new SupportTicketStorageOptions(SupportTicketProvider.Sqlite, "Data Source=:memory:")
            {
                OperationalClock = clock
            });

    [Fact]
    public async Task TriageQueueIsFifoExclusiveAndAcknowledged()
    {
        await using var host = await CreateHostAsync();
        var operations = host.Operations;

        await operations.QueueForTriageAsync("TCK-1", "high");
        await operations.QueueForTriageAsync("TCK-2", "high");
        await operations.QueueForTriageAsync("TCK-3", "high");

        var first = await operations.ClaimNextTriageAsync("agent-a", TimeSpan.FromMinutes(5), "high");
        var second = await operations.ClaimNextTriageAsync("agent-b", TimeSpan.FromMinutes(5), "high");

        Assert.NotNull(first);
        Assert.NotNull(second);
        // FIFO per priority lane.
        Assert.Equal("TCK-1", first!.TicketNumber);
        Assert.Equal("TCK-2", second!.TicketNumber);
        // Exclusive: two claimers never receive the same item.
        Assert.NotEqual(first.MessageId, second.MessageId);

        Assert.True(await operations.CompleteTriageAsync(first.MessageId, first.LeaseToken));

        // After both visible items are claimed, only TCK-3 remains for a third claimer.
        var third = await operations.ClaimNextTriageAsync("agent-c", TimeSpan.FromMinutes(5), "high");
        Assert.Equal("TCK-3", third!.TicketNumber);
    }

    [Fact]
    public async Task ExpiredTriageLeaseIsRedeliveredToAnotherAgent()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var host = await CreateHostAsync(clock);
        var operations = host.Operations;

        await operations.QueueForTriageAsync("TCK-1", "normal");
        var firstClaim = await operations.ClaimNextTriageAsync("agent-a", TimeSpan.FromSeconds(30), "normal");
        Assert.NotNull(firstClaim);

        // Agent A stalls; nothing else is visible while the lease holds.
        Assert.Null(await operations.ClaimNextTriageAsync("agent-b", TimeSpan.FromSeconds(30), "normal"));

        clock.Advance(TimeSpan.FromSeconds(31));

        var redelivered = await operations.ClaimNextTriageAsync("agent-b", TimeSpan.FromSeconds(30), "normal");
        Assert.NotNull(redelivered);
        Assert.Equal("TCK-1", redelivered!.TicketNumber);
        Assert.Equal(2, redelivered.Attempt);
    }

    [Fact]
    public async Task OwnershipIsExclusiveWithMonotonicFencingTokens()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var host = await CreateHostAsync(clock);
        var operations = host.Operations;

        var a = await operations.AcquireOwnershipAsync("TCK-9", "agent-a", TimeSpan.FromSeconds(60));
        Assert.True(a.Granted);

        // A second agent is denied while the lease is live.
        var deniedB = await operations.AcquireOwnershipAsync("TCK-9", "agent-b", TimeSpan.FromSeconds(60));
        Assert.False(deniedB.Granted);
        Assert.Equal("agent-a", deniedB.Owner);

        // After expiry the lease can be stolen with a strictly larger fencing token.
        clock.Advance(TimeSpan.FromSeconds(61));
        var stolenByB = await operations.AcquireOwnershipAsync("TCK-9", "agent-b", TimeSpan.FromSeconds(60));
        Assert.True(stolenByB.Granted);
        Assert.True(stolenByB.FencingToken > a.FencingToken);

        // The stale owner can no longer release using the old fencing token.
        Assert.False(await operations.ReleaseOwnershipAsync("TCK-9", "agent-a", a.FencingToken));

        var state = await operations.ReadOwnershipAsync("TCK-9");
        Assert.Equal("agent-b", state!.Owner);
        Assert.Equal(stolenByB.FencingToken, state.FencingToken);
    }

    [Fact]
    public async Task NotificationOutboxDeliversInOrderAtLeastOnce()
    {
        await using var host = await CreateHostAsync();
        var operations = host.Operations;

        await operations.NotifyAsync("TCK-5", "created", "Ticket opened.");
        await operations.NotifyAsync("TCK-5", "assigned", "Assigned to agent-a.");
        await operations.NotifyAsync("TCK-5", "resolved", "Issue resolved.");

        var dispatched = await operations.DispatchNotificationsAsync();
        Assert.Equal(["created", "assigned", "resolved"], dispatched.Select(notification => notification.Kind));

        // Delivered messages are removed; a second dispatch finds nothing.
        Assert.Empty(await operations.DispatchNotificationsAsync());
    }

    [Fact]
    public async Task FailedNotificationIsRetriedUntilDelivered()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var host = await CreateHostAsync(clock);
        var operations = host.Operations;

        await operations.NotifyAsync("TCK-7", "created", "Ticket opened.");

        // First attempt fails at the channel; the message is retried, not lost.
        var failedAttempt = await operations.DispatchNotificationsAsync(channel: _ => false);
        Assert.Empty(failedAttempt);

        clock.Advance(TimeSpan.FromSeconds(31));

        var delivered = await operations.DispatchNotificationsAsync();
        var notification = Assert.Single(delivered);
        Assert.Equal("created", notification.Kind);
        Assert.Equal(2, notification.Attempt);
    }

    [Fact]
    public async Task EscalationCommitsSupervisorTriageAndNotificationAtomically()
    {
        await using var host = await CreateHostAsync();
        var operations = host.Operations;

        await operations.EscalateAsync("TCK-3", "Customer is a VIP.");

        // The supervisor-lane triage item landed...
        var supervisorWork = await operations.ClaimNextTriageAsync("supervisor-sam", TimeSpan.FromMinutes(5), "supervisor");
        Assert.NotNull(supervisorWork);
        Assert.Equal("TCK-3", supervisorWork!.TicketNumber);

        // ...and so did the manager notification, in the same atomic commit.
        var dispatched = await operations.DispatchNotificationsAsync();
        var alert = Assert.Single(dispatched);
        Assert.Equal("escalated", alert.Kind);
        Assert.Equal("TCK-3", alert.TicketNumber);
    }

    [Fact]
    public async Task ProviderFitIsDerivedFromRequirements()
    {
        await using var host = await CreateHostAsync();

        // Same operational requirements: Supported on an operational provider...
        Assert.IsType<ProviderFit.Supported>(host.OperationalFit.OperationalProvider);

        // ...Unsupported on a portable document provider, with only its absent queue and lease
        // semantics reported. Atomic document commit is a real capability of that provider.
        var unsupported = Assert.IsType<ProviderFit.Unsupported>(host.OperationalFit.DocumentOnlyProvider);
        Assert.Contains(WellKnownCapabilities.OrderedConsumption, unsupported.MissingRequirements);
        Assert.Contains(WellKnownCapabilities.AtomicClaim, unsupported.MissingRequirements);
        Assert.Contains(WellKnownCapabilities.FencedOwnership, unsupported.MissingRequirements);
        Assert.DoesNotContain(WellKnownCapabilities.AtomicCommit, unsupported.MissingRequirements);
    }
}
