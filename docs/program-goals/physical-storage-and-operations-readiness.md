# Physical Storage and Operations Readiness

Status: active.

Area: physical storage / query planning / schema evolution / provider operations.

Steward(s): Groundwork maintainers plus active architects/agents.

## Purpose

Turn Groundwork's proven provider-neutral document foundation into the only concrete persistence implementation needed by application modules, without turning Groundwork into an ORM or multiplying feature migrations by database provider.

This bucket succeeds the completed [Groundwork Persistence Readiness](groundwork-persistence-readiness.md) program goal. The completed goal remains the historical record of the foundation and provider-validation slices; this goal coordinates the next generation of physical storage and operational work.

## Governing Decision

[ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md) establishes the architectural boundary for this program:

- three physical storage forms with canonical JSON as the source of truth;
- a provider-neutral `PhysicalTableDefinition` and bounded query contract;
- deterministic host and provider naming policies;
- provider-neutral schema evolution and an operations CLI;
- pooled sessions, executable capability claims, and provider conformance.

Implementation specs and issues must link to that ADR and must not silently reopen its decisions. Vocabulary and public API names are reconciled by the [Groundwork vocabulary and public API review](../reports/groundwork-vocabulary-and-public-api.md) and should be implemented through its staged compatibility strategy.

## In Scope

- Reconcile the established `StorageUnit`, `PhysicalizationPolicy`, portable/optimized, projection, materialization, query, and migration vocabulary with the three-form model.
- Define `PhysicalTableDefinition` and its relationship to manifests, storage units, materialization plans, and schema history.
- Materialize and operate shared document storage with index tables, dedicated document tables, and physical entity tables.
- Route one bounded provider-neutral query contract to the selected physical form without caller-visible storage branching.
- Support compound indexes, ranges, ordering, paging, counts, projections, and explicitly justified aggregates needed by consuming stores.
- Replace singleton/gated relational connections with pooled per-operation sessions and explicit unit-of-work sessions.
- Enforce tenant identity at the storage boundary for all tenant-aware reads and writes.
- Derive provider capability reports from executable handlers and verify every advertised capability through conformance tests.
- Generate provider-neutral schema changes from manifest diffs, support explicitly authored semantic transforms, and translate plans in each provider.
- Provide deterministic plan, validate, status, and apply workflows through a Groundwork CLI suitable for CI and deployment pipelines.
- Instrument storage, query planning, materialization, migrations, sessions, and provider health with structured logs, traces, metrics, health checks, and actionable diagnostics.
- Add a specialized append/query/retention primitive for time-ordered diagnostic data when ordinary document CRUD is not an honest fit.
- Measure correctness, latency, throughput, allocation, database work, storage cost, write amplification, migration/backfill cost, and recovery behavior.

## Out of Scope

- General-purpose `IQueryable`, arbitrary LINQ translation, or blanket ORM feature parity.
- Generic map/reduce until a concrete workload proves that incremental aggregation is required.
- Application-domain repository contracts or application-specific EF Core replacement code.
- An EF Core compatibility repository or migration of data from pre-release EF-backed applications.
- Provider-specific business naming conventions in provider adapters.
- Silent production fallback to loading and filtering an unbounded document set in memory.

## Objectives

1. Complete and ratify the vocabulary/public-API review, including the names and boundaries of all physical forms.
2. Specify the provider-neutral physical table, naming, and query-planning contracts.
3. Implement dedicated document tables and physical entity tables while preserving the portable shared-document fallback.
4. Implement pooled operation sessions and explicit transactional units of work before performance comparisons are treated as evidence.
5. Implement provider-neutral manifest-diff planning, schema history, locking, safe/destructive authorization, backfills, and CLI workflows.
6. Close required query and operational-storage gaps without introducing arbitrary query translation.
7. Pass the same capability, storage, query-plan, migration, concurrency, tenancy, restart, and failure-recovery conformance suites for SQLite, SQL Server, PostgreSQL, and MongoDB.
8. Establish reproducible performance baselines across shared documents, dedicated document tables, and physical entity tables, including an EF Core relational oracle where an application migration needs one.
9. Provide operational observability for storage, session, query-plan, materialization, migration, and provider-health behavior.
10. Publish versioned Groundwork releases that downstream application work can consume without coordinating long-lived cross-repository branches.

Session-lifecycle progress: [issue #34](https://github.com/valence-works/Groundwork/issues/34)
migrates the conventional SQLite, SQL Server, and PostgreSQL document-store factories to
short-lived materialization connections and stateless returned stores. Factory-path tests cover
SQLite serialization/private-memory rejection and relational concurrency/pool pressure. This
completes the factory-registration portion of objective 4; session/pool observability remains part
of objectives 7 and 9.

## Readiness Gates

- Canonical JSON remains authoritative in every document and physical entity row; projected columns are atomically maintained and rebuildable.
- Static document types default to dedicated document tables; types with stable scale-bearing query fields can select physical entity tables; dynamic types retain shared portable storage.
- Required production queries execute server-side or validation fails before serving traffic.
- Resolved physical names are deterministic, collision-checked, fingerprinted, and recorded in schema history.
- Additive and backfill changes can be planned and safely applied across all four providers; destructive operations require explicit authorization.
- Tenant-aware storage cannot read or mutate another tenant's data through missing ambient filters.
- Provider capability reports cannot claim behavior that lacks an executable handler and a conformance test.
- Operators can observe provider health, migration/materialization progress and failures, query-plan selection, session/pool pressure, retries, and dropped or rejected work through stable logs, traces, metrics, health checks, and diagnostics.
- Performance evidence is collected only after the session lifecycle and query-routing baselines are correct.

## Linked Surfaces

- [Universal physical storage and provider readiness PRD](https://github.com/valence-works/Groundwork/issues/25)
- [Delivery project](https://github.com/orgs/valence-works/projects/4) (private organization board)
- [Completed Groundwork Persistence Readiness goal](groundwork-persistence-readiness.md)
- [ADR 0001: Separate runtime provider and materialization capabilities](../adr/0001-separate-runtime-provider-and-materialization-capabilities.md)
- [ADR 0002: Additive-index backfill lives in the declarative materializer](../adr/0002-additive-index-backfill-in-materializer.md)
- [ADR 0003: Adopt three physical storage forms](../adr/0003-adopt-three-physical-storage-forms.md)
- [Groundwork vocabulary and public API reconciliation](../reports/groundwork-vocabulary-and-public-api.md)
- [Relational session lifecycle prototype](../reports/relational-session-lifecycle-prototype.md)
- [Stateless relational document-store factory migration](https://github.com/valence-works/Groundwork/issues/34)
- [Groundwork Physicalization and Performance specification](../../specs/019-groundwork-physicalization-performance/spec.md)
- [Groundwork Runtime Evaluation and Hardening report](../reports/groundwork-runtime-evaluation.md)
- [Groundwork Automatic Migrations specification](../../specs/021-groundwork-automatic-migrations/spec.md)

## Drift / Review Notes

- Keep generic Groundwork mechanics here and application migration work in the consuming repository's program goal.
- Treat the G7 optimized projection design as implemented history, not as the full meaning of the new three-form physical storage model.
- Preserve ADR 0001's separation between runtime provider capability and materialization capability.
- Preserve ADR 0002's rule that declarative additive backfills belong to materialization; do not create a second hand-authored path for the same operation.
- When an operational workload does not fit bounded document semantics, design a specialized Groundwork contract rather than weakening the document contract.

## Completion Conditions

Complete this bucket when the objectives and readiness gates above—including operational observability—are implemented, verified for all four providers, released in versioned Groundwork packages, and no unresolved Groundwork gap blocks downstream removal of an in-repository EF Core implementation.
