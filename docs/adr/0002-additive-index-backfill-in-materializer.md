# Additive-index backfill lives in the declarative materializer

When a storage manifest adds an index to a unit that already holds documents, the pre-existing documents must become visible to the new index. Groundwork performs this backfill in the **declarative materializer** path (`MaterializationPlanner` → `RelationalMaterializerBase`), as part of applying the `CreateIndexOperation`, and inside the same materialization transaction as the rest of the plan.

## Why the materializer, not the migration operation kinds

`GroundworkMigrationOperationKind` declares `BackfillDocuments` and `BackfillOptimizedProjection`, but these belong to the **imperative** migration pipeline (`IGroundworkMigrationExecutor`), which executes hand-authored `ProviderSql`/`ProviderDestructive` operations only; no executor consumes those two kinds today. Wiring additive-index backfill through them would force a second, hand-authored backfill-authoring path that duplicates logic the declarative materializer already owns.

The declarative materializer is the established home for projection maintenance: optimized projections are already backfilled there (`BackfillPhysicalizedAsync`), and per ADR-0001 provider materializers execute a self-contained `MaterializationPlan`. Additive-index backfill shares that lifecycle exactly, so it lives alongside the existing projection backfill. Issue #44 makes this stage explicit after index creation using the Core schema-evolution `BackfillCanonicalJsonOperation`; compatibility materialization and route-native physical schema diffs now share that single semantic operation authority rather than declaring parallel backfill types. The imperative `BackfillDocuments`/`BackfillOptimizedProjection` kinds remain reserved for future hand-authored migrations.

## Relational providers (SQLite, PostgreSQL, SQL Server)

Portable indexes are stored in the shared `groundwork_document_indexes` projection table, which is otherwise only written at document save time. `RelationalMaterializerBase` rebuilds the projection for an added index by deleting the rows for `(document_kind, index_name)` and re-inserting one row per existing document whose content yields a value. Value extraction is shared with the save-time path via `RelationalIndexValues` so runtime and backfill semantics cannot drift. The rebuild is idempotent, so re-materializing a manifest is safe.

## MongoDB

MongoDB needs no backfill code. Its indexes are server-side indexes over the document content, and queries filter the documents directly, so an index added to a populated collection covers pre-existing documents implicitly. This is verified by a test rather than implemented in code.

## Unique indexes over pre-existing duplicates

If a **unique** index is added to a unit whose existing documents already contain duplicate values for the indexed field, the backfill insert violates the unique constraint and the entire materialization transaction rolls back — materialization fails loudly rather than silently dropping documents from the index. This is the intended behavior: the pre-existing data violates the newly declared constraint and must be reconciled before the manifest can be materialized. (MongoDB surfaces the equivalent situation as an index-creation advisory.)
