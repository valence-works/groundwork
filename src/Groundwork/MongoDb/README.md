# Groundwork.MongoDb

`Groundwork.MongoDb` provides MongoDB materialization, document-store operations, and the
transactional diagnostic-record provider.

## Current Scope

- Compiles host naming and provider-neutral definitions into immutable MongoDB physical routes.
- Maps shared documents to a discriminator-bearing shared collection plus optional unit-owned linked
  projection collection, dedicated documents to unit-owned collections, and physical entities to
  unit-owned collections with in-document projected fields.
- Preserves the exact canonical JSON string as the authority in every primary document; native BSON
  content and projected fields are rebuildable copies.
- Converts projected numeric values from their original JSON lexemes before BSON can round them,
  enforces declared Decimal precision/scale, and stores projected DateTime instants as UTC tick
  integers so equality, uniqueness, and ranges preserve the portable 100ns contract. Native
  Number/DateTime query paths without an exact typed projection are refused before traffic.
- Creates scoped, compound, unique, and direction-aware native indexes from resolved physical routes.
- Applies additive schema changes through a renewable, generation-fenced manifest/provider lease;
  operation evidence and the applied-state compare-and-swap are atomically fenced, while durable
  document-incarnation tokens make canonical-JSON backfills safe across delete/recreate races.
  Evidence is target-qualified, and an attempt that did not publish its target never skips later
  backfill or validation. Required-field finalization is durably acknowledged after its canonical
  backfill even though MongoDB needs no collection-level nullability alteration.
- Requires MongoDB physical-schema leases to be at least one second so BSON millisecond timestamp
  precision and renewal scheduling jitter cannot make an accepted lease expire at acquisition.
  The default remains five minutes and renewal runs at one third of the configured duration.
- Detects same-version definition changes through route fingerprints and rejects out-of-band native
  index key, uniqueness, collation, partial, sparse, hidden, TTL, and wildcard-option conflicts
  against durable applied evidence.
- Executes route-aware CRUD, optimistic concurrency, linked projection maintenance, and replica-set
  units of work atomically. Configurable attempt and elapsed-time budgets bound fresh-session body
  retries and same-session commit-only retries. Exhausted ambiguous commits raise provider-neutral
  acknowledgement-uncertain evidence instead of being misreported as conflicts. A non-successful or
  failed explicit unit-of-work write aborts that unit and makes it terminal. Document kinds outside
  the immutable commit scope are rejected before database traffic without poisoning the unit.
- Compiles `DocumentQuery` declarations through exact handler certifications; linked lookups and
  native primary/entity lookups execute filtering, count, ordering, and paging in MongoDB. Count,
  page, and linked-primary hydration share one snapshot attempt; transient failures retry the whole
  attempt on a fresh session within the same attempt and elapsed-time budgets.
- Exposes native `explain` evidence for the resolved collection and selected physical index.
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

- The compatibility materializer/store retain their pre-route one-field API. New three-form work
  uses `MongoDbPhysicalStorageModel`, the physical materializer overload, and
  `MongoDbPhysicalDocumentStore` (or `CreatePhysicalAsync`).
- Physical bounded handlers currently certify offset paging, not keyset continuation or
  latest-per-key selection; declarations requiring either fail during store construction.
- Canonical JSON is stored as a string while a provider-owned native BSON copy supports ordinary
  server-side document-path evaluation and rebuildable projections.
- No Entity Framework dependency.
- No host-specific dependency.
- Standalone MongoDB deployments cannot serve atomic multi-object physical writes or the
  diagnostic-record contract; use a replica set or sharded cluster. `CreatePhysicalAsync` probes
  this requirement before compiling or materializing the physical model, so rejection creates no
  database state.
