# Groundwork Operational

Groundwork Operational defines the provider-neutral contract family for runtime hot-path
"operational" workloads that the portable `IDocumentStore` deliberately does not serve: work queues,
outboxes, leases, and mailbox ownership.

It is intentionally separate from `Groundwork.Documents` so the portable document contract stays
clean across every provider. The seams are:

- `IWorkQueueStore` — ordered FIFO enqueue, claim-with-lease + visibility timeout, acknowledge,
  abandon/dead-letter, and idempotent destructive dequeue.
- `ILeaseStore` — ownership leases with monotonic fencing tokens and TTL/expiry.
- `IOutboxStore` — ordered append, claim-with-lease delivery (`GetDeliverable`), and delivery-result
  recording (success / retry / dead-letter).
- `IOperationalUnitOfWork` / `IOperationalSessionFactory` — cross-unit atomic commit across the
  operational seams.

Each operational capability is declared as a `CapabilityId` on a storage unit (see
`WellKnownCapabilities`) and as a supported (and evidenced) capability on a provider, so the existing
`ProviderCapabilityValidator` remains the single compatibility authority. This package references
only `Groundwork.Core`.
