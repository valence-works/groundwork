# Groundwork vocabulary and public API reconciliation

Status: source-verified design follow-up under accepted [ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md); this report does not reopen the governing storage-form decision.

Date: 2026-07-12.

Tracking: [PRD #25](https://github.com/valence-works/Groundwork/issues/25) and [issue #28](https://github.com/valence-works/Groundwork/issues/28).

## Purpose

Reconcile Groundwork's current public vocabulary with ADR 0003's three physical storage forms before those forms become a broad implementation surface. This report inventories the current API and its actual runtime use, defines the target vocabulary and ownership boundaries, and gives existing consumers a staged migration path.

This is an API-design report. It intentionally changes no runtime implementation.

## Executive findings

- `StorageManifest` and `StorageUnit` remain the correct provider-neutral intent boundary.
- `Portable` and `Optimized` are not coherent storage-form names. All three ratified forms are portable declarations, and optimization is a workload-specific outcome rather than a shape.
- `PhysicalizationPolicy` currently means "derive an eligible sidecar projection" rather than "select a physical storage form". It should be replaced by an explicit physical-storage policy.
- Projection remains useful vocabulary only for derived fields or linked structures. It must not name the complete persistence layout.
- `PhysicalTableDefinition` should be the provider-neutral structural description produced from a storage unit's declared layout, before provider identifier normalization.
- The active `Groundwork.Materialization` model is the canonical schema-preparation pipeline. The duplicate `Groundwork.Core.Materialization` records and the independent imperative migration pipeline create competing public concepts and should converge.
- `PortableDocumentQuery` is the more complete bounded query contract. `DocumentStoreQuery` is a legacy single-equality subset and should become a compatibility adapter, not a second query path.
- `IndexDeclaration.SupportedOperations` and `PortableQueryDeclaration.Operations` duplicate query-capability intent. The legacy equality path reads the former while bounded-query support reads the latter, so one manifest can describe two answers.
- Logical naming, host naming policy, resolved names, and provider identifier normalization need distinct types. Current provider naming helpers collapse those stages.

## Source-verified current surface

### Intent and physicalization declarations

| Current public API | Runtime role verified in source | Assessment |
|---|---|---|
| [`StorageManifest`](../../src/Groundwork/Core/Manifests/StorageManifest.cs) | Versioned aggregate of storage units plus required provider capabilities. It is validated, planned, and passed to store factories. | Keep. This is desired storage intent, not a physical schema or an execution plan. |
| [`StorageUnit`](../../src/Groundwork/Core/Manifests/StorageUnit.cs) | Logical document kind plus lifecycle, identity, tenancy, concurrency, serialization, index, query, and physicalization declarations. Providers also use its identity as the current document kind/collection key. | Keep, but replace its physicalization member with an explicit physical-storage policy in the breaking API. |
| [`PhysicalizationPolicy` / `PhysicalizationKind`](../../src/Groundwork/Core/Manifests/StoragePolicies.cs) | Selects `Portable`, `Optimized`, or `Specialized`. Only `Optimized` affects the generic document runtime, by making otherwise-default eligible indexes physicalized. | Rename and reshape. The values do not correspond to ADR 0003's forms; `Specialized` belongs to a distinct contract family, not a document layout. |
| [`IndexPhysicalizationPolicy`](../../src/Groundwork/Core/Indexing/IndexDeclaration.cs) | Overrides unit policy per index with `Default`, `Portable`, or `Optimized`. | Remove from the logical index declaration. A logical index/query requirement should not choose a sidecar or inline physical placement. |
| [`IndexDeclaration`](../../src/Groundwork/Core/Indexing/IndexDeclaration.cs) | Declares serialized fields, value kind, uniqueness, sortability, missing-value behavior, supported query operations, and physicalization placement. Providers use it for write constraints and both query paths. | Narrow and rename to `LogicalIndexDeclaration`; move supported operations and ordering demand to bounded-query declarations and physical placement/sort direction to `PhysicalTableDefinition`. |
| [`PhysicalizationProjection`](../../src/Groundwork/Core/Physicalization/PhysicalizationProjection.cs) | Derives fields only from single-field indexes with `MissingValueBehavior.Excluded` and equality support. It is used by materialization, relational save/delete/equality query paths, and MongoDB materialization/save/query paths. | Make an internal projected-column planning concern. Its current name overstates the scope. |
| [`PhysicalizedFieldPlan`](../../src/Groundwork/Core/Physicalization/PhysicalizedFieldPlan.cs) | Carries index identity, serialized path, value kind, uniqueness, and sortability into provider naming, materialization, writes, and queries. | Replace with a richer `ProjectedColumnDefinition` inside `PhysicalTableDefinition`; keep query indexes as separate definitions. |

The current relational "optimized" form does not move canonical JSON. [`RelationalDocumentStore`](../../src/Groundwork/Relational/Documents/RelationalDocumentStore.cs) always writes the shared document table, maintains shared index rows, and additionally maintains a per-unit physicalized table. Its legacy equality-query overload can route through that table. MongoDB stores the same derived values in a `physicalized` subdocument and may index them. This is linked projection/index behavior, not ADR 0003's physical entity table.

### Planning and materialization

| Current public API | Runtime role verified in source | Assessment |
|---|---|---|
| [`Groundwork.Materialization.MaterializationPlan`](../../src/Groundwork/Materialization/MaterializationPlan.cs) | Self-contained provider plan used by every provider materializer and document-store factory. | Keep as the one provider-neutral schema-preparation plan. |
| [`MaterializationOperation`](../../src/Groundwork/Materialization/MaterializationOperation.cs) | Typed operations for storage-unit creation, index creation, optimized projection creation, and schema-history recording. | Keep the typed-operation model; rename operations around structures, not "optimized" behavior, and add diff/backfill operations. |
| [`MaterializationPlanner`](../../src/Groundwork/Materialization/MaterializationPlanner.cs) | Validates a manifest and provider reports, derives all current create operations, and emits schema history. It does not yet diff a recorded resolved definition. | Keep and extend to plan from resolved definitions plus history. |
| [`Groundwork.Materialization.SchemaHistoryEntry`](../../src/Groundwork/Materialization/SchemaHistoryEntry.cs) | Carries manifest identity/version, provider, planning time, and operation targets. Provider ledgers currently persist a narrower identity/version/provider record. | Keep the concept, expand its durable shape to prove the resolved definition and names that were applied. |
| [`Groundwork.Core.Materialization`](../../src/Groundwork/Core/Materialization/MaterializationPlan.cs) plan, operation, enum, and schema-history records | No repository runtime usage outside their own declarations. They duplicate the active `Groundwork.Materialization` names with different shapes. | Remove at the breaking boundary. They contradict ADR 0001's ownership decision. |
| [`DocumentPlan`](../../src/Groundwork/Documents/Planning/DocumentPlan.cs) / [`RelationalPlan`](../../src/Groundwork/Relational/Planning/RelationalPlan.cs) | Public projections over the manifest plus the same materialization plan. Repository consumers are planner tests; provider factories execute `MaterializationPlan` directly. | Do not grow these into competing plans. Make them internal diagnostics/read models or replace them with one resolved-storage plan. |

`Materialization` retains its meaning from [`CONTEXT.md`](../../CONTEXT.md): preparing provider storage for a manifest. It is not synonymous with projecting JSON values, and it is not limited to first-time creation.

### Queries

| Current public API | Runtime role verified in source | Assessment |
|---|---|---|
| [`PortableQueryDeclaration`](../../src/Groundwork/Core/Queries/PortableQueryDeclaration.cs) | Declares supported operations and ordering for an index. Paging, disjunction, and count flags are retained as advisory metadata rather than native-support gates. Its operations overlap `IndexDeclaration.SupportedOperations`. | Rename to `BoundedQueryDeclaration` and make it the sole owner of allowed query shapes; "portable" describes the whole contract, not one declaration category. |
| [`DocumentStoreQuery`](../../src/Groundwork/Documents/Store/DocumentStoreQuery.cs) | Single equality by index identity with offset paging. Relational providers may route it to physicalized sidecars; MongoDB chooses content or physicalized paths. | Deprecate as a source-compatible facade over the bounded query. It must not retain a separate execution path. |
| [`PortableDocumentQuery`](../../src/Groundwork/Documents/Store/PortableDocumentQuery.cs) | Closed `AND`-of-`OR` query with equality, membership, contains, ordering, offset paging, total count, and tenant scope. Relational and MongoDB providers execute it server-side. | Evolve into the single `DocumentQuery` contract. Extend only through declared bounded operations. |
| [`ClosedQueryCapabilityModel`](../../src/Groundwork/Core/Queries/ClosedQueryCapabilityModel.cs) and [`ClosedQueryNativeSupport`](../../src/Groundwork/Documents/Store/ClosedQueryNativeSupport.cs) | Evaluate whether a query's declared comparisons and ordering are supported. Their comments still allow caller-side fallback. | Rename around bounded-query support and make production fallback policy a validation decision. |

[`IDocumentStore`](../../src/Groundwork/Documents/Store/IDocumentStore.cs) currently exposes both query models. ADR 0003 requires one query entry model whose provider planner selects shared indexes, a dedicated document table, or physical entity columns without caller-visible branching.

### Naming

| Current public API | Runtime role verified in source | Assessment |
|---|---|---|
| [`PhysicalizationNameEncoder`](../../src/Groundwork/Core/Physicalization/PhysicalizationNameEncoder.cs) | Produces readable, hashed, length-bounded identifiers. | Retain as an internal deterministic encoding utility, not the host policy contract. |
| [`RelationalPhysicalizationNames`](../../src/Groundwork/Relational/Physicalization/RelationalPhysicalizationNames.cs) | Hard-codes physicalized table/column/index prefixes and a 63-character cap. | Replace with provider resolution over logical names. The 63-character limit is PostgreSQL-specific and must not define every relational provider. |
| [`MongoDbGroundworkNames`](../../src/Groundwork/MongoDb/MongoDbGroundworkNames.cs) | Encodes collection and physicalized-field names and owns schema-history naming. | Keep provider mechanics internal; feed it resolved logical names rather than storage-unit identities directly. |

No current host policy can transform all logical database-object names, and no schema-history fingerprint proves the final resolved name. ADR 0003's naming order therefore requires new API rather than a rename of the existing helpers.

### Migrations

[`GroundworkMigration`](../../src/Groundwork/Core/Migrations/GroundworkMigration.cs) is a second, imperative public pipeline. It declares create/drop/index/projection/backfill/transform/provider-SQL operations, while [`GroundworkMigrationRunner`](../../src/Groundwork/Core/Migrations/GroundworkMigrationRunner.cs) delegates to an executor. The only repository executor is SQLite, and its practical escape hatch is provider SQL. By contrast, declarative additive-index and projection backfills already execute through `MaterializationPlanner` and provider materializers under ADR 0002.

The two pipelines overlap in name and operation kinds but do not share a desired-state diff, plan, or durable definition. They should not both grow into general schema evolution systems.

## Canonical vocabulary

The following terms are normative for new work.

| Term | Meaning | Avoid |
|---|---|---|
| **Storage manifest** | A versioned, provider-neutral declaration of desired storage intent owned by a feature or composition. | Schema, migration plan. |
| **Storage unit** | One logical persisted document kind with lifecycle, identity, tenancy, concurrency, serialization, index, query, and physical-storage intent. | Table, collection. |
| **Storage-unit provisioning mode** | A binding declaration of whether a unit is a bounded, statically declared kind (`Declared`) or belongs to a runtime-created/unbounded family (`Dynamic`). Unlike `WorkloadIntent`, this value participates in physical resolution and fingerprints. | Inferring storage form from a diagnostic workload label. |
| **Physical storage form** | One of the three provider-neutral layouts selected for a storage unit. | Portable/optimized mode. |
| **Shared document storage** | Canonical envelopes and JSON for multiple units in a provider-level structure, with linked index structures as needed. | Portable table. |
| **Shared storage definition** | A manifest/composition-owned definition of one shared primary document structure. Units reference it by a stable binding identity rather than redefining its name or envelope independently. | A per-unit shared table definition. |
| **Shared storage binding** | A stable reference from a unit's default policy or explicit definition to one manifest/composition-owned shared storage definition. It carries identity, not a competing primary shape. | Repeating the shared table name/envelope on every unit. |
| **Dedicated document table** | One unit-specific envelope-plus-canonical-JSON table or provider-native equivalent. | Optimized table. |
| **Physical entity table** | One unit-specific table containing the envelope, canonical JSON, and declared projected columns. | ORM entity table, columns-only entity. |
| **Linked index table** | A derived structure that stores query keys and a document reference. It is not a fourth storage form. | Physicalization table, optimized projection. |
| **Projected column** | A rebuildable native field derived from a stable serialized JSON path and maintained atomically with canonical JSON. | Authoritative entity property. |
| **Logical index declaration** | A provider-neutral uniqueness, key-shape, and missing-value requirement. It does not promise query operators, ordering, or physical placement. | Physical index, query capability. |
| **Physical index definition** | An index over columns/fields in a resolved physical definition, selected to satisfy logical index and bounded-query requirements. | Logical query contract. |
| **Physical table definition** | The provider-neutral structural description of a storage unit's selected form, logical names, envelope/JSON columns, projected columns, indexes, and evolution metadata. | Provider DDL, materialization plan. |
| **Resolved physical definition** | A physical table definition after defaults, host naming, and explicit per-unit overrides, but before provider normalization. | Raw manifest, provider DDL. |
| **Provider physical definition** | The provider-targeted definition after identifier normalization and collision checks. It preserves the resolved logical names beside final provider identifiers. | Hand-authored provider schema. |
| **Materialization plan** | The self-contained, provider-targeted but semantically provider-neutral work required to make storage match resolved definitions. | Storage manifest, query plan. |
| **Materialization operation** | One typed structural, backfill, validation, history, or authorized cleanup step in a materialization plan. | Hand-authored provider migration. |
| **Schema history** | Durable evidence of which resolved manifest fingerprint, names, definitions, and operations a provider applied. | A list of migration class names only. |
| **Semantic migration** | An explicitly authored, provider-neutral data transformation used only when a manifest diff cannot infer the change. | General schema creation pipeline. |
| **Bounded query declaration** | The operations, ordering, paging, projection, or aggregate shapes a unit promises and providers must validate. | Arbitrary LINQ/IQueryable. |
| **Scale-bearing query demand** | An explicit `BoundedQueryExecutionClass.ScaleBearing` declaration for a bounded query whose referenced stable non-envelope index paths require an indexed native physical plan. It participates in default form resolution and fingerprints; it is never inferred from names, workload labels, or observed cardinality. | A vague performance hint or runtime heuristic. |
| **Document query** | The one runtime request model for a declared bounded document query. | Portable query vs optimized query. |
| **Physical query plan** | A provider-selected execution choice over shared indexes, a dedicated table, or entity columns. It is diagnostic/provider output, not a caller request. | Document query. |
| **Logical name** | A feature-owned default database-object name before host policy. | Quoted SQL identifier. |
| **Physical object kind** | A provider-neutral naming category: `PrimaryStorage`, `LinkedIndexStorage`, `ProjectedField`, `PhysicalIndex`, or `SchemaHistory`. Providers map these categories to engine objects such as tables, collections, columns, fields, and indexes. | Raw provider object-type strings in host policy. |
| **Resolved physical name** | The logical name after host policy and explicit per-unit override. | Provider-normalized identifier. |
| **Provider identifier** | The final quoted/cased/length-bounded engine identifier after provider normalization and collision checking. | Business naming policy. |

`Portable` remains valid as an adjective for provider-neutral definitions and bounded operations. It must not label only the shared storage form. `Optimized` may describe measured outcomes, but it must not be an enum value or contract promise without a workload and evidence.

## Proposed public model

Names below are the recommended target. Exact record-vs-class construction can be settled in the implementation spec without changing the vocabulary.

### Storage declarations

- Keep `StorageManifest` and `StorageUnit`. Add a binding `StorageUnitProvisioningMode` with `Declared` and `Dynamic`; do not derive this choice from the current diagnostic-only `WorkloadIntent` descriptor.
- Replace `PhysicalizationPolicy` with `PhysicalStoragePolicy`. Each `StorageUnit` owns exactly one policy:
  - `Default(SharedStorageBinding? sharedStorage = null)` asks Groundwork to apply ADR 0003's deterministic provisioning/query-demand rules. `Dynamic` units require the binding; `Declared` units reject it because their default is unit-specific storage.
  - `Explicit(PhysicalTableDefinition)` supplies the provider-neutral form, feature-default logical name, projected columns, and indexes directly.
- Introduce `PhysicalStorageForm` with exactly:
  - `SharedDocuments`
  - `DedicatedDocumentTable`
  - `PhysicalEntityTable`
- Do not add `Specialized` to that enum. Diagnostic streams and other non-document workloads use their own Groundwork contract families.
- Replace `IndexDeclaration` with `LogicalIndexDeclaration`, retaining field/value shape, uniqueness, and missing-value semantics. Ordering demand belongs only to bounded queries.
- Remove `IndexPhysicalizationPolicy` and `SupportedOperations` from the logical index declaration. `BoundedQueryDeclaration` exclusively owns allowed query operations, ordering, paging, projection, and aggregate shapes.
- Add `BoundedQueryExecutionClass` with `Ordinary` and `ScaleBearing`. A scale-bearing declaration identifies its required stable paths through referenced `LogicalIndexDeclaration`s; those paths become projected-column demand for default resolution. This is a binding contract requiring a native indexed server-side plan, not advisory metadata.
- Add `PhysicalTableDefinition` as the portable per-unit structural description. For dedicated/entity forms it owns the primary logical table identity/name, standard envelope and JSON columns, projected-column definitions, physical indexes, schema version, and migration hints. For shared form it instead owns a `SharedStorageBinding` plus only unit-owned linked/index structures; the primary name and common envelope come from a manifest/composition-owned `SharedDocumentStorageDefinition`.
- Projected columns reference stable serialized paths. A `ProjectedColumnDefinition` owns portable type, length, precision, nullability, collation/default metadata, and rebuild semantics.
- `PhysicalIndexDefinition` values reference columns in a `PhysicalTableDefinition` and express storage target, compound order, uniqueness, and sort direction. They are distinct from logical index and bounded-query declarations even when planning derives one from both.

Default resolution remains the ADR 0003 policy, made executable through binding declarations rather than heuristics:

1. `Dynamic` units resolve to shared documents and must identify a declared shared-storage binding through their `Default` policy (or through an explicit shared-form definition).
2. `Declared` units with no scale-bearing projected-field demand resolve to dedicated document tables.
3. `Declared` units whose `BoundedQueryDeclaration`s explicitly mark stable non-envelope fields as scale-bearing resolve to physical entity tables.

The provisioning mode, scale-bearing declarations, shared binding, and synthesized definition all participate in the fingerprint. Ambiguous or contradictory declarations fail validation and require an explicit definition; `WorkloadIntent.Descriptor` never selects a form. An explicit definition wins form defaulting but still passes through host naming, per-unit host override, provider normalization, capability validation, and collision checks.

### Resolution and naming

The resolution pipeline is:

`StorageManifest -> storage defaults -> PhysicalTableDefinition -> IPhysicalNamePolicy -> explicit per-unit override -> ResolvedPhysicalTableDefinition -> provider normalization -> ProviderPhysicalTableDefinition + fingerprint`

- `IPhysicalNamePolicy` is provider-agnostic and receives storage-unit identity, a `PhysicalObjectKind`, and the feature-default logical name. A function adapter may support simple prefix/suffix cases.
- An explicit per-unit override wins over the general host policy.
- A provider-owned identifier policy handles only quoting, casing behavior, reserved words, maximum lengths, deterministic truncation, and collision detection.
- The host-resolved model should be explicit—recommended name `ResolvedPhysicalTableDefinition`—and remain provider-neutral.
- Provider normalization produces a `ProviderPhysicalTableDefinition` (or equivalent) that carries both resolved logical names and final identifiers so materialization and runtime query planning consume the same names and shapes.
- Both resolved logical names and final provider identifiers participate in the plan fingerprint and schema history. Two logical names that normalize to the same provider identifier fail validation.

The current `PhysicalizationNameEncoder`, relational naming helper, and MongoDB naming helper become provider-internal implementation details behind this resolution surface.

### Materialization and schema history

The relationship among the main concepts is:

1. `StorageManifest` owns one or more logical `StorageUnit` declarations.
2. The manifest/composition owns each `SharedDocumentStorageDefinition`; units using shared form reference one by stable binding identity. Resolution groups those units, resolves the shared primary name/envelope once, and rejects conflicting definitions.
3. Each unit's `PhysicalStoragePolicy` either supplies a `PhysicalTableDefinition` or asks the resolver to synthesize one from its binding provisioning mode, optional default-policy shared binding, and declared query/index requirements.
4. Host naming produces a resolved physical definition; provider normalization produces a provider physical definition and deterministic fingerprint.
5. `MaterializationPlanner` compares the provider physical definition with durable `SchemaHistory` and emits one `MaterializationPlan`.
6. The plan contains typed `MaterializationOperation` values for structures, indexes, backfills, semantic transforms, validation, authorized destructive work, and history recording.
7. A provider materializer executes that plan under its migration lock and records the resolved fingerprint, names, definition version, and applied operations.
8. Runtime storage and query planners consume the same resolved definitions; they do not re-derive different names or shapes from raw `StorageUnit` values.

`Groundwork.Materialization` remains the owner of the canonical plan, operation, capability, planner, and schema-history contracts. Remove the unused `Groundwork.Core.Materialization` duplicates rather than maintaining converters indefinitely.

The imperative migration surface narrows to explicit `SemanticMigration` declarations and optional provider extensions. Structural create/add/drop/backfill operations are generated into the materialization plan from manifest/history diffs. The CLI may call this workflow "migrations" for operator familiarity, but there is only one plan and executor lifecycle underneath.

The existing public migration family has this complete target disposition:

| Current migration API | Target disposition |
|---|---|
| `IGroundworkMigration` / `GroundworkMigration` | Replace with a narrow `ISemanticMigration`/`SemanticMigration` declaration for non-inferable provider-neutral data transforms only. |
| `GroundworkMigrationOperation` / `GroundworkMigrationOperationKind` | Generate structural operations from definition/history diffs; adapt semantic transforms into typed `MaterializationOperation`s. Raw provider SQL is an explicitly non-portable provider extension, never the general feature migration contract. |
| `GroundworkMigrationRunner` | Replace with the single materialization application coordinator used by startup and the CLI; it consumes resolved plans rather than owning a second ordering/execution lifecycle. |
| `IGroundworkMigrationExecutor` | Remove as a parallel provider executor. Provider materializers execute the unified plan under the provider migration lock. |
| `GroundworkMigrationExecutionOptions` | Replace `DryRun`/`AllowDestructive` with the ratified `ValidateOnly`, `ApplySafe`, and explicitly authorized `ApplyAuthorized` modes. |
| `GroundworkMigrationResult` | Replace with one materialization application result carrying the plan fingerprint, status, applied/skipped operations, and deterministic diagnostics. |
| `GroundworkMigrationRecord` and the migration ledger | Fold semantic-migration identity/version evidence into expanded schema history; do not retain a second structural ledger. |

### Bounded queries and physical plans

- Rename `PortableQueryDeclaration` to `BoundedQueryDeclaration` when the breaking query revision lands; it becomes the single source for allowed query operations.
- Rename/narrow `IndexDeclaration` to `LogicalIndexDeclaration`; query support is no longer declared there.
- Evolve `PortableDocumentQuery` into `DocumentQuery`, retaining its closed expression model rather than exposing `IQueryable`.
- Add required bounded operations only when declarations, provider capability handlers, and conformance tests land together.
- Make `DocumentStoreQuery` an obsolete equality convenience that constructs `DocumentQuery`; providers must not implement a separate overload path.
- Rename `ClosedQueryCapabilityModel` and related support types around `BoundedQuery` so "closed" and "portable" do not become competing families.
- Introduce a provider diagnostic `PhysicalQueryPlan` (or equivalent) that identifies the selected table/index/column path. Callers never submit one.
- Validation rejects required production queries that lack a server-side plan. In-memory evaluation is an explicitly bounded development/test policy, not implied by the query contract.

This yields one direction of dependency:

`BoundedQueryDeclaration + DocumentQuery + ProviderPhysicalTableDefinition + executable provider capabilities -> PhysicalQueryPlan`

## Compatibility and migration strategy

Groundwork is currently versioned `0.0.1`, so this is the right time for a deliberate pre-1.0 cleanup. Source compatibility can be staged; binary compatibility cannot be promised for renamed positional records, constructors, namespaces, or enum values.

### Stage 1: additive bridge

- Introduce the new storage-form, table-definition, naming, and bounded-query types alongside current types.
- Preserve the current `StorageUnit` constructor for one transition release. Add an adapter/builder that produces the new definition rather than adding more positional parameters.
- Map legacy `PhysicalizationPolicy.Portable` to shared document storage.
- Map legacy `PhysicalizationPolicy.Optimized` to shared document storage with the existing linked projection/index structure—not to a physical entity table. Mapping it to the entity form would silently change storage semantics.
- Require an explicit adapter or diagnostic for `Specialized`; it has no honest three-form mapping.
- Map `IndexPhysicalizationPolicy.Optimized` to legacy linked projected-field placement and `Portable` to the shared index path during the bridge.
- Convert each legacy `PortableQueryDeclaration` into an authoritative bounded-query declaration. Preserve the current valid-subset rule: error only when a query requests an operation outside its index's legacy `SupportedOperations`; extra index operations do not create implicit query capabilities. Convert ordering only from a query's declared sort support after validating the legacy index is sortable, then retire the index-level sortability flag.
- Legacy units have no binding static/dynamic signal. Preserve their explicit `Portable`/`Optimized` mappings during the bridge; require new units using `Default` to provide `StorageUnitProvisioningMode`, a shared binding when dynamic, and scale-bearing query declarations rather than inferring from `WorkloadIntent.Descriptor`.
- Implement `DocumentStoreQuery` by converting it to a single equality `DocumentQuery` and delete provider-specific overload logic.
- Mark legacy policies, projection plan types, legacy query names, and duplicate Core materialization types obsolete with replacement guidance.

### Stage 2: one execution model

- Make providers consume resolved physical definitions for materialization, writes, and queries.
- Make the materialization plan the only schema-evolution execution model; adapt semantic migrations into it.
- Reject manifests that specify both legacy physicalization and new physical storage intent unless their mapping is provably identical.
- Publish analyzers or compile-time obsolete diagnostics for legacy members and document mechanical replacements.

### Stage 3: breaking cleanup

- In a clearly versioned pre-1.0 breaking release, remove legacy physicalization enums, public projection planners, `DocumentStoreQuery`, duplicate `Groundwork.Core.Materialization` records, and overlapping structural migration kinds.
- Require consumers to recompile. Renamed public types and changed record constructors are binary breaks and should be called out explicitly in release notes.
- If a type moves assemblies without changing namespace/name, use .NET type forwarding where practical. Type forwarding cannot preserve renamed types or changed constructors, so source adapters remain the primary transition tool.
- Provide a migration table in release notes and a small manifest-upgrade sample. Groundwork should not maintain permanent dual semantics for unreleased/pre-1.0 APIs.

## Stale and historical terminology

The following locations are accurate descriptions of the implemented G7 feature but stale as future architecture guidance:

- [`README.md`](../../README.md) presents `PhysicalizationPolicy.Portable/Optimized`, per-index optimized overrides, and `DocumentStoreQuery` as the primary current API. Keep examples compiling until the bridge lands, then update this as the canonical consumer quickstart.
- [`specs/019-groundwork-physicalization-performance`](../../specs/019-groundwork-physicalization-performance/spec.md) defines "optimized physicalization" as the G7 sidecar/subdocument feature. Preserve it as implemented history and add a pointer to ADR 0003 and this report; do not rewrite its historical acceptance criteria to pretend it implemented entity tables.
- [`specs/021-groundwork-automatic-migrations`](../../specs/021-groundwork-automatic-migrations/spec.md) treats migration operations, manifest changes, optimized projection backfill, and provider SQL as one early design. A successor migration/materialization spec should use the single-plan relationship defined here.
- [`specs/013-groundwork-core-manifest-planner`](../../specs/013-groundwork-core-manifest-planner/spec.md) and its tasks refer to the duplicate `Groundwork.Core.Materialization` location. Preserve completed task history, but remove that surface during the breaking cleanup under ADR 0001.
- [ADR 0002](../adr/0002-additive-index-backfill-in-materializer.md) uses implemented `CreateOptimizedProjection` and `BackfillPhysicalizedAsync` names. Keep those identifiers where they explain current code, and link successor operations rather than rewriting history after renames.
- [`CONTEXT.md`](../../CONTEXT.md) remains the canonical short language reference. When the new APIs land, add physical storage form, physical table definition, resolved physical definition, schema history, semantic migration, bounded query, and physical query plan there. Keep detailed rationale in ADR 0003 and this report.
- The successor [Physical Storage and Operations Readiness](../program-goals/physical-storage-and-operations-readiness.md) goal remains the canonical coordination surface; implementation specs and issues should link back to it rather than duplicating vocabulary.

## Recommended implementation boundaries

1. Specify the new declaration and resolved-definition records, including naming and fingerprint inputs, without changing provider storage.
2. Add legacy-to-new manifest conversion and snapshot tests that prove mappings are explicit and deterministic.
3. Move all providers to resolved names/definitions before adding dedicated or entity tables.
4. Collapse query overloads into the bounded query planner and expose physical-plan diagnostics.
5. Add dedicated document tables, then physical entity tables, through typed materialization operations.
6. Replace structural migration duplication with manifest/history diffs and semantic transforms in the one materialization plan.
7. Remove compatibility APIs at the announced pre-1.0 breaking boundary.

Each slice must keep canonical JSON authoritative, preserve caller-independent physical routing, and prove provider capability claims through the same handlers that execute them.

## Decision summary

| Current concept | Decision |
|---|---|
| `StorageManifest` | Keep as versioned desired storage intent. |
| `StorageUnit` | Keep as a logical document kind; revise its physical-storage member. |
| `WorkloadIntent.Descriptor` | Keep diagnostic-only; never use it to select or fingerprint a physical form. |
| `StorageUnitProvisioningMode` | Add as the binding `Declared`/`Dynamic` input required by default physical resolution. |
| `PhysicalizationPolicy` / `PhysicalizationKind` | Replace with `PhysicalStoragePolicy` / `PhysicalStorageForm`. |
| Portable vs optimized modes | Retire as form names; all declared forms are portable and optimization is measured. |
| `IndexPhysicalizationPolicy` | Remove from logical indexes; placement belongs to resolved physical definitions. |
| `IndexDeclaration` | Narrow/rename to `LogicalIndexDeclaration`; remove duplicated query operations, ordering demand, and physical placement. |
| `PhysicalizationProjection` | Internalize/rename as projected-column planning. |
| `PhysicalizedFieldPlan` | Replace with `ProjectedColumnDefinition`. |
| `PhysicalTableDefinition` | Adopt as the provider-neutral structural definition for the selected form. |
| Shared primary document storage | Define once at manifest/composition scope; shared-form units reference it through `SharedStorageBinding`. |
| Active `Groundwork.Materialization` types | Keep and extend as the only preparation/evolution plan. |
| Duplicate `Groundwork.Core.Materialization` types | Deprecate, then remove. |
| `DocumentPlan` / `RelationalPlan` | Internalize as diagnostics/read models or replace with one resolved-storage plan; do not grow competing execution plans. |
| Existing migration interface/record/runner/executor/options/result/ledger family | Converge into materialization diffs, application modes/results, and schema history; retain explicit semantic migrations only. |
| `PortableQueryDeclaration` | Evolve to `BoundedQueryDeclaration`. |
| Scale-bearing query selection | Declare through `BoundedQueryExecutionClass.ScaleBearing` plus referenced logical-index paths; include it in resolution and fingerprints. |
| `PortableDocumentQuery` | Evolve to the single `DocumentQuery`. |
| `DocumentStoreQuery` | Compatibility wrapper only, then remove. |
| `ClosedQueryCapabilityModel` / `ClosedQueryNativeSupport` | Rename around bounded-query support and make production fallback an explicit validation policy. |
| Provider naming helpers | Internal mechanics behind host policy and provider normalization. |
