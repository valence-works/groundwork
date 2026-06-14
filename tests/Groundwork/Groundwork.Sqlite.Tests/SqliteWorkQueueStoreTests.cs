using Groundwork.Operational;
using Groundwork.Operational.WorkQueue;
using Groundwork.Sqlite.Operational;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteWorkQueueStoreTests
{
    private const string Unit = "outbox";

    [Fact]
    public async Task EnqueueDequeuePreservesFifoOrderPerPartition()
    {
        await using var harness = await OperationalHarness.Create();
        var queue = harness.Store.WorkQueue;

        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "a"));
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "b"));
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "c"));

        var first = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "k1"));
        var second = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "k2"));
        var third = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "k3"));
        var empty = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "k4"));

        Assert.Equal("a", first.Message!.Payload);
        Assert.Equal("b", second.Message!.Payload);
        Assert.Equal("c", third.Message!.Payload);
        Assert.Equal(DequeueStatus.Empty, empty.Status);
    }

    [Fact]
    public async Task DequeueIsIdempotentPerKey()
    {
        await using var harness = await OperationalHarness.Create();
        var queue = harness.Store.WorkQueue;
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "a"));
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "b"));

        var first = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "same-key"));
        var replay = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "same-key"));

        Assert.Equal(DequeueStatus.Dequeued, first.Status);
        Assert.Equal("a", first.Message!.Payload);
        Assert.Equal(DequeueStatus.Replayed, replay.Status);
        Assert.Equal("a", replay.Message!.Payload);

        // The replay must not have consumed "b".
        var next = await queue.DequeueAsync(new DequeueRequest(Unit, "p1", "other-key"));
        Assert.Equal("b", next.Message!.Payload);
    }

    [Fact]
    public async Task ClaimGivesExclusiveOwnershipUnderConcurrency()
    {
        await using var harness = await OperationalHarness.Create();
        var queue = harness.Store.WorkQueue;
        const int total = 50;
        for (var i = 0; i < total; i++)
            await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", $"m{i}"));

        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task Worker()
        {
            while (true)
            {
                var batch = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromMinutes(5), BatchSize: 1));
                if (batch.Count == 0)
                    break;

                foreach (var message in batch)
                {
                    claimed.Add(message.MessageId);
                    await queue.AcknowledgeAsync(new AckRequest(Unit, message.MessageId, message.LeaseToken));
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Worker()));

        Assert.Equal(total, claimed.Count);
        Assert.Equal(total, claimed.Distinct().Count());
    }

    [Fact]
    public async Task ClaimAppliesVisibilityTimeoutAndRedeliversAfterLeaseExpiry()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var harness = await OperationalHarness.Create(clock);
        var queue = harness.Store.WorkQueue;
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "a"));

        var firstClaim = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromSeconds(30)));
        Assert.Single(firstClaim);
        Assert.Equal(1, firstClaim[0].Attempt);

        // Still leased: invisible to a second claimer.
        var hidden = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromSeconds(30)));
        Assert.Empty(hidden);

        // Lease expires -> message becomes visible again and is redelivered with a higher attempt.
        clock.Advance(TimeSpan.FromSeconds(31));
        var redelivered = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromSeconds(30)));
        Assert.Single(redelivered);
        Assert.Equal(firstClaim[0].MessageId, redelivered[0].MessageId);
        Assert.Equal(2, redelivered[0].Attempt);
    }

    [Fact]
    public async Task AcknowledgeIsFencedAndIdempotent()
    {
        await using var harness = await OperationalHarness.Create();
        var queue = harness.Store.WorkQueue;
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "a"));

        var claim = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromMinutes(5)));
        var message = claim.Single();

        var staleLease = await queue.AcknowledgeAsync(new AckRequest(Unit, message.MessageId, "wrong-token"));
        Assert.Equal(AckStatus.LeaseLost, staleLease.Status);

        var first = await queue.AcknowledgeAsync(new AckRequest(Unit, message.MessageId, message.LeaseToken));
        var second = await queue.AcknowledgeAsync(new AckRequest(Unit, message.MessageId, message.LeaseToken));

        Assert.Equal(AckStatus.Acknowledged, first.Status);
        Assert.Equal(AckStatus.AlreadyAcknowledged, second.Status);
    }

    [Fact]
    public async Task AbandonDeadLettersAfterMaxAttempts()
    {
        await using var harness = await OperationalHarness.Create();
        var queue = harness.Store.WorkQueue;
        await queue.EnqueueAsync(new EnqueueRequest(Unit, "p1", "a", MaxAttempts: 2));

        var first = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromMinutes(5)));
        var firstAbandon = await queue.AbandonAsync(new AbandonRequest(Unit, first[0].MessageId, first[0].LeaseToken));
        Assert.Equal(AbandonStatus.Requeued, firstAbandon.Status);

        var second = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromMinutes(5)));
        Assert.Equal(2, second[0].Attempt);
        var secondAbandon = await queue.AbandonAsync(new AbandonRequest(Unit, second[0].MessageId, second[0].LeaseToken));
        Assert.Equal(AbandonStatus.DeadLettered, secondAbandon.Status);

        // Dead-lettered message is no longer claimable.
        var afterDeadLetter = await queue.ClaimAsync(new ClaimRequest(Unit, TimeSpan.FromMinutes(5)));
        Assert.Empty(afterDeadLetter);
    }
}
