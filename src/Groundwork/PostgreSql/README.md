# Groundwork.PostgreSql

`Groundwork.PostgreSql` provides PostgreSQL materialization and document-store operations for portable Groundwork documents.

## Current Scope

- Creates document, declared-index, and schema-history tables.
- Saves, loads, updates, deletes, and queries JSON document envelopes by declared index.
- Supports equality, set-membership (`IN`), and case-insensitive `Contains` (ILIKE) query operations over declared indexes.
- Supports declared-index ordering and skip/take pagination (`LIMIT`/`OFFSET`).
- Maintains declared indexes transactionally with document writes.
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
- No Entity Framework dependency.
- No host-specific dependency.
