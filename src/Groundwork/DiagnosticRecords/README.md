# Groundwork Diagnostic Records

`Groundwork.DiagnosticRecords` is the provider-neutral contract for immutable, high-volume,
time-ordered diagnostic streams. It is intentionally separate from ordinary document CRUD and from
destructive operational primitives such as work queues and outboxes.

This package implements the contract decision tracked by
[Groundwork issue #30](https://github.com/valence-works/Groundwork/issues/30). Its boundaries follow
[ADR 0003](../../../docs/adr/0003-adopt-three-physical-storage-forms.md), the
[vocabulary/API reconciliation](../../../docs/reports/groundwork-vocabulary-and-public-api.md)
landed by [PR #29](https://github.com/valence-works/Groundwork/pull/29), and the source-verified
[Elsa diagnostics workload](https://github.com/elsa-workflows/elsa-foundation/blob/main/docs/reports/diagnostics-storage-workload.md).

It is not a fourth document physical-storage form. Provider implementations may materialize a
stream with a physical entity table/collection and linked multi-value indexes, but callers see only
the specialized record contract.

## Contract surface

`IDiagnosticRecordStore` exposes four bounded operations:

- `AppendAsync` atomically appends one already-batched request to one explicit tenant/scope and
  stream. Groundwork assigns the durable monotonic cursor.
- `QueryAsync` executes a closed predicate tree, cursor-only or field-plus-cursor order, optional
  latest-per-key selection, snapshot keyset continuation, and an optional exact count.
- `InspectAsync` returns exact retained count plus trim-independent lifetime cursor and logical
  high-water metadata.
- `TrimAsync` implements exact scope-local `KeepNewest` retention.

Every request carries `DiagnosticStorageScope`, which contains both tenant identity and the host
storage-scope identity. Record ids, cursors, operation ledgers, queries, counts, and trims are all
isolated by `(tenant, scope, stream)`.

Append and trim operation ids contain an issuance time and nonce. The stream definition declares a
bounded future clock-skew allowance; a new operation beyond it is rejected, while the same allowance
protects a legitimately slow caller at the old edge of the admission window. Caller time never owns
ledger retention. A provider records the successful commit time and keeps the operation outcome
until `commit time + idempotency window`; replay lookup therefore precedes new-operation freshness
validation. `DiagnosticRecordRequestValidator.Validate` checks request shape and fingerprint, while
`ValidateNewOperationAdmission` checks only admission time after a validated request's durable replay
lookup misses, avoiding repeated payload validation on the append hot path.

Provider time is a durable, monotonically nondecreasing UTC high-water, computed as the maximum of
the current provider clock and the previously recorded high-water. Wall-clock regression after a
restart may not move it backward. When an outcome expires, its operation identity becomes a minimal
tombstone and every later use returns `DiagnosticOperationExpiredException`, regardless of
fingerprint. The tombstone is retained through the later of:

- `provider commit time + idempotency window`; and
- `caller issued-at + idempotency window + maximum clock skew`.

The boundary is inclusive. A provider may remove the tombstone only after its durable provider-time
high-water is strictly greater than that horizon. At that point new-operation admission is
mathematically impossible, and the nondecreasing high-water prevents the identity becoming fresh
again after clock regression. This permits bounded tombstone cleanup without allowing a consecutive
post-expiry retry to re-commit records or re-run a trim.

Request fingerprints use a versioned, length-delimited SHA-256 encoding over the complete ordered
request. A retry inside the provider-recorded ledger window returns the original outcome; the same
operation id with a different fingerprint is a conflict, and an expired operation is rejected instead
of being treated as new work. `DiagnosticAcknowledgementLostException` explicitly reports the
uncertain-acknowledgement case so a caller can inspect or safely retry.

## Telemetry contract

Every shipped provider routes its public store and all four entries in
`DiagnosticRecordStoreHandlers` through `InstrumentedDiagnosticRecordStore`. The decorator accepts
an immutable, bounded `DiagnosticRecordTelemetryIdentity`; provider and store values contain at most
64 lowercase ASCII letters, digits, periods, underscores, or hyphens. Application-owned handlers can
use the same decorator without adding provider-specific instrumentation. When neither its activity
source nor relevant instruments have listeners, each public method returns the underlying handler's
original `ValueTask` without an async state machine. With telemetry enabled, the non-async boundary
also preserves synchronous request-validation and provider throws. A completed faulted or canceled
`ValueTask` remains awaitable instead of becoming a synchronous throw; only incomplete provider
operations continue through an async helper. The boundary restores the caller's ambient activity
before every return while the incomplete operation retains its own span context.

The OpenTelemetry-compatible contract version is `1.0.0`. `DiagnosticRecordTelemetry` exposes every
name below as a public constant. The `ActivitySource` and `Meter` are both named
`Groundwork.DiagnosticRecords` and carry the contract version. Changing the meaning or value domain
of an existing name is a breaking telemetry-contract change and requires a new contract version;
additive instruments or tags must remain bounded and non-sensitive.

| Operation | Activity name | Successful outcomes |
|---|---|---|
| append | `groundwork.diagnostic_records.append` | `committed`, `replayed` |
| query | `groundwork.diagnostic_records.query` | `success` |
| inspect | `groundwork.diagnostic_records.inspect` | `success` |
| trim | `groundwork.diagnostic_records.trim` | `completed`, `replayed` |

Every activity carries `groundwork.diagnostic_records.operation`, `.provider`, `.store`, `.outcome`,
`.classification`, `.scope.kind`, and `.scope.present`; a non-null request also supplies `.stream`.
Scope kind is the fixed value `tenant_scope`; scope presence is a boolean. Operation-specific
activity tags expose only bounded shape: append batch size; query limit plus exact-count,
latest-per-key, and continuation booleans; and trim keep-newest. Tenant id, storage-scope id, payload,
record id, operation nonce, fingerprint, exception message, and other request values are never
recorded. Stream is intentionally present on activities for trace diagnosis but absent from metrics
to prevent unbounded metric cardinality. A null request is passed unchanged to the underlying
handler so its synchronous-throw versus faulted-`ValueTask` contract cannot change when telemetry is
enabled; request-derived tags are omitted and scope presence is `false` on that rejected span.

Failure outcomes and classifications are deterministic:

| Condition | Outcome | Classification | Activity status |
|---|---|---|---|
| operation-id conflict | `conflict` | `conflict` | error |
| validation, fingerprint mismatch, expiry, or clock skew | `rejected` | `rejection` | error |
| caller/provider cancellation | `cancelled` | `cancellation` | error |
| uncertain acknowledgement | `acknowledgement_lost` | `acknowledgement_loss` | error |
| any other provider exception | `provider_failure` | `provider_failure` | error |

Metrics use only operation, provider, store, outcome, classification, and disposition tags:

| Instrument | Type/unit | Meaning |
|---|---|---|
| `groundwork.diagnostic_records.operation.duration` | histogram, seconds | provider-boundary latency for all four operations |
| `groundwork.diagnostic_records.operation.outcomes` | counter, operations | success, replay, conflict, rejection, cancellation, acknowledgement loss, or provider failure |
| `groundwork.diagnostic_records.append.batches` | counter, batches | `accepted`, definitely `rejected`, or `indeterminate` disposition |
| `groundwork.diagnostic_records.append.records` | counter, records | records committed; replay never increments it |
| `groundwork.diagnostic_records.query.exact_count.requests` | counter, requests | exact-count usage |
| `groundwork.diagnostic_records.query.latest_per_key.requests` | counter, requests | latest-per-key usage |
| `groundwork.diagnostic_records.trim.records.examined` | counter, records | records examined by non-replayed completed trims |
| `groundwork.diagnostic_records.trim.records.deleted` | counter, records | records deleted by non-replayed completed trims |
| `groundwork.diagnostic_records.retained_records` | histogram, records | retained count from inspect and non-replayed completed trim |

Replay is the boundary-observable retry outcome; acknowledgement loss identifies work that may need
a safe retry. Cancellation, acknowledgement loss, and provider failure use an `indeterminate` append
disposition because the shared seam cannot prove whether the provider committed. The decorator does
not invent counts for internal provider retry attempts or operation-ledger cleanup because neither
is observable through `IDiagnosticRecordStore`. Those signals should
be added only if a future provider-neutral contract exposes truthful results. Groundwork configures no
exporter, sampling policy, dashboard, or host pipeline.

## Stream definitions and bounded queries

`DiagnosticRecordStreamDefinition` declares:

- stream and logical storage identity plus schema version;
- append/trim idempotency windows and a bounded operation clock-skew allowance;
- bounded batch, payload, record-id, field, query-limit, and predicate-node sizes;
- portable scalar or multi-value fields, types, case policy, supported predicates, ordering, and
  latest-per-key support; and
- an optional scalar `Int64` logical high-water field.

Every record also has the built-in `$occurredAt` timestamp field. It supports equality, membership,
inclusive range, and field-plus-cursor ordering without duplicating occurrence time into metadata.
Field order is scalar-only; records missing an optional ordered field are explicitly excluded from
both that query's page and exact count.

The query API contains no `IQueryable`, provider expression, offset, or arbitrary aggregation.
`DiagnosticRecordQueryValidator` validates every scale-bearing operation against both the stream
definition and `IDiagnosticQueryHandler.Capabilities`. Capability metadata therefore comes from the
same executable handler that runs the query; an unsupported operation fails before execution and
cannot silently fall back to client-side loading.

String fields select one explicit comparison policy. `Ordinal` uses versioned UTF-16 keys.
`AsciiIgnoreCase` accepts only U+0020 through U+007E and maps `A` through `Z` to `a` through `z`; it
has no culture, normalization, operating-system, or Unicode-version dependency.
`UnicodeOrdinalIgnoreCase` accepts every well-formed Unicode string and maps each Unicode scalar into
a fixed-width scalar key only when the active runtime's ordinal-ignore-case comparer accepts that
simple-uppercase mapping. This guard matters when the runtime's general Unicode table is newer than
its ordinal-casing table. It preserves .NET ordinal-ignore-case equality and ordering without
delegating semantics to provider collations. The Unicode algorithm id fingerprints every accepted
non-identity scalar mapping in the active runtime, so Unicode-data or ordinal-casing drift is detected
as physical schema drift instead of silently mixing keys.
Malformed UTF-16 is rejected before provider I/O; no policy performs Unicode normalization.

The canonical comparison policies, key algorithms, and SHA-256 lookup-hash projection live in
`Groundwork.Core.Text.PortableStringComparison`, so document identity and diagnostic records share
one implementation and one set of persisted version IDs. Diagnostic records map their case-policy
enum at the package boundary and add only their storage-specific bounded prefix and substring-search
projection.

Providers persist the full comparison key for exact collision checks, a bounded order-preserving
prefix for ordering, a SHA-256 key for equality and membership seeks, and a boundary-delimited search
key for substring predicates. Native indexes contain only the bounded prefix or hash, never the full
comparison or search key. Long values that share the entire bounded prefix are ordered by the full key
and cursor, and hash matches are always rechecked against the full key. The persisted stream-definition
state binds the complete definition to the comparison-algorithm manifest and their fingerprints; a
definition or algorithm mismatch must be resolved through explicit schema evolution/backfill.
The manifest separately versions the comparison mapping, UTF-8/SHA-256/lower-hex lookup hash, and
boundary-delimited search-key format, and records the bounded-prefix length.

`Contains` deliberately executes as a server-native substring filter over the boundary-delimited
search key. It is not a substring B-tree seek: every provider first narrows the candidates through a
native `(tenant, scope, stream, field, type)` access path and applies the substring filter on the
server. It never scans unrelated scopes or evaluates records in the client. The snapshot high-water,
request limits, 32 MiB projected-comparison budget, and stream retention policy bound the work; hosts
that need arbitrary unbounded text search should use a dedicated search projection instead.

Each provider validates its own legal field and aggregate request bounds before opening a connection,
creating a database/file, probing topology, or issuing DDL. Provider-specific limits may be narrower
than the provider-neutral contract, but a legal request is never silently truncated or evaluated on
the client. The shipped adapters accept at most 65,536 UTF-8 bytes per string. Definition validation
proves that one record and one maximum predicate/continuation shape fit the comparison budget, while
append and query admission also sum the actual request values with overflow-safe arithmetic. The
48-times input estimate covers the exact-size full/search UTF-16 strings and transient hash encoding;
the full key is computed once and reused for prefix, hash, and search projection.

Relational public store constructors perform a retryable one-time materialization admission after
request validation and before their first provider operation. That admission reads the persisted
definition and comparison-algorithm fingerprints, so direct construction cannot bypass drift checks.
Concurrent callers share one successful admission; failure or caller cancellation does not poison a
store and a later operation retries. Async factories return an already-admitted store after completing
the same check explicitly.

Continuation values carry the first page's committed cursor high-water, the exclusive last key,
and a canonical fingerprint of both the query shape and stream definition. Concurrent or backdated
appends are excluded from the existing traversal, and a continuation cannot be reused with a
different filter, order, limit, count, scope, stream, latest-per-key request, or definition version.
Providers capture mutable requests before asynchronous work. `DiagnosticRecordQuerySnapshot` freezes
nested predicate/value collections, while `DiagnosticRecordSnapshot` gives providers a reusable deep
copy for append outcomes and query pages. Conformance requires returned collections to reject mutation
so a caller cannot alter a later idempotent replay or another read.

## Provider conformance

`Groundwork.DiagnosticRecords.Tests` contains the reusable abstract
`DiagnosticRecordStoreConformanceTests` suite. A provider supplies only an
`IDiagnosticRecordStoreConformanceFixture`; the inherited tests run unchanged. The deterministic
in-memory fixture proves the suite itself and supports restart plus injected pre-commit and
post-commit/pre-acknowledgement execution points. Its mid-batch hook must be placed by each concrete
provider after at least one record is staged inside the atomic transaction; throwing there proves
rollback of records, cursor allocation, and the operation ledger. The trim mid-transaction hook is
placed after at least one record is staged for deletion and proves that records, statistics, and the
trim ledger roll back together on failure or cancellation.

The suite covers atomic and concurrent append, duplicate and fingerprint conflicts, expiration,
tenant isolation, scalar/multi-value predicates, case policy, inclusive boundaries, both ordering
forms, snapshot continuation, latest-per-key, exact counts, retention boundaries, restart,
cancellation, and uncertain acknowledgement. SQLite, SQL Server, PostgreSQL, and MongoDB run the
same suite and assert native server-side plans; the in-memory fixture is not a production provider
or an authorization to perform client evaluation.

Capture channels, batch sizing, retry/backoff, overload shedding, graceful drain, redaction, live
subscriptions, and application drop accounting remain consumer policy. Mutable diagnostic catalogs
remain ordinary documents. Generic reduce/map-reduce and arbitrary aggregation are out of scope.
