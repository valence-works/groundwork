# Executable storage routes

Tracking: [Groundwork #43](https://github.com/valence-works/groundwork/issues/43).

An executable storage route is the immutable provider-neutral mapping between a resolved provider
physical definition and later provider execution. Providers consume it for materialization, CRUD,
projection/index maintenance, and bounded-query planning. They do not select a storage form from a
workload descriptor, recompute names, or infer where projected fields live.

## Compilation boundary

`ExecutableStorageRouteCompiler` accepts one or more `ProviderPhysicalTableDefinition` values. A
successful route carries:

- the shared, dedicated-document, or physical-entity form;
- resolved provider names for primary storage, optional linked index storage, every envelope and
  canonical-JSON field, linked relationship field, projected field, and physical index;
- the document-kind discriminator, scope policy, scope key, primary key, and optional auxiliary
  key;
- stable serialized-path-to-projected-column mappings;
- ordered and compound physical indexes, including their target storage object;
- atomic save, update, and delete maintenance targets;
- primary-identity and physical-index candidate query paths, with scale-bearing query identities;
- executable capability requirements;
- the source definition fingerprint and a canonical route fingerprint.

The route fingerprint is SHA-256 over canonical route serialization and includes the source
definition fingerprint, resolved names, scope/key behavior, mappings, maintenance targets, query
paths, and capability requirements. Declaration and input-list order do not affect it.

## Placement rules

- Shared documents keep the canonical envelope and JSON in manifest-owned primary storage.
  Unit-owned relationship fields, projected fields, and physical indexes use the declared linked
  index storage.
- Dedicated document tables keep envelope indexes in primary storage and may place projected-value
  indexes in linked storage. `PhysicalIndexDefinition.Target` makes that placement explicit so one
  definition can use both objects. `FormDefault` preserves the pre-route convention: linked when a
  linked object is declared, otherwise primary.
- Physical entity tables keep canonical JSON, projected fields, and physical indexes in primary
  storage.

Shared primary identity includes document kind, storage scope, and document id. Dedicated and
entity primary identity includes storage scope and document id; the document kind remains an
explicit envelope discriminator but is implicit in the unit-specific primary object. Global scope
is explicit and uses the Groundwork global sentinel behavior; it is never an absent/wildcard scope.

Linked storage has its own explicit relationship fields. Their defaults are `document_id`,
`document_kind`, and `storage_scope`, and hosts may override them before provider normalization.
Those logical names are reserved within the linked object even when host overrides give the fields
different provider identifiers.
Shared auxiliary identity uses all three fields; dedicated auxiliary identity uses storage scope
and document id. Physical indexes targeting linked storage resolve envelope identity roles through
these linked fields instead of reusing primary-envelope identifiers.

Provider collision scopes follow physical placement. Envelope and in-primary entity projected
fields share the primary column namespace. Linked relationship and linked projected fields share a
separate linked column namespace, so the same identifier may be used once in each physical object
without a false collision.

## Failure contract

Compilation is atomic. Any error returns no routes. Diagnostics reject stale definition
fingerprints, missing or inconsistent resolved/provider names, unmapped columns or serialized
paths, unexpected names, provider identifier collisions, duplicate storage-unit routes, linked-form
contradictions, invalid linked relationship fields, and scale-bearing demand without a matching
physical index route.

Legacy portable and optimized declarations first pass through `LegacyPhysicalStorageBridge`.
Portable behavior remains shared-document storage, while optimized behavior remains shared storage
plus linked projection/index maintenance.

## Deliberate exclusions

This Core contract contains no provider SDK types and performs no DDL, schema-history I/O, document
I/O, provider capability probing, or query translation. Those layers consume the compiled route in
later work units.
