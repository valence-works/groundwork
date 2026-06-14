namespace Groundwork.Operational.Outbox;

public sealed record OutboxAppendRequest(
    string Unit,
    string StreamKey,
    string Payload,
    int MaxAttempts = 5);

public sealed record GetDeliverableRequest(
    string Unit,
    TimeSpan LeaseDuration,
    int BatchSize = 16,
    string? StreamKey = null);

public sealed record DeliverableMessage(
    string MessageId,
    string StreamKey,
    long Sequence,
    string Payload,
    int Attempt,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt);

public enum DeliveryOutcome
{
    Delivered,
    Retry,
    DeadLetter
}

public sealed record DeliveryResultRequest(
    string Unit,
    string MessageId,
    string LeaseToken,
    DeliveryOutcome Outcome,
    TimeSpan? RetryDelay = null);
