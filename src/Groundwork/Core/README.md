# Groundwork Core

Groundwork Core defines provider-neutral persistence intent: manifests, storage units, storage intent, logical indexes, bounded queries, physical table definitions, deterministic name resolution, executable storage routes, provider capabilities, materialization plan concepts, schema-history records, migration contracts, and validation diagnostics.

This package is generic infrastructure. It does not reference host-specific packages or provider-specific database libraries.

## Extension Points

- Storage manifests describe durable intent.
- A manifest/composition owns shared document-storage definitions; units reference them by stable binding.
- `PhysicalTableDefinition` describes shared, dedicated-document, or physical-entity structure without provider SDK types or DDL; declared indexes carry explicit primary/linked placement when a dedicated form uses both objects.
- `PhysicalStorageResolver` applies declared defaults, host naming, per-unit overrides, provider normalization, collision validation, and deterministic fingerprints.
- `ExecutableStorageRouteCompiler` freezes primary/linked objects, envelope/relationship/projected fields, keys, maintenance, query paths, capability requirements, resolved names, and fingerprints into one immutable provider input. See [`docs/executable-storage-routes.md`](../../../docs/executable-storage-routes.md).
- `PhysicalSchemaDiffPlanner` compares those routes with typed durable applied state and emits deterministic additive semantic operations; `PhysicalSchemaApplication` enforces the provider/manifest lock and acknowledgement-before-recording contract. See [`docs/physical-schema-diffs.md`](../../../docs/physical-schema-diffs.md).
- Storage intent declares whether a unit is a portable document, benchmark-gated, or provider-specialized.
- Provider capability reports describe what a provider can materialize.
- Validators return structured diagnostics for preview and startup checks.
- Materialization concepts provide the shared language for later provider packages.
- Migration contracts provide an Orchard-style runner/executor boundary without taking a dependency on provider-specific migration frameworks.
