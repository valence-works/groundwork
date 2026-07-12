# Storage-scope sessions

Tracking: [Groundwork #32](https://github.com/valence-works/groundwork/issues/32).

## Contract

Every ordinary document store is created with exactly one explicit access context:

- `DocumentStoreAccess.Scoped(new StorageScope(value))` serves storage units declared with
  `TenancyPolicy.Scoped` and stamps that value into provider-owned envelope keys.
- `DocumentStoreAccess.Global` serves only units deliberately declared with
  `TenancyPolicy.Global`.

There is no null, ambient, or payload-derived scope. A scoped session cannot access a global unit,
a global session cannot access a scoped unit, and the mismatch is rejected before provider I/O.
Application authorization for acquiring either session remains outside Groundwork.

Scope values are opaque and compared ordinally, including case and Unicode code-unit distinctions.
For portable provider behavior they are limited to 128 UTF-16 code units, cannot have leading or
trailing whitespace, and cannot use Groundwork's reserved `__groundwork_` prefix. The explicit
envelope field name is `storageScope`; a payload field named `tenantId` remains ordinary payload.

Privileged access requires a `PrivilegedStorageAccess` capability and one of three deliberate paths:
targeted scoped access, targeted global access, or cross-scope query access. Cross-scope sessions
cannot perform point writes, loads, or deletes because those operations require an unambiguous
target scope. Privileged acquisition and rejected access flow through `IStorageScopeObserver` using
low-cardinality event shapes that contain no scope value. The same paths emit
`Groundwork.Documents.StorageScope` activities and counters; metric tags contain only access kind,
operation, required policy, and rejection reason.

## Provider keys

Relational providers persist `storage_scope` in the document envelope. Document primary keys,
dependent foreign keys, portable-index keys, unique indexes, linked projection keys, and optimized
projection indexes include it. SQL predicates join and filter dependent rows on the same value.
SQL Server retains exact binary-collated originals and uses persisted fixed-width SHA-256 shadow
columns for native composite keys, so maximum legal values cannot exceed its 900/1700-byte index
limits. Original-value predicates prevent a digest collision from aliasing identities.

MongoDB uses a composite `_id` containing scope and logical id, also persists `storage_scope`, and
prefixes declared native indexes with `storage_scope`. Missing-value indexes use a partial filter so
the always-present scope key does not defeat sparse semantics. Materialization creates collections
with the simple binary collation and rejects preexisting collections configured with another
default collation.

The provider-neutral resolved physical definition carries `StorageScopePolicy`; canonical
serialization and fingerprints therefore differ between otherwise identical global and scoped
definitions. Scoped physical indexes include the envelope scope column; synthesized indexes place
it first, including compound and unique indexes.

Scope support is not advertised by a detached provider flag. `DocumentStoreScopeResolver` is the
shared executable policy handler, manifest validation rejects policies without that handler, and
each provider conformance suite proves the same bound operations and native keys.

## Failure and unit-of-work semantics

Wrong-scope point reads and deletes return the same not-found outcomes as absent records. A
compare-and-swap update in the wrong scope cannot observe the other scope's version or dependent
rows. Unconditional saves remain upserts within the session's own scope.

An explicit unit of work inherits its store's access context. Groundwork rejects a commit scope that
mixes global and scoped storage-unit policies before opening a provider transaction. Relational and
MongoDB transactional units of work retain the same scope for every enlisted operation.

## Conformance evidence

Provider scenarios cover identical ids and unique values in two scopes, CRUD and OCC
non-disclosure, simple and bounded query/count/any/first isolation, optimized projections, stale
dependent-row protection, rollback, restart, privileged cross-scope queries, and provider-native
key/index inspection for SQLite, SQL Server, PostgreSQL, and MongoDB. MongoDB rollback runs against
a real single-node replica set; standalone behavior remains a loud unsupported-capability result.

## Pre-1.0 source break

This greenfield change intentionally removes optional ambient-tenant delegates, `TenancyPolicy.None`,
`TenancyPolicy.TenantPartition(...)`, payload partition-field planning, and the
`QueryTenantScope.TenantAgnostic` bypass. Provider constructors and factories now require a
`DocumentStoreAccess`. Callers choose `TenancyPolicy.Global` or `TenancyPolicy.Scoped` in the
manifest and acquire the matching store session explicitly. No released-data migration is provided.
