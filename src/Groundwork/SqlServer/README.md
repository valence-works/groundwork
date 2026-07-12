# Groundwork.SqlServer

`Groundwork.SqlServer` provides SQL Server materialization and document-store operations for portable Groundwork documents.

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
- No Entity Framework dependency.
- No host-specific dependency.
