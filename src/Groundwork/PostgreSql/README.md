# Groundwork.PostgreSql

`PostgreSqlPhysicalSchemaExecutor`, `PostgreSqlPhysicalDocumentStore`, and
`PostgreSqlPhysicalQueryRuntime` implement all three compiled physical storage forms.
`PostgreSqlPhysicalMutationRuntime` executes declared bounded transitions and deletes with exact
idempotent outcomes. Schema
application uses advisory locks and a transactional operation ledger; document and query operations
use independent pooled sessions. Portable date-time projections use exact UTC ticks to avoid native
microsecond rounding, and no client-side query fallback is available.

`Groundwork.PostgreSql` provides PostgreSQL materialization and document-store operations for portable Groundwork documents.

It also implements the provider-neutral diagnostic-record contract through
`PostgreSqlDiagnosticRecordStoreFactory`. Diagnostic stores use independent pooled sessions, native
`LIMIT`, `strpos`, session advisory per-stream locks, `C`-collated comparison keys, partial latest-per-key
indexes, durable operation tombstones, and bounded `ctid` cleanup.

## Current Scope

- Creates document, declared-index, and schema-history tables.
- Saves, loads, updates, deletes, and queries JSON document envelopes by declared index.
- Supports equality, set-membership (`IN`), and case-insensitive `Contains` (ILIKE) query operations over declared indexes.
- Supports declared-index ordering and skip/take pagination (`LIMIT`/`OFFSET`).
- Maintains declared indexes transactionally with document writes.
- Executes declared bounded mutations with transaction-scoped advisory locks, exact identity
  selection, and durable replay evidence.
- Enforces unique declared indexes with a partial unique index.
- Uses optimistic concurrency through expected document versions.
- Exposes `PostgreSqlGroundworkCapabilities.Runtime()` (advertising `IndexCapabilities.All` and the full portable query-operation set) and `PostgreSqlGroundworkCapabilities.Materialization()`.

## Factory and session lifecycle

`PostgreSqlDocumentStoreFactory.CreateAsync` materializes through one short-lived pooled connection,
disposes it before returning, and returns `PostgreSqlDocumentStore` directly. The returned store is
stateless: independent operations acquire concurrent pooled connections, while an explicit unit of
work owns one connection and transaction until completion. Pool limits and timeouts—not a
Groundwork-wide semaphore—provide backpressure.

The former `PostgreSqlDocumentStoreHandle` and its lifetime-owning `Connection` property were
removed because a stateless store has no single connection to expose or dispose.

## Deliberate Limits

- JSON content is stored as text (no `jsonb` column or provider-specific JSON indexing).
- Definitions whose predicate bounds can exceed PostgreSQL's 65,535-parameter command ceiling are
  rejected before materialization.
- No Entity Framework dependency.
- No host-specific dependency.
