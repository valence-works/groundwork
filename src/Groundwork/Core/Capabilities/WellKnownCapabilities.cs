namespace Groundwork.Core.Capabilities;

/// <summary>
/// The built-in capabilities owned by Groundwork core, replacing the former
/// <c>StorageRequirement</c> enum. These are seeded into <see cref="CapabilityRegistry.Default"/> and
/// referenced by built-in operational contracts. Custom modules introduce their own descriptors in
/// their own namespace.
/// </summary>
public static class WellKnownCapabilities
{
    public static readonly CapabilityId AtomicClaim = new("groundwork.operational.atomic-claim");
    public static readonly CapabilityId LeaseRecovery = new("groundwork.operational.lease-recovery");
    public static readonly CapabilityId FencedOwnership = new("groundwork.operational.fenced-ownership");
    public static readonly CapabilityId OrderedConsumption = new("groundwork.operational.ordered-consumption");
    public static readonly CapabilityId RetryRecovery = new("groundwork.operational.retry-recovery");
    public static readonly CapabilityId Idempotency = new("groundwork.operational.idempotency");
    public static readonly CapabilityId RangeQuery = new("groundwork.query.range-scan");
    public static readonly CapabilityId RetentionPolicy = new("groundwork.operational.retention-policy");
    public static readonly CapabilityId AtomicCommit = new("groundwork.operational.atomic-commit");
    public static readonly CapabilityId ConcurrencyEvidence = new("groundwork.operational.concurrency-evidence");
    public static readonly CapabilityId OperationalDiagnostics = new("groundwork.operational.diagnostics");

    /// <summary>All built-in capability descriptors, owned by the <c>groundwork</c> module.</summary>
    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(AtomicClaim, "Atomic claim", "Atomically claim a batch of items under a visibility-timeout lease.", EvidenceGatedByDefault: true),
        new(LeaseRecovery, "Lease recovery", "Acquire/renew/expire ownership leases with TTL recovery.", EvidenceGatedByDefault: true),
        new(FencedOwnership, "Fenced ownership", "Monotonic fencing tokens that fence out stale owners.", EvidenceGatedByDefault: true),
        new(OrderedConsumption, "Ordered consumption", "FIFO-per-partition ordering of enqueue/dequeue.", EvidenceGatedByDefault: true),
        new(RetryRecovery, "Retry recovery", "Attempt counting, redelivery, and dead-letter on exhaustion.", EvidenceGatedByDefault: true),
        new(Idempotency, "Idempotency", "Idempotent destructive operations via caller-supplied keys.", EvidenceGatedByDefault: true),
        new(RangeQuery, "Range scan", "Range/comparison query operations and ordered scans.", EvidenceGatedByDefault: false),
        new(RetentionPolicy, "Retention policy", "Time/size-based retention and pruning of operational data.", EvidenceGatedByDefault: true),
        new(AtomicCommit, "Atomic commit", "Cross-unit atomic commit (unit of work) across operational units.", EvidenceGatedByDefault: true),
        new(ConcurrencyEvidence, "Concurrency evidence", "Demonstrated correctness under concurrent access.", EvidenceGatedByDefault: true),
        new(OperationalDiagnostics, "Operational diagnostics", "Observability hooks for operational hot-path workloads.", EvidenceGatedByDefault: true)
    ];
}
