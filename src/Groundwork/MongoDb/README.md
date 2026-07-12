# Groundwork.MongoDb

`Groundwork.MongoDb` provides MongoDB materialization, document-store operations, and the
transactional diagnostic-record provider.

## Current Scope

- Creates one MongoDB collection per storage unit.
- Creates native MongoDB indexes for declared one-field indexes.
- Saves, loads, updates, deletes, and queries JSON document envelopes.
- Supports equality, set-membership (`$in`), and case-insensitive `Contains` (regex) query operations over declared indexes.
- Supports declared-index ordering and skip/limit pagination.
- Enforces unique declared indexes with MongoDB unique indexes.
- Uses optimistic concurrency through expected document versions.
- Exposes `MongoDbGroundworkCapabilities.Runtime()` and `MongoDbGroundworkCapabilities.Materialization()`.
- Materializes native scoped record, stream-state, provider-clock, append-ledger, and trim-ledger collections.
- Executes diagnostic append, trim, cursor allocation, and operation outcomes atomically.
- Executes bounded predicates, ordering, snapshot continuation, exact count, latest-per-key, and retention server-side.
- Persists `groundwork-ascii-lower-v1` comparison keys under MongoDB's default binary string semantics.
- Persists ordinal strings as fixed-width UTF-16 hexadecimal keys (`groundwork-utf16-hex-v1`) so MongoDB binary order matches .NET ordinal order.
- Persists and validates a canonical stream-definition fingerprint, schema version, limits, and comparison-key algorithm.
- Keeps append replay outcomes in bounded per-record documents so legal batches cannot exceed MongoDB's 16 MiB document limit.
- Converts expired replay outcomes into minimal durable tombstones, then removes tombstones after their admission horizon.
- Runs page/count/high-water and inspection reads under one MongoDB snapshot transaction.

## Diagnostic Deployment Requirement

`MongoDbDiagnosticRecordStoreFactory` requires a replica set or sharded cluster because a diagnostic
append or trim changes multiple documents atomically. It rejects standalone deployments before
returning a store. A direct connection to a replica-set member is accepted after inspecting the
connected server topology. Each operation opens an independent driver session; the `MongoClient`
connection and session pools remain shared by stores created over the same database client.
Diagnostic collections and indexes are created with simple collation; incompatible existing
collations are rejected. Provider-clock writes and transaction commits explicitly use majority
write concern, and transactions use snapshot read concern rather than inheriting weaker client
defaults.

## Deliberate Limits

- One-field indexes only.
- JSON content is stored as native BSON under a `content` field.
- No Entity Framework dependency.
- No host-specific dependency.
- Standalone MongoDB deployments cannot serve the diagnostic-record contract.
