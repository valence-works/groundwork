# Operational hot path — SupportTickets showcase

This folder layers Groundwork's **operational** store family beside the portable document store to
showcase real runtime "hot-path" use cases. Ticket and comment *documents* stay portable across
SQLite / SQL Server / PostgreSQL / Mongo; the operational concerns below run on a dedicated
operational-capable provider (SQLite reference impl).

The split is deliberate: `SupportTicketRepository` only touches `IDocumentStore`, while
`SupportTicketOperations` uses the operational seams (`IWorkQueueStore`, `ILeaseStore`,
`IOutboxStore`, `IOperationalSessionFactory`). The document contract is never widened.

## Use cases

| Capability | Real use case | Primitive |
|------------|---------------|-----------|
| FIFO triage queue with exclusive, lease-protected claim and visibility timeout | New tickets are claimed by exactly one agent in priority order; a stalled agent's work is redelivered | `IWorkQueueStore` (`OrderedConsumption`, `AtomicClaim`, `RetryRecovery`, `Idempotency`) |
| Exclusive ticket ownership with fencing tokens | Only one agent edits a ticket at a time; a stale owner can't clobber a newer owner after lease expiry | `ILeaseStore` (`LeaseRecovery`, `FencedOwnership`) |
| At-least-once customer-notification outbox | Status-change notifications are delivered in order per ticket, with retry/dead-letter on failure | `IOutboxStore` (`OrderedConsumption`, `RetryRecovery`, `Idempotency`) |
| Atomic escalation | Escalating to a supervisor enqueues supervisor triage **and** appends a manager notification in one atomic cross-unit commit | `IOperationalUnitOfWork` (`AtomicCommit`) |

## HTTP endpoints

- `GET  /operational/fit` — capability-derived verdict: `Supported` on the operational provider,
  `Unsupported` (with missing requirements) on a document-only provider.
- `POST /triage/claim` — claim the next triage item (`agentId`, optional `priority`, `leaseSeconds`).
- `POST /triage/{messageId}/complete` — acknowledge a claimed item (`leaseToken`).
- `POST /tickets/{ticketNumber}/lock` / `unlock` — acquire / release exclusive ownership.
- `GET  /tickets/{ticketNumber}/lock` — read current ownership.
- `POST /notifications/dispatch` — dispatch pending notifications in order.
- `POST /tickets` — also enqueues the new ticket for triage.
- `POST /tickets/{ticketNumber}/escalate` — also performs the atomic supervisor escalation.

## Capability-derived fit

The operational manifest (`SupportTicketOperationsManifest`) declares only **requirements**
(what the data needs). `ProviderCapabilityValidator.Evaluate` computes the verdict — the author never
self-declares a provider category. The same requirements are `Supported` against an operational
provider and `Unsupported` against a portable document-only provider, which is exactly why the hot
path runs on its own operational provider.
