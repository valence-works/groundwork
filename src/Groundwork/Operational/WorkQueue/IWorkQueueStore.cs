namespace Groundwork.Operational.WorkQueue;

/// <summary>
/// Ordered, lease-based work queue. Provides FIFO-per-partition enqueue, atomic claim-with-lease
/// (visibility timeout), acknowledge, abandon/dead-letter, and an idempotent destructive dequeue.
/// Maps to storage requirements <c>OrderedConsumption</c>, <c>AtomicClaim</c>, <c>RetryRecovery</c>,
/// <c>Idempotency</c>, and <c>RangeQuery</c>.
/// </summary>
public interface IWorkQueueStore
{
    /// <summary>Appends a message to the tail of its partition with a monotonic sequence.</summary>
    Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <see cref="ClaimRequest.BatchSize"/> currently-visible messages in
    /// sequence order, stamping a lease token and a new visibility deadline and incrementing the
    /// attempt counter. Claimed messages are invisible to other consumers until acknowledged,
    /// abandoned, or the lease expires.
    /// </summary>
    Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a claimed message. Fenced by the lease token and idempotent: a second
    /// acknowledge for the same message returns <see cref="AckStatus.AlreadyAcknowledged"/>.
    /// </summary>
    Task<AckResult> AcknowledgeAsync(AckRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a claimed message back to its partition, optionally after a delay. When the message
    /// has reached its maximum attempts it transitions to dead-letter state instead.
    /// </summary>
    Task<AbandonResult> AbandonAsync(AbandonRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ordered destructive dequeue of the next visible message in a partition. Idempotent via the
    /// caller-supplied <see cref="DequeueRequest.IdempotencyKey"/>: replays return the original
    /// outcome rather than consuming another message.
    /// </summary>
    Task<DequeueResult> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken = default);
}
