namespace Groundwork.Operational.Outbox;

/// <summary>
/// Transactional outbox. Messages are appended in order (typically inside the same atomic commit as
/// the business write that produced them), claimed for delivery under a lease (visibility timeout),
/// and resolved by recording a delivery result: success removes the message, retry re-shows it after
/// a delay, and exhausting attempts dead-letters it. Maps to storage requirements
/// <c>OrderedConsumption</c>, <c>AtomicClaim</c>, <c>RetryRecovery</c>, and <c>Idempotency</c>.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Appends a message to the outbox in monotonic order.</summary>
    Task AppendAsync(OutboxAppendRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a batch of deliverable messages (visible and not yet delivered) under a lease, in
    /// order. Leased messages are hidden from other deliverers until the lease expires or a result
    /// is recorded.
    /// </summary>
    Task<IReadOnlyList<DeliverableMessage>> GetDeliverableAsync(GetDeliverableRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of a delivery attempt: success removes the message; transient failure
    /// re-shows it for retry; exceeding the attempt budget dead-letters it.
    /// </summary>
    Task RecordDeliveryResultAsync(DeliveryResultRequest request, CancellationToken cancellationToken = default);
}
