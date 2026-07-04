# Groundwork.MongoDb

`Groundwork.MongoDb` provides MongoDB materialization and document-store operations for portable Groundwork documents.

## Current Scope

- Creates one MongoDB collection per storage unit.
- Creates native MongoDB indexes for declared one-field indexes.
- Saves, loads, updates, deletes, and queries JSON document envelopes.
- Supports equality, set-membership (`$in`), and case-insensitive `Contains` (regex) query operations over declared indexes.
- Supports declared-index ordering and skip/limit pagination.
- Enforces unique declared indexes with MongoDB unique indexes.
- Uses optimistic concurrency through expected document versions.
- Exposes `MongoDbGroundworkCapabilities.Runtime()` and `MongoDbGroundworkCapabilities.Materialization()`.

## Deliberate Limits

- One-field indexes only.
- JSON content is stored as native BSON under a `content` field.
- No Entity Framework dependency.
- No host-specific dependency.
