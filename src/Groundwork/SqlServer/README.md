# Groundwork.SqlServer

`Groundwork.SqlServer` provides SQL Server materialization and document-store operations for portable Groundwork documents.

It also implements the provider-neutral diagnostic-record contract through
`SqlServerDiagnosticRecordStoreFactory`. Diagnostic stores use independent pooled sessions, native
`TOP`, `CHARINDEX`, session-owned per-stream `sp_getapplock` locks, binary UTF-8 comparison keys, filtered
latest-per-key indexes, durable operation tombstones, and bounded cleanup.

## Current Scope

- Creates document, declared-index, and schema-history tables.
- Saves, loads, updates, deletes, and queries JSON document envelopes.
- Supports equality, set-membership (`IN`), and case-insensitive `Contains` (LIKE) query operations over declared indexes.
- Supports declared-index ordering and skip/take pagination (`OFFSET`/`FETCH`).
- Maintains declared indexes transactionally with document writes.
- Enforces unique declared indexes with a filtered unique index.
- Uses optimistic concurrency through expected document versions.
- Exposes `SqlServerGroundworkCapabilities.Runtime()` and `SqlServerGroundworkCapabilities.Materialization()`.

## Factory and session lifecycle

`SqlServerDocumentStoreFactory.CreateAsync` materializes through one short-lived pooled connection,
disposes it before returning, and returns `SqlServerDocumentStore` directly. The returned store is
stateless: independent operations acquire concurrent pooled connections, while an explicit unit of
work owns one connection and transaction until completion. Pool limits and timeouts—not a
Groundwork-wide semaphore—provide backpressure.

Diagnostic-record databases must have `READ_COMMITTED_SNAPSHOT ON`. Materialization validates this
prerequisite and fails with an actionable error when it is absent; the provider does not silently
change a host-owned database setting. Row-versioned reads let inspection observe the last durable
snapshot while a retention transaction is staged.

The former `SqlServerDocumentStoreHandle` and its lifetime-owning `Connection` property were removed
because a stateless store has no single connection to expose or dispose.

## Deliberate Limits

- JSON content is stored as text.
- Document kinds, document ids, declared index names, declared index values, and physicalized
  projection values are retained exactly in binary-collated `NVARCHAR(450)` columns. They do not
  participate directly in native composite keys. Persisted SHA-256 shadow columns provide bounded
  `BINARY(32)` primary, foreign, and unique-index keys, keeping every declared key below SQL
  Server's 900-byte limit even when every original value is at its 450-code-unit maximum.
- Queries compare both retained original values and their scope boundary semantics; the digest is
  only a bounded native key. A hypothetical digest collision cannot overwrite or disclose another
  original identity: the native constraint rejects the colliding write and original-value
  predicates never treat the values as equal.
- Diagnostic identifiers use U+0020 through U+007E without boundary whitespace. Scope, stream,
  field, and nonce components are limited to 64 bytes; record ids to 128 bytes. Ordinal string field
  bounds are limited to 128 UTF-8 bytes and their sortable keys to `VARCHAR(512)`. Definitions that
  can exceed SQL Server's 2,100-parameter command ceiling are rejected before materialization.
- No Entity Framework dependency.
- No host-specific dependency.
