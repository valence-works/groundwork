using Groundwork.Operational.Outbox;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteOutboxStoreTests
{
    private const string Unit = "post-commit-outbox";

    [Fact]
    public async Task GetDeliverableReturnsOrderedMessagesUnderLease()
    {
        await using var harness = await OperationalHarness.Create();
        var outbox = harness.Store.Outbox;
        await outbox.AppendAsync(new OutboxAppendRequest(Unit, "s1", "a"));
        await outbox.AppendAsync(new OutboxAppendRequest(Unit, "s1", "b"));

        var deliverable = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)));

        Assert.Equal(["a", "b"], deliverable.Select(message => message.Payload));

        // Leased messages are hidden from a second deliverer.
        var hidden = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)));
        Assert.Empty(hidden);
    }

    [Fact]
    public async Task DeliveredResultRemovesMessage()
    {
        await using var harness = await OperationalHarness.Create();
        var outbox = harness.Store.Outbox;
        await outbox.AppendAsync(new OutboxAppendRequest(Unit, "s1", "a"));

        var deliverable = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)));
        var message = deliverable.Single();
        await outbox.RecordDeliveryResultAsync(new DeliveryResultRequest(Unit, message.MessageId, message.LeaseToken, DeliveryOutcome.Delivered));

        // Even after the lease would expire, the message is gone.
        var after = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)));
        Assert.Empty(after);
    }

    [Fact]
    public async Task RetryResultReshowsMessageAfterDelay()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var harness = await OperationalHarness.Create(clock);
        var outbox = harness.Store.Outbox;
        await outbox.AppendAsync(new OutboxAppendRequest(Unit, "s1", "a", MaxAttempts: 5));

        var first = (await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromSeconds(30)))).Single();
        await outbox.RecordDeliveryResultAsync(new DeliveryResultRequest(Unit, first.MessageId, first.LeaseToken, DeliveryOutcome.Retry, TimeSpan.FromSeconds(60)));

        Assert.Empty(await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromSeconds(30))));

        clock.Advance(TimeSpan.FromSeconds(61));
        var retried = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromSeconds(30)));
        Assert.Single(retried);
        Assert.Equal(2, retried[0].Attempt);
    }

    [Fact]
    public async Task DeadLetterResultParksMessage()
    {
        await using var harness = await OperationalHarness.Create();
        var outbox = harness.Store.Outbox;
        await outbox.AppendAsync(new OutboxAppendRequest(Unit, "s1", "a"));

        var message = (await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)))).Single();
        await outbox.RecordDeliveryResultAsync(new DeliveryResultRequest(Unit, message.MessageId, message.LeaseToken, DeliveryOutcome.DeadLetter));

        var after = await outbox.GetDeliverableAsync(new GetDeliverableRequest(Unit, TimeSpan.FromMinutes(5)));
        Assert.Empty(after);
    }
}
