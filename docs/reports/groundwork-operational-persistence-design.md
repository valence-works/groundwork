# Groundwork Operational Persistence & Capability-Derived Workload Fit

Program goal state: [Groundwork Persistence Readiness](../program-goals/groundwork-persistence-readiness.md).
Related: [Groundwork Runtime Evaluation](../reports/groundwork-runtime-evaluation.md).

Status: design + reference implementation. The capability-derived workload-fit API and the
operational contract family are implemented in core; the SQLite reference provider implements all
four seams with passing xUnit coverage.

## Problem

`IDocumentStore` (Save/Load/Delete/Query) is an excellent **portable document** contract:
per-document optimistic concurrency, single-field equality query by declared index, per-Save
transaction scope, and offset paging. It is deliberately not sufficient for runtime hot-path
**operational** workloads — work queues, outboxes, leases, and mailbox ownership — which need atomic
claim with visibility timeout, ordered destructive dequeue, cross-unit atomic commit, fenced
ownership, retry/idempotency metadata, and range/ordered scans.

The runtime evaluation already classified these candidates (benchmark-gated continuation state and
specialized-provider operational streams), but no primitives existed to implement them. This note
adds those primitives **beside** `IDocumentStore`, never inside it, and reworks workload
classification so a provider's fit is **derived from capabilities** rather than self-declared by the
manifest author.

## Two parts

- **Part A — Operational primitives.** A new `Groundwork.Operational` contract family, separate from
  `Groundwork.Documents`, with focused seams and a cross-unit transaction boundary.
- **Part B — Capability-derived workload fit.** Storage units declare *requirements* (what the data
  needs); providers declare *supported* (and *evidenced*) capabilities; the verdict is a **computed**
  `ProviderFit`, not an author-declared category.

---

## Part A — Operational contract surface

All operational contracts live in `Groundwork.Operational` (references `Groundwork.Core` only). They
are never folded into `IDocumentStore`; the portable document contract stays clean across
SQLite / SQL Server / PostgreSQL / Mongo.

### `IWorkQueueStore` — gaps 1, 2, 5, 6

```csharp
Task<EnqueueResult>               EnqueueAsync(EnqueueRequest request, CancellationToken ct = default);
Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(ClaimRequest request, CancellationToken ct = default);
Task<AckResult>                   AcknowledgeAsync(AckRequest request, CancellationToken ct = default);
Task<AbandonResult>               AbandonAsync(AbandonRequest request, CancellationToken ct = default);
Task<DequeueResult>               DequeueAsync(DequeueRequest request, CancellationToken ct = default);
```

- **Enqueue** appends a payload to the tail of its partition with a monotonic per-partition sequence.
- **Claim** atomically selects the visible head of the queue (`next_visible_at <= now`, not
  dead-lettered), in sequence order, batches up to *N*, stamps a `lease_token` and a new
  `next_visible_at = now + lease`, and increments `attempt` — lease-on-read plus visibility timeout
  in one step. Returns `ClaimedMessage { MessageId, PartitionKey, Sequence, Payload, Attempt,
  LeaseToken, LeaseExpiresAt }`.
- **Acknowledge** permanently removes the message, fenced by `lease_token`; idempotent
  (`Acknowledged` / `AlreadyAcknowledged` / `LeaseLost`).
- **Abandon** re-shows the message immediately or after a delay; once `attempt >= maxAttempts` it
  transitions to dead-letter instead (`Requeued` / `DeadLettered` / `LeaseLost`).
- **Dequeue** is the ordered, destructive FIFO single-call path, made idempotent by a caller
  `IdempotencyKey` so replays return the original outcome rather than consuming another message.

### `ILeaseStore` — gap 4

```csharp
Task<LeaseAcquisition> TryAcquireAsync(AcquireLeaseRequest request, CancellationToken ct = default);
Task<LeaseAcquisition> RenewAsync(RenewLeaseRequest request, CancellationToken ct = default);
Task<bool>             ReleaseAsync(ReleaseLeaseRequest request, CancellationToken ct = default);
Task<LeaseState?>      ReadAsync(string unit, string resourceKey, CancellationToken ct = default);
```

`LeaseAcquisition` is `Acquired(long FencingToken, DateTimeOffset ExpiresAt)` or
`Denied(string CurrentOwner, DateTimeOffset ExpiresAt)`. The **fencing token strictly increases per
resource across acquisitions**, so a stale owner whose lease expired is fenced out: downstream
writers reject any token lower than the latest. `Renew` and `Release` require a matching `ownerId`
**and** `fencingToken`. TTL/expiry lets a new owner steal an expired lease.

### `IOutboxStore` — gaps 1, 5

```csharp
Task                                AppendAsync(OutboxAppendRequest request, CancellationToken ct = default);
Task<IReadOnlyList<DeliverableMessage>> GetDeliverableAsync(GetDeliverableRequest request, CancellationToken ct = default);
Task                                RecordDeliveryResultAsync(DeliveryResultRequest request, CancellationToken ct = default);
```

`GetDeliverable` is claim-with-lease over ordered, undelivered messages (the consumer-side mirror of
`Claim`). `RecordDeliveryResult` resolves an attempt: `Delivered` removes the message, `Retry`
re-shows it after a delay, `DeadLetter` (or exhausting `MaxAttempts`) parks it. Appending typically
happens inside the same atomic commit as the business write that produced the message (see below).

### `IOperationalUnitOfWork` — gap 3 (cross-unit atomic commit)

```csharp
public interface IOperationalSessionFactory
{
    Task<IOperationalUnitOfWork> BeginAsync(OperationalCommitScope scope, CancellationToken ct = default);
}

public interface IOperationalUnitOfWork : IAsyncDisposable
{
    IWorkQueueStore WorkQueue { get; }
    ILeaseStore     Leases   { get; }
    IOutboxStore    Outbox   { get; }
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}
```

A checkpoint that must write bookmarks + durable values + incidents + operational + scheduler state
together opens one unit of work over an `OperationalCommitScope` (the set of unit identities), uses
the seams it exposes, and commits once. Nothing is durable until `CommitAsync` succeeds; disposing
without committing rolls back.

#### Transaction-boundary abstraction & document-provider behavior

A provider advertises a `TransactionBoundary`:

- `CrossUnitAtomic` — multiple operational units commit atomically in one transaction (relational
  providers, via a single shared `DbTransaction`).
- `PerOperation` — each operation commits independently; cross-unit atomicity is unavailable
  (document-only providers such as Mongo for this contract).

This is surfaced as the `AtomicCommit` storage requirement. **Planning time:** a unit that requires
`AtomicCommit` makes the capability validator yield `Unsupported` (or `RequiresEvidence`) against a
provider that does not support it, so the mismatch is caught before deployment. **Runtime:** a
provider asked to honor a scope it cannot commit atomically throws
`UnsupportedAtomicCommitException` rather than silently losing atomicity. Document-only providers
**reject loudly**; relational providers **honor** the scope.

---

## Part B — Capability-derived workload fit

### The problem with self-declared categories

Workload classification previously forced the author to declare a **conclusion**: first
`WorkloadCandidateCategory` (`GroundworkDefault` / `BenchmarkGated` / `SpecializedProvider`), later
`StorageIntentKind` with the same three values. That category is Groundwork-internal vocabulary
leaking into a neutral storage description, and it created **two parallel taxonomies** answering the
same "is this provider compatible?" question — the enum rules *and* the capability report. The enum
path is coarser and forces a core edit for every new workload shape.

### Target: declare requirements, derive the verdict

A storage unit declares **what the data needs** as a set of `StorageRequirement` flags plus a
non-binding soft label:

```csharp
new StorageUnit(
    identity,
    "Outbox",
    StorageIntent.Operational(
        rationale: "At-least-once outbox delivery with ordered retry and lease-based claim.",
        descriptor: WorkloadIntent.OperationalStream,           // soft label: docs / diagnostics only
        StorageRequirement.OrderedConsumption,
        StorageRequirement.AtomicClaim,
        StorageRequirement.RetryRecovery,
        StorageRequirement.Idempotency,
        StorageRequirement.AtomicCommit),
    lifecycle);
```

`StorageIntent` no longer carries a `Kind`. It is:

```csharp
public sealed record StorageIntent
{
    public IReadOnlySet<StorageRequirement> Requirements { get; }
    public string? Rationale { get; }
    public WorkloadIntent Descriptor { get; }   // never gates; diagnostics / human readability only

    public static StorageIntent PortableDocument(WorkloadIntent descriptor = WorkloadIntent.RuntimeDefinedBusinessData);
    public static StorageIntent Operational(string rationale, WorkloadIntent descriptor, params StorageRequirement[] requirements);
}
```

`WorkloadIntent` (`Unspecified`, `MetadataConfiguration`, `CatalogAuthoredData`,
`RuntimeDefinedBusinessData`, `RuntimeContinuationState`, `OperationalStream`, `Projection`,
`AuditTrail`) is kept **only** as a soft label for diagnostics and human readability. It never gates
planning.

### Providers declare supported (and evidenced) capabilities

`ProviderCapabilityReport` drops `SupportedStorageIntents` and adds `EvidencedStorageRequirements`:

```csharp
IReadOnlySet<StorageRequirement> SupportedStorageRequirements;   // the provider can do this
IReadOnlySet<StorageRequirement> EvidencedStorageRequirements;   // ...and has benchmark/operational evidence
```

Provider packages expose provider-owned runtime capability factories, such as
`SqliteGroundworkCapabilities.Runtime()`. Tests or samples that need synthetic provider reports build
`ProviderCapabilityReport` values locally.

### `ProviderFit` — the computed verdict

```csharp
public abstract record ProviderFit
{
    public sealed record Supported : ProviderFit;
    public sealed record RequiresEvidence(IReadOnlyList<string> Reasons) : ProviderFit;
    public sealed record Unsupported(IReadOnlyList<StorageRequirement> MissingRequirements) : ProviderFit;
}
```

The existing `ProviderCapabilityValidator` is the **single compatibility authority**. It gains a
public `Evaluate`:

```csharp
ProviderFit Evaluate(StorageManifest manifest, ProviderCapabilityReport capabilities,
                     WorkloadEvidencePolicy? policy = null);
```

Derivation, per the union of every unit's requirements (`required`):

1. `missing = required \ SupportedStorageRequirements` → **`Unsupported(missing)`**.
2. else `needsEvidence = (required ∩ EvidenceGated) \ EvidencedStorageRequirements` →
   **`RequiresEvidence(reasons)`**.
3. else **`Supported`**.

`WorkloadEvidencePolicy.Default` gates the operational requirements as evidence-requiring **except**
`RangeQuery` (an ordinary portable query shape). This reproduces the old
`GroundworkDefault` / `BenchmarkGated` / `SpecializedProvider` trichotomy — but as a **derived
result** of capabilities + an explicit, swappable evidence policy, not an author declaration.

### How the validator gates operational manifests

`Validate` keeps every index / concurrency / materialization / query-operation check and replaces
the old kind block with `Evaluate`:

- `Unsupported` → **`GW-CAP-004`** (unsupported storage requirements) — blocks planning.
- `RequiresEvidence` → **`GW-CAP-013`** (provider must supply benchmark/operational evidence) —
  blocks planning until evidence is recorded.
- Retired: **`GW-CAP-003`** (intent-kind gate), now meaningless without `StorageIntentKind`.

`StorageManifestValidator`:

- **`GW-UNIT-005`** re-keyed: a unit that declares requirements must supply a `Rationale`.
- Retired **`GW-UNIT-004`** (portable-kind-can't-declare-requirements) and **`GW-UNIT-012`**
  (gated-kind-needs-requirements) — both keyed off the deleted `Kind`.

### Migration away from self-declared categories

No backward compatibility is maintained (confirmed). `StorageIntent.PortableDocument()` keeps the
same call shape, so portable manifests are unchanged. Every `BenchmarkGated(...)` /
`SpecializedProvider(...)` / `new StorageIntent(Kind, …)` call site moves to
`StorageIntent.Operational(rationale, descriptor, …requirements)`. Provider capability reports and
their tests add `EvidencedStorageRequirements`. Validator tests that asserted `GW-CAP-003` /
`GW-UNIT-004` / `GW-UNIT-012` become derived-fit tests asserting
`Supported` / `RequiresEvidence` / `Unsupported`.

---

## The seven gaps → capability flags

| # | Hot-path need | Operational surface | `StorageRequirement` |
|---|---|---|---|
| 1 | Atomic claim with visibility timeout (lease-on-read) | `IWorkQueueStore.ClaimAsync`, `IOutboxStore.GetDeliverableAsync` | `AtomicClaim` |
| 2 | Ordered destructive FIFO dequeue per partition | `IWorkQueueStore.DequeueAsync` | `OrderedConsumption` |
| 3 | Cross-unit / multi-document atomic commit | `IOperationalUnitOfWork` | `AtomicCommit` |
| 4 | Lease / ownership with fencing tokens + TTL | `ILeaseStore` | `LeaseRecovery` + **`FencedOwnership`** |
| 5 | Retry / idempotency metadata (attempts, next-visible, dead-letter, delivery result) | `Attempt`/`AbandonAsync`/`RecordDeliveryResultAsync` | `RetryRecovery` + `Idempotency` |
| 6 | Range / comparison queries + ordered scans | provider SQL + `SupportedQueryOperations` | **`RangeQuery`** |
| 7 | Insert-only → unique-index conflict (already covered) | unique declared index on `IDocumentStore` | *(none — verify only)* |

Net additions to `StorageRequirement`: `FencedOwnership`, `RangeQuery`. Gap 7 is proven by the
existing unique-index conflict path on the document contract and needs no new primitive.

---

## Relational reference (implemented)

`Groundwork.Operational.Relational` provides the shared SQL plumbing; `Groundwork.Sqlite` provides
the first concrete implementation (relational is the easiest place to prove ordering / lease / claim
correctness).

- `SqliteOperationalMaterializer` creates `groundwork_work_queue`, `groundwork_outbox`,
  `groundwork_leases`, `groundwork_dequeue_idempotency`, and `groundwork_operational_sequence`
  (per-partition sequence, `next_visible_utc`, `lease_token`, `attempt`, `dead_lettered`, fencing
  columns) from `RelationalOperationalSchema`.
- `RelationalOperationalStore` owns one connection guarded by a `SemaphoreSlim` (mirroring the
  document store). Autonomous calls run through a `GatedOperationalExecutor` (gate + own
  transaction + commit); a unit of work runs every enlisted call through an
  `EnlistedOperationalExecutor` over one shared `DbTransaction`, giving the
  `IOperationalUnitOfWork` cross-unit atomic commit path. `SqliteOperationalStore` fixes the boundary
  to `CrossUnitAtomic`; constructing with `PerOperation` makes `BeginAsync` throw
  `UnsupportedAtomicCommitException`.
- Range and ordering are plain SQL (`WHERE next_visible_utc <= @now ORDER BY sequence`), proving gap
  6. Exclusive claim selects the visible head in sequence order then stamps each row with a
  guarded `UPDATE … WHERE … next_visible_utc <= @now`, so exactly one claimer wins a row; the lease
  deadline is written into `next_visible_utc`, giving the visibility timeout and lease-expiry
  redelivery in one column. An ISO-8601 round-trip (`"O"`) UTC encoding keeps lexical string
  comparison aligned with chronological order. An `IOperationalClock` seam lets tests drive lease
  expiry / visibility / retry timing deterministically.

## Project & build wiring

- `src/Groundwork/Operational/Groundwork.Operational.csproj` references `Groundwork.Core` only.
- `src/Groundwork/Operational.Relational/` references Core + Operational.
- `Groundwork.Sqlite` references Operational + Operational.Relational.
- All new projects are registered in `Groundwork.slnx`, inherit central package management
  (`Directory.Packages.props`, no per-project versions), and the two core/library projects are added
  to the `GroundworkDependencyBoundaryTests` allow-list so the dependency boundary stays enforced.

## Phasing

1. **Phase 1 — done.** Capability-derived fit API in Core, operational contract signatures + DTOs in
   `Groundwork.Operational`, this design note.
2. **Phase 2 — done.** Migrated all manifests/tests off the self-declared category; wired `Evaluate`
   / `ProviderFit` into `Validate`.
3. **Phase 3 — done.** SQLite reference implementation + operational materializer.
4. **Phase 4 — done.** Provider tests: FIFO ordering, idempotent dequeue, exclusive claim under
   concurrency, lease expiry/redelivery, visibility timeout, dead-letter after N attempts, atomic
   multi-unit commit + rollback, fenced ownership.
5. **Phase 5 — done.** Capability-fit tests: operational manifest `Unsupported` vs document-only
   provider, `Supported` vs operational provider, and `ProviderFit` derives Supported /
   RequiresEvidence / Unsupported correctly.

## Out of scope

No Elsa-specific concepts or code. Other providers (SQL Server / PostgreSQL / Mongo) receive
capability/evidence declarations only — not operational implementations — in this slice. Benchmarks
are structured/stubbed; real performance numbers are a later slice.
