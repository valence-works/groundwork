namespace Groundwork.Operational.WorkQueue;

public sealed record EnqueueRequest(
    string Unit,
    string PartitionKey,
    string Payload,
    int MaxAttempts = 5,
    TimeSpan? InitialDelay = null);

public sealed record EnqueueResult(string MessageId, long Sequence);

public sealed record ClaimRequest(
    string Unit,
    TimeSpan LeaseDuration,
    int BatchSize = 1,
    string? PartitionKey = null);

public sealed record ClaimedMessage(
    string MessageId,
    string PartitionKey,
    long Sequence,
    string Payload,
    int Attempt,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt);

public sealed record AckRequest(string Unit, string MessageId, string LeaseToken);

public enum AckStatus
{
    Acknowledged,
    AlreadyAcknowledged,
    LeaseLost
}

public sealed record AckResult(AckStatus Status)
{
    public static AckResult Acknowledged { get; } = new(AckStatus.Acknowledged);
    public static AckResult AlreadyAcknowledged { get; } = new(AckStatus.AlreadyAcknowledged);
    public static AckResult LeaseLost { get; } = new(AckStatus.LeaseLost);
}

public sealed record AbandonRequest(
    string Unit,
    string MessageId,
    string LeaseToken,
    TimeSpan? Delay = null);

public enum AbandonStatus
{
    Requeued,
    DeadLettered,
    LeaseLost
}

public sealed record AbandonResult(AbandonStatus Status, int Attempt)
{
    public static AbandonResult LeaseLost { get; } = new(AbandonStatus.LeaseLost, 0);
}

public sealed record DequeueRequest(string Unit, string PartitionKey, string IdempotencyKey);

public enum DequeueStatus
{
    Dequeued,
    Empty,
    Replayed
}

public sealed record DequeueResult(DequeueStatus Status, ClaimedMessage? Message = null)
{
    public static DequeueResult Empty { get; } = new(DequeueStatus.Empty);
}
