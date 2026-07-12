# Groundwork.Sqlite

`Groundwork.Sqlite` is the first concrete Groundwork provider. It materializes portable document storage for a `StorageManifest` using `Microsoft.Data.Sqlite` directly, without Entity Framework or host-specific dependencies.

## Current Scope

- Creates the shared document, declared-index, and schema-history tables.
- Saves, loads, updates, deletes, and queries JSON document envelopes.
- Supports equality, set-membership (`IN`), and case-insensitive `Contains` (LIKE) query operations over declared indexes.
- Supports declared-index ordering and skip/take pagination.
- Maintains declared index rows transactionally with document writes.
- Enforces optimistic concurrency with expected document versions.
- Rejects queries for undeclared indexes.
- Enforces unique declared indexes through SQLite constraints.
- Provides a durable Groundwork migration executor backed by `groundwork_migration_history`.
- Adds and backfills new optimized physicalized projection columns during materialization.
- Exposes `SqliteGroundworkCapabilities.Runtime()` and `SqliteGroundworkCapabilities.Materialization()`.
- Materializes a dedicated diagnostic-record schema and executes the complete bounded
  `IDiagnosticRecordStore` contract through a real, file-backed SQLite database.
- Persists scoped stream cursor state, immutable records and multi-value fields, append/trim
  outcomes and tombstones, and a durable provider-clock high-water.
- Executes predicates, snapshot continuation, exact count, latest-per-key, ordering, and retention
  in SQL. Comparison keys use `groundwork-ascii-lower-v1` and SQLite `BINARY` collation.

## Factory and session lifecycle

`SqliteDocumentStoreFactory.CreateAsync` materializes through one short-lived connection and returns
`SqliteDocumentStore` directly. The returned store owns no connection: each operation opens and
disposes its own connection behind a provider-owned serialization gate, and each explicit unit of
work owns one connection and transaction until completion.

The stateless factory accepts file-backed SQLite only. A private `Data Source=:memory:` database is
connection-scoped and is therefore rejected; use the direct `SqliteConnection` constructor when a
retained, explicitly serialized in-memory store is intentional. The former
`SqliteDocumentStoreHandle` and its lifetime-owning `Connection` property were removed because no
truthful connection can represent the lifetime of a stateless store.

`SqliteDiagnosticRecordStoreFactory.CreateAsync` applies the provider-neutral relational diagnostic
schema through a short-lived materialization connection. The returned store uses serialized,
per-operation sessions and explicit transactions. Reads use deferred transactions; writes acquire
SQLite's cross-connection writer boundary before ledger or cursor reads, preventing deferred
read-to-write upgrade races between independently created stores. Expired append and trim operation
rows are removed in bounded batches only after their durable tombstone/admission horizon.
`SqliteDiagnosticRecordStore` can be constructed directly only when the schema has already been
materialized.

## Deliberate Limits

- Single-field index extraction only.
- JSON content is stored as text; provider-specific JSON indexing is deferred.
- This package is a Groundwork provider package, not a host-specific integration package.
