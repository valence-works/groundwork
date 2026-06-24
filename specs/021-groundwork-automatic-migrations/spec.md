# Feature Specification: Groundwork Automatic Migrations

**Feature Branch**: `main`

**Created**: 2026-06-24

**Status**: Draft

**Input**: User description: "Create a specification and implementation for automatic migrations when Groundwork manifests, document schemas, or physicalized indexes change. Evaluate an Orchard-style migrations API/runner, FluentMigrator, and alternatives."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Run Ordered Groundwork Migrations (Priority: P1)

A framework or module author can publish ordered Groundwork migrations that are applied once per provider/database and recorded durably.

**Why this priority**: Groundwork currently has idempotent materialization and schema history, but no upgrade runner. Providers need a common lifecycle for safe schema and data changes.

**Independent Test**: Register multiple migrations, run the provider runner twice, and verify each migration is executed once in version order with a durable ledger record.

**Acceptance Scenarios**:

1. **Given** unapplied migrations, **When** the runner executes, **Then** it applies them in ascending version order and records them.
2. **Given** the same migrations have already been applied, **When** the runner executes again, **Then** no migration operations are executed.
3. **Given** dry-run mode, **When** the runner evaluates migrations, **Then** pending migrations are reported without changing storage.

---

### User Story 2 - Safely Migrate Physicalized Indexes (Priority: P1)

A storage unit can add an eligible physicalized index field and the provider can update optimized structures without breaking existing documents.

**Why this priority**: Physicalized indexes are generated provider-owned structures. A manifest change should not require hand-written ALTER/backfill scripts for common additive changes.

**Independent Test**: Materialize an optimized SQLite manifest, save documents, add a new optimized index field, materialize again, and verify the new column exists, existing rows are backfilled, and queries use the optimized path.

**Acceptance Scenarios**:

1. **Given** an optimized relational projection table already exists, **When** a new eligible physicalized field is added, **Then** the materializer adds the missing column.
2. **Given** existing documents, **When** the new physicalized field is added, **Then** projection rows are backfilled from stored content.
3. **Given** a physicalized uniqueness conflict during backfill, **When** materialization runs, **Then** the migration fails clearly instead of silently dropping the constraint.

---

### User Story 3 - Support Semantic Document Schema Migrations (Priority: P2)

A module author can provide document transforms for semantic schema changes that Groundwork cannot infer.

**Why this priority**: Groundwork can detect schema-version drift and run a pipeline, but it cannot safely invent application-specific JSON transforms.

**Independent Test**: Define a migration from schema `1.0.0` to `2.0.0`, run it over stored documents, and verify documents are rewritten with the new schema version and transformed content.

**Acceptance Scenarios**:

1. **Given** documents with an old schema version, **When** a transform migration is registered, **Then** the runner applies the transform and records completion.
2. **Given** a transform throws for a document, **When** the runner executes, **Then** the migration aborts and is not recorded as applied.
3. **Given** no transform exists for a semantic schema change, **When** planning detects it, **Then** Groundwork reports a required manual migration.

---

### User Story 4 - Keep Provider Dependencies Optional (Priority: P3)

Groundwork can integrate with relational migration ecosystems without making them the core abstraction.

**Why this priority**: FluentMigrator and similar tools are useful for relational SQL, but Groundwork must also support MongoDB, manifest diffs, physicalized indexes, and document backfills.

**Independent Test**: Review package references and verify Groundwork.Core has no dependency on FluentMigrator, EF Core, DbUp, or provider-specific migration packages.

**Acceptance Scenarios**:

1. **Given** Groundwork.Core migration contracts, **When** dependencies are inspected, **Then** they remain provider-neutral.
2. **Given** a team already uses FluentMigrator, **When** they want integration, **Then** an adapter package can translate Groundwork migration operations at the edge.

### Edge Cases

- Destructive operations such as dropping old projection structures require explicit `AllowDestructive` execution options.
- A provider that cannot run a migration transactionally must state the weaker boundary in diagnostics.
- Migrations must be idempotent from the runner perspective; re-running after success must skip applied records.
- Failed migrations must not be recorded as applied.
- Generated physical structures should follow expand/backfill/validate/contract: create shadow or missing structures, backfill, validate, switch reads, and retire old structures only when allowed.
- MongoDB index option/key conflicts should become planned drop/rebuild steps when destructive changes are allowed; otherwise they remain blocking diagnostics/advisories.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Groundwork MUST define provider-neutral migration definitions, operations, execution options, execution results, and an executor contract in Core.
- **FR-002**: Groundwork MUST provide a runner that orders migrations, skips applied migrations, supports dry-run mode, blocks destructive operations unless explicitly allowed, and records successful execution.
- **FR-003**: Provider packages MUST own durable migration ledger storage and operation execution for their database family.
- **FR-004**: SQLite MUST provide a durable migration executor backed by a `groundwork_migration_history` table.
- **FR-005**: SQLite migration execution MUST be idempotent at the runner level and transactional for each migration.
- **FR-006**: Core migration contracts MUST NOT depend on FluentMigrator, EF Core, DbUp, MongoDB, or relational provider packages.
- **FR-007**: Physicalized relational projection materialization MUST support additive schema changes for eligible fields without requiring manual DDL.
- **FR-008**: Additive physicalized projection changes MUST backfill existing documents from stored JSON content.
- **FR-009**: Semantic document schema migrations MUST require explicit transform code supplied by the owning module or application.
- **FR-010**: Destructive cleanup of removed indexes, columns, collections, or tables MUST require an explicit destructive migration option.
- **FR-011**: Migration failures MUST surface as errors and MUST NOT create applied ledger records.

### Key Entities

- **Groundwork Migration**: Ordered migration definition with stable identity, version, description, and operations.
- **Migration Operation**: Provider-neutral unit of migration work such as custom provider SQL, document transform, optimized projection backfill, generated structure creation, or destructive cleanup.
- **Migration Runner**: Provider-neutral lifecycle coordinator that plans pending migrations, enforces options, delegates execution, and records success.
- **Migration Executor**: Provider-owned implementation that stores ledger records and executes operations against a concrete database.
- **Migration Ledger**: Durable provider table/collection that records applied migration identity, version, provider, applied UTC, and description.
- **Document Transform**: Application/module-provided semantic rewrite between schema versions.
- **Physicalized Projection Migration**: Generated migration for optimized provider structures derived from manifest/index changes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Core tests prove migrations are ordered, dry-run does not execute, destructive operations are blocked by default, and applied migrations are skipped.
- **SC-002**: SQLite tests prove migrations are recorded durably and not re-executed.
- **SC-003**: SQLite tests prove adding a physicalized index field adds the missing column and backfills existing rows.
- **SC-004**: Groundwork.Core still has no provider-specific or third-party migration framework dependency.

## Recommendation

Groundwork should adopt an Orchard-style migration API and runner concept, but keep the abstraction Groundwork-native and manifest-aware. FluentMigrator should not be a required dependency because it is relational-only and does not model MongoDB collections, document transforms, physicalized projection rebuilds, or provider-neutral manifests. A future optional adapter package can bridge Groundwork migration operations into FluentMigrator for teams that already standardize on it.

The default migration lifecycle should be:

1. Plan pending Groundwork migrations from explicit module migrations and generated manifest-diff operations.
2. Acquire the provider's migration lock where available.
3. Apply each pending migration transactionally where the provider supports it.
4. Backfill and validate generated physicalized structures before routing reads to them.
5. Record the migration only after successful completion.
6. Require explicit destructive approval before dropping old structures.

