# Adopt three physical storage forms

Status: accepted (2026-07-12; ratified through the maintainer grilling and PR #26 review).

Date: 2026-07-12.

Tracking: [PRD #25](https://github.com/valence-works/Groundwork/issues/25).

Follow-up: [Groundwork vocabulary and public API reconciliation](../reports/groundwork-vocabulary-and-public-api.md).

## Context

Groundwork currently offers portable document storage and optimized projections. In relational providers, the portable form stores canonical JSON in a shared documents table and declared index values in a shared index table. Optimized physicalization adds a per-storage-unit projection table linked back to the canonical document row. MongoDB uses native collections and indexes while preserving the same document contract.

This design proves provider-neutral manifests, query semantics, materialization, and provider parity, but it leaves several problems for applications that want Groundwork to be their only concrete persistence implementation:

- one shared relational documents table can become an unnecessary contention and operational boundary for many unrelated static document types;
- a projection side table still requires a lookup back to the canonical document row even when all scale-bearing query fields are known;
- callers can encounter competing query paths instead of one contract whose implementation selects the physical plan;
- provider-specific migrations would recreate the maintenance multiplication Groundwork is intended to avoid;
- long-lived, singleton relational connections serialize work and distort both concurrency behavior and performance evidence;
- hand-maintained capability metadata can drift from behavior a provider actually executes;
- host naming conventions and provider identifier rules need separate, deterministic ownership.

Groundwork must solve these problems without exposing an ORM query surface, making physical columns authoritative, or leaking Groundwork into consuming modules' persistence contracts.

## Decision

### 1. Support three physical storage forms

Each document storage unit resolves to one of three provider-neutral physical forms:

1. **Shared documents with index tables**. Canonical document envelopes and JSON share a provider-level documents structure. Separate index structures contain declared lookup values and link to their documents. This remains the portable fallback for dynamic or runtime-defined document types.
2. **Dedicated document table**. A statically declared document type receives a dedicated table with Groundwork's standard envelope and canonical JSON schema. Dedicated document tables partition document types without requiring property-by-property relational mapping. Declared indexes may still use linked index structures where appropriate.
3. **Physical entity table**. A document type receives a dedicated table containing the standard envelope, canonical JSON, and declared provider-native projection columns. This combines document and index data needed by scale-bearing queries in one row while preserving document semantics.

The provider maps the same intent to its native structures. For example, a MongoDB provider may use a dedicated collection rather than simulating relational tables.

Defaults are deliberate:

- statically declared document types use a dedicated document table;
- types that declare stable, performance-relevant query fields use a physical entity table;
- dynamic or runtime-defined types use shared documents with index tables unless explicitly configured otherwise.

These are policy defaults, not caller-visible query choices.

### 2. Keep canonical JSON authoritative

Canonical JSON remains the source of truth in dedicated document tables and physical entity tables. Envelope metadata and projected physical columns are maintained atomically with it. Projected columns are derived, rebuildable structures; they do not create a second domain model or require an ORM-style normalized mapping.

This preserves serialization versions, upcasting, document portability, and symmetry with document databases. A columns-only entity table is not part of this model.

### 3. Describe physical tables with `PhysicalTableDefinition`

Groundwork will introduce a provider-neutral `PhysicalTableDefinition`. At minimum it describes:

- stable storage-unit identity and logical table name;
- physical storage form;
- standard envelope and canonical JSON columns;
- projected columns selected by stable serialized field paths;
- portable type, length, precision, nullability, collation, and default metadata;
- single and compound indexes, uniqueness, sort direction, and query intent;
- schema version and migration hints.

Typed expressions may be an authoring convenience, but they must resolve to stable serialized paths before the definition is fingerprinted or materialized. Portable definitions do not contain raw SQL or require provider-native types. Optional provider extensions may add provider-specific features without changing the portable meaning.

The exact relationship between `PhysicalTableDefinition`, `StorageUnit`, `PhysicalizationPolicy`, and existing projection vocabulary is settled by the [vocabulary and public-API review](../reports/groundwork-vocabulary-and-public-api.md). That review renames existing API elements without erasing the three distinct forms decided here.

### 4. Expose one bounded query contract

Callers use one provider-neutral, bounded query contract regardless of physical storage form. They do not select an index table, dedicated table, entity table, or provider query API.

The provider query planner selects a physical plan using the resolved manifest and provider capabilities. Required application queries—such as compound equality, membership, ranges, ordering, paging, counts, projections, and explicitly supported aggregates—must execute server-side when declared as scale-bearing. Production validation fails when a provider cannot execute a required query; it may not silently load and filter an unbounded set in memory.

Groundwork does not expose `IQueryable`, promise arbitrary LINQ translation, or pursue blanket EF Core feature parity. Generic map/reduce is deferred until a concrete workload demonstrates a need for incremental aggregation. Specialized workloads such as time-ordered diagnostics may use a dedicated Groundwork append/query/retention contract rather than being forced through ordinary document CRUD.

### 5. Separate logical naming from provider identifiers

Physical names resolve through two ownership layers and one explicit override:

`feature default -> host naming policy -> explicit per-unit override -> provider normalization`

The provider-agnostic host policy may add prefixes, suffixes, or organizational conventions. Hosts may register a simple function or a policy implementation. The provider owns only identifier mechanics such as quoting, reserved words, casing behavior, maximum lengths, and deterministic truncation.

Resolved names are included in plan fingerprints and schema history. Provider normalization must detect and report collisions rather than silently mapping two logical objects to the same identifier.

### 6. Make schema evolution provider-neutral

Groundwork derives semantic schema changes from differences between resolved manifests and recorded schema history. Feature authors describe a change once. Providers translate the same materialization or migration plan into SQL Server, PostgreSQL, SQLite, or MongoDB operations.

Additive table, column, index, and rebuild/backfill changes are generated where the manifest provides sufficient information. When inference is impossible, a feature author supplies one provider-neutral semantic data transform. Provider-native escape hatches remain optional and explicitly non-portable.

The operational modes are:

- `ValidateOnly`: report pending, incompatible, unsupported, or destructive changes without applying them; this is the default production startup posture.
- `ApplySafe`: apply additive and approved backfill operations under a provider migration lock.
- `ApplyAuthorized`: apply an explicitly identified destructive change after operator authorization.

Groundwork will provide a .NET CLI with deterministic human-readable and JSON output, dry-run support, stable exit codes, and at least these workflows:

- `groundwork plan`
- `groundwork validate`
- `groundwork status`
- `groundwork apply --safe`
- `groundwork apply --allow-destructive <migration-id>`

CI can plan and validate. Deployment pipelines or dedicated migrator jobs apply production changes. Development hosts may opt into safe automatic application.

ADR 0001 continues to govern the separation between runtime provider capability and materialization capability. ADR 0002 continues to govern declarative additive-index backfill; this decision broadens the structures that materialization can describe without creating a competing backfill pipeline.

### 7. Use pooled sessions and explicit units of work

Document-store and query facades are stateless. Relational providers acquire a pooled session or connection for each independent operation. Work that requires atomicity uses an explicit unit-of-work session shared by its participating operations.

Providers may impose narrower concurrency where their native behavior requires it. SQLite may use a provider-specific serialized mode, but SQL Server and PostgreSQL are not serialized behind a singleton store semaphore.

This lifecycle change is a prerequisite for representative performance comparison.

### 8. Make capabilities executable and conformance-tested

A provider capability claim must correspond to the same registered handler or execution path that implements it. Planning rejects unsupported combinations before startup or migration. Capability reports are not optimistic metadata maintained independently from execution.

Shared conformance suites verify advertised behavior for SQLite, SQL Server, PostgreSQL, and MongoDB, including:

- all applicable physical forms and equivalent query results;
- the expected physical query plan, not only result equality;
- materialization, migration, backfill, and schema history;
- optimistic concurrency and explicit unit-of-work atomicity;
- tenant isolation on load, save, update, delete, and query;
- restart, failure recovery, and migration locking.

Tenant-aware units stamp and validate tenant identity at the storage boundary. Unique indexes include tenant scope. Tenant-agnostic access requires an explicit privileged session; an ambient query filter is not sufficient isolation.

Groundwork exposes operational evidence for the same executable paths. Storage and query operations, session/pool pressure, query-plan selection, materialization and migration progress, retries/failures, and provider health emit structured logs, traces, metrics, health checks, or diagnostics appropriate to their lifecycle. Capability conformance includes the presence and accuracy of this evidence where the capability is operationally significant.

## Performance acceptance policy

Correctness, durability, concurrency, tenant isolation, and restart behavior are absolute prerequisites. Performance comparisons must include the shared-document, dedicated-document, and physical-entity forms and must not be treated as evidence until pooled sessions and native query routing are in place.

For application migrations that compare Groundwork with an EF Core oracle, initial provisional gates are:

- runtime hot paths: Groundwork p95 latency no worse than 1.10 times EF Core and throughput at least 90 percent of EF Core;
- ordinary feature stores: Groundwork p95 latency no worse than 1.25 times EF Core and throughput at least 80 percent of EF Core;
- Groundwork p99 latency no worse than twice EF Core;
- physical entity tables must show a repeatable benefit over both shared and dedicated document forms for the workload that selects them.

The first controlled baseline may ratify revised thresholds, but it must publish workload, scale, payload, selectivity, concurrency, database, hardware, query-plan, allocation, storage, and write-amplification evidence rather than silently relaxing a gate.

## Alternatives considered

### Keep one shared documents table plus projection tables

This is portable and already implemented, but it leaves unrelated static types in one relational structure and retains a join or second lookup for optimized reads. It remains one supported form rather than the only form.

### Store physical entities as columns without canonical JSON

This can resemble a conventional normalized relational store, but it makes physical mappings authoritative and weakens serialization/upcasting portability. It would pull Groundwork toward ORM-style schema ownership and multiply provider-specific evolution concerns.

### Expose a different store or query API for every physical form

This makes physical layout an application concern and prevents providers from changing plans transparently. The physical form is a manifest and planner decision, not a caller responsibility.

### Reproduce `IQueryable` and general EF Core behavior

This would create a large, provider-sensitive translation surface that Groundwork does not need. Bounded application store contracts and declared query capabilities make unsupported behavior visible and testable.

### Maintain migrations per feature and provider

This recreates the maintenance explosion Groundwork is intended to solve. One semantic plan with provider translations keeps feature intent provider-neutral while preserving explicit escape hatches.

### Keep singleton relational connections behind a semaphore

This simplifies ownership but serializes unrelated operations, hides pool behavior, and invalidates representative SQL Server and PostgreSQL concurrency measurements.

## Consequences

- Groundwork gains a larger materialization and query-planning surface, but one that is bounded by declared manifests and executable provider capabilities.
- Existing optimized projection behavior remains valid as the index-table form; “physicalization” can no longer be used as a synonym for projection side tables alone.
- `PhysicalizationPolicy`, portable/optimized terminology, projection APIs, and migration vocabulary are reconciled by the [vocabulary and public-API review](../reports/groundwork-vocabulary-and-public-api.md); implementation should follow its staged compatibility strategy.
- Providers must implement more schema and query-plan conformance, not merely return equivalent documents.
- Canonical JSON permits rebuilds and cross-provider semantics at the cost of retaining JSON storage alongside native projected columns.
- Application hosts gain naming and deployment control without leaking provider identifier mechanics into feature definitions.
- Operators gain deterministic validation and migration tooling suitable for CI/CD.
- Operators gain first-class provider health, migration/materialization, query-plan, session, retry, and failure observability rather than relying on application-specific instrumentation.
- Specialized operational contracts remain allowed inside Groundwork; standardization means one concrete persistence ecosystem, not one universal CRUD abstraction.
