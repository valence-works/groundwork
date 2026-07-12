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
- Document kinds, document ids, declared index names, declared index values, and physicalized projection values are constrained to `NVARCHAR(450)` because they participate in SQL Server keys or indexes. Keep portable identifiers and indexed values within that limit, or add validation before writing.
- No Entity Framework dependency.
- No host-specific dependency.
