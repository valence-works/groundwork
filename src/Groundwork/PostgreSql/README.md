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

## Deliberate Limits

- JSON content is stored as text (no `jsonb` column or provider-specific JSON indexing).
- No Entity Framework dependency.
- No host-specific dependency.
