# Physical schema diffs and durable applied state

Tracking: [Groundwork #44](https://github.com/valence-works/groundwork/issues/44).

`Groundwork.Core.SchemaEvolution` turns a `PhysicalSchemaTarget`—manifest/provider identity plus
compiled `ExecutableStorageRoute` values—into a deterministic additive diff against durable
`PhysicalSchemaAppliedState`. It is provider-neutral: it contains no SQL, provider SDK types, CLI
behavior, destructive authorization, or semantic data transforms.

## Semantic operation pipeline

The planner emits stable, content-addressed operations for:

- primary, linked, and physical-entity storage creation;
- projected-column addition;
- physical-index creation;
- canonical-JSON backfill for rebuildable projections and linked physical indexes;
- target validation; and
- applied-state recording.

Operation identities and fingerprints depend only on semantic payload. Unchanged structures keep
the same identity across declaration ordering, process restart, and manifest-version changes. A new
column or index is therefore pending even when the manifest version did not change. An identical
target fingerprint produces no operations. Mutating or removing an already-applied semantic slot
is rejected as a non-additive conflict in this slice; destructive authorization and semantic
transforms remain later work.

Every route-native column, index, backfill, and validation operation carries its immutable owning
executable route (or complete ordered route set for target validation), including resolved primary
and linked object names, envelope/relationship keys, and provider identifiers. A stateless provider
translator can therefore execute same-version additive work after restart without relying on an
earlier create operation or an in-memory route cache. A projected field declared
`SemanticMigrationRequired` is rejected as `GW-SCHEMA-005`; this additive pipeline never substitutes
canonical-JSON extraction for an explicitly required semantic transform.

The compatibility `Groundwork.Materialization` planner now schedules the same Core
`BackfillCanonicalJsonOperation` used by route-native diffs after `CreateIndexOperation`; there is
no second compatibility backfill operation type. Relational providers
execute the existing rebuild at that stage; MongoDB acknowledges it as a native-index no-op because
new native indexes cover existing documents. This removes the hidden rebuild inside index creation
and preserves one create-then-backfill semantic sequence while route-native provider execution is
implemented by issues #46–#48.

## Durable state

`PhysicalSchemaAppliedState` records:

- manifest identity and version;
- provider name and version;
- the aggregate target fingerprint;
- per-route definition and route fingerprints;
- every resolved primary/linked object, envelope/relationship/projected field, and index name;
- the full desired semantic-operation snapshot and the operations acknowledged for the application;
- planned/applied timestamps; and
- canonical route and applied-snapshot JSON.

Canonical route, full-snapshot, target, and operation payload fingerprints are recomputed when
history is read. Canonical formatting alone is not trusted: inconsistent identities, slots,
resolved snapshots, payloads, or fingerprints reject the history instead of suppressing work.
Operation slots are independently rederived from their typed semantic parts rather than trusted
from the persisted slot or its payload prefix. Even when the aggregate target fingerprint is
unchanged, the planner reconciles the complete desired semantic-operation set before returning a
no-change result, so incomplete operation evidence is retried instead of silently accepted.

`PhysicalSchemaAppliedStateSerializer` is the canonical restart representation. Providers persist
that exact representation in their schema-history mechanism and return the reconstructed state
through `IPhysicalSchemaExecutor.ReadHistoryAsync`.

## Application and failure contract

`PhysicalSchemaApplication` always acquires the provider-name/manifest exclusion lease before reading
history, planning, applying, validating, or recording. Manifest version is intentionally absent
from the lock key so same-version changes and version bumps cannot race. Provider package version is
also excluded: rolling provider upgrades operate on the same physical schema and must share the lock;
the version remains durable evidence and a version change produces validation/recording work.

An executor must apply `(operation identity, fingerprint)` idempotently. The same identity with a
different fingerprint is a conflict. An acknowledgement means the operation is durably observable;
if the acknowledgement is lost, retry reconciles the durable operation and returns the same
identity/fingerprint without applying it twice.

The coordinator records target state only after every non-record operation returns the exact
expected acknowledgement. Failure, cancellation, partial execution, or a mismatched
acknowledgement cannot record unapplied target state. State recording is compare-and-swap against
the previously read target fingerprint and must itself be idempotent: if its acknowledgement is
lost after the durable write, restart reads the identical target and returns no changes.

## Greenfield legacy-history policy

Groundwork is unreleased and this work is greenfield. Legacy schema-history rows without a typed
applied snapshot are rejected deterministically (`GW-SCHEMA-001`); Groundwork does not guess,
adopt, or migrate them. Operators must remove such rows before applying the new target. There is no
compatibility-upgrade policy in #44.

Provider DDL translation and durable provider ledgers are implemented with the physical-form
runtimes in #46–#48. The migration CLI is #49.
