# Relational physical storage runtime

Tracking: [Groundwork #46](https://github.com/valence-works/Groundwork/issues/46).

The relational runtime consumes the provider-neutral contracts described by
[physical schema diffs](physical-schema-diffs.md) and
[bounded physical query plans](bounded-physical-query-plans.md). It does not resolve names, choose
a physical form, or infer maintenance/query paths again after `ExecutableStorageRoute` compilation.
SQLite is the reference implementation. SQL Server and PostgreSQL use the same route-driven store,
query, acknowledgement, CAS, backfill, and compatibility kernel with provider-owned DDL, metadata,
locking, value, and explain adapters. MongoDB is tracked separately by #48.

## Schema application

`SqlitePhysicalSchemaExecutor` implements `IPhysicalSchemaExecutor` for all three forms. It:

- creates the exact compiled primary and optional linked objects;
- stages typed projected columns, rebuilds linked or in-primary values from authoritative canonical
  JSON, and finalizes required nullability before creating physical indexes;
- validates the compiled object/column set;
- records operation acknowledgements in a manifest/provider-scoped durable ledger; and
- persists canonical `PhysicalSchemaAppliedState` with target-fingerprint compare-and-swap.

Every operation call carries its immutable provider/manifest target identity; executor instances do
not keep mutable ambient application identity and may safely interleave distinct target leases.
Existing SQLite objects are accepted only when envelope/relationship columns, primary keys,
projected type/null/default/collation metadata, and physical index ownership, uniqueness, columns,
directions, and collations match the compiled route. `IF NOT EXISTS` is not compatibility evidence.
SQLite column inspection and required-column rebuilds parse top-level items within the table body;
they do not search the complete `CREATE TABLE` text for an identifier. The parser preserves the
actual DDL while recognizing double-quoted, bracketed, backtick, unquoted, and escaped identifiers,
comments, strings, and nested expressions. Required-column rebuilds replace only the parsed table
token and exact projected-column item, copy every physical column, and replay ordinary, compound,
expression, partial, and quoted indexes. A table and one of its projected columns may therefore
intentionally resolve to the same provider name.

The provider/manifest application lease covers history read, diff, operations, validation, and
state recording. A repeated operation returns its durable acknowledgement, and a repeated complete
target returns no changes. Same-version additions remain discoverable because durable semantic
fingerprints, rather than manifest version alone, drive the diff.

## Relational runtime

`RelationalPhysicalDocumentStore` is the provider-reusable CRUD/OCC/unit-of-work engine. A small
`RelationalPhysicalDocumentDialect` owns identifier quoting, parameter limits, paging/substring
syntax, JSON extraction, and provider constraint classification. Save, update, and delete use one
transaction to maintain:

- the envelope and authoritative canonical JSON in the selected primary object;
- optional unit-owned linked relationship/projection rows; or
- in-primary entity projected columns.

Identity always includes the compiled discriminator and storage scope. The same document identity
and unique projected value can therefore exist in independent scopes when the physical unique index
includes scope. Dedicated document storage works with or without a linked object.

Until creation/update timestamps become portable envelope fields, relational providers reserve the
provider column identifiers `created_utc` and `updated_utc`. Route validation rejects envelope or
in-primary projection mappings that collide with either reserved identifier; providers never
silently reinterpret a compiled route mapping.

Canonical JSON is parsed before mutation. One portable typed projection converter feeds live
primary/linked maintenance, physical predicates, and bounded canonical-JSON backfill batches.
Physical query plans always carry the declared logical value kind. A logical index provides a
default kind and heterogeneous compound fields declare an explicit `IndexField.ValueKind` override.
The provider-neutral compatibility matrix rejects projected types that would change those semantics;
a checked converter then binds the compatible native representation so numeric, Boolean, date-time,
GUID, JSON, and binary predicates match live and backfilled values. Text operators for non-text
canonical or projected values fail before a handler can be certified. A failed mutation poisons and
rolls back an explicit unit of work before partial primary/linked state can be committed.

Decimal projections require explicit precision and scale. SQLite supports precision 1–18 and
stores the checked fixed-scale value as an integer; values outside the declared precision/scale fail
before SQL mutation. Numeric conversion parses the original sign, digits, decimal point, and
exponent before creating a CLR numeric value, so underflow, excess precision, and rounded collisions
cannot enter live, default, backfill, or query paths. Portable date-time projections are UTC instants
at .NET tick precision (100ns), require an explicit UTC designator or numeric offset, and reject
fractional seconds beyond seven digits before parsing; SQLite stores their UTC ticks as an integer.
SQLite does not certify canonical-JSON Number or
DateTime query sources because its native JSON numeric conversion and `julianday` would lose those
semantics; declarations requiring them must provide an exact projected route.
Default physical resolution cannot invent Decimal precision/scale from `IndexValueKind.Number`;
numeric scale-bearing storage therefore requires an explicit physical table definition.

The reusable store accepts `RelationalSessionFactory`: autonomous calls own one pooled provider
connection for the duration of that call, and explicit units of work own one connection and
transaction until completion. SQLite's public connection-string facade selects the provider's
serialized session policy and rejects private in-memory databases; the direct-connection constructor
is retained for explicitly owned in-memory/test sessions. SQL Server and PostgreSQL can bind the same
kernel to concurrent per-operation factories in #47 without introducing retained shared connections.

`RelationalPhysicalDocumentQueryHandler` executes `PhysicalQueryPlan` mappings server-side. Linked
plans join to the primary envelope; primary and entity plans read it directly. Scope and
discriminator are injected from the bound store session. Predicate fields, compound predicates,
ordering with the planned identity tie-break, offset paging, count, any, and first are translated
from the exact certified plan. There is no `IQueryable` or client-evaluation path.

`SqlitePhysicalQueryRuntime` compiles the declarations at construction, creates per-source handler
certifications from the exact provider/route/object/index/field mapping, and delegates startup
validation to `PhysicalQueryDocumentStore`. Its current certified SQLite profile deliberately does
not advertise keyset paging or latest-per-key; such declarations fail before traffic.
Literal substring/prefix values escape SQL `LIKE` wildcards through the shared `ContainsPattern`
contract. Requests exceeding SQLite's parameter budget fail before dispatch.

## Conformance surface

The SQLite black-box suite covers all three forms, dedicated storage without a linked table,
CRUD/OCC/unit-of-work atomicity, linked and entity maintenance, scoped same-identity and unique-value
isolation, compound predicates, count/order/page terminals, durable restart state, same-version
additive backfill, and `EXPLAIN QUERY PLAN` evidence that the declared physical index is selected.
These route-driven scenarios are the reusable behavioral baseline for the SQL Server and PostgreSQL
executors; provider tests must run the same assertions with only dialect, connection, and explain
adaptation. `RelationalPhysicalStorageConformance` is linked into both the SQLite and
relational-provider test projects so #47 inherits the forms, scope isolation, UoW, additive
evolution/restart/lock, CRUD/OCC/query, and dedicated-without-linked contract. MongoDB has its
distinct provider contract in #48.

## SQL Server and PostgreSQL parity

`RelationalServerPhysicalSchemaExecutor` keeps server-provider physical schema execution behind one
deep relational interface. Every independent history, operation, and state call owns a pooled
connection. A provider/manifest application lease owns a distinct connection and a SQL Server
session application lock or PostgreSQL advisory lock across history, DDL/backfill, validation, and
state recording. DDL/backfill and the matching semantic operation ledger row commit in one
transaction; the returned acknowledgement is reread from durable storage so retry after response
loss returns the database timestamp.

Both server providers inspect the live catalog rather than treating create-if-absent as compatibility
evidence. Envelope types, nullability, collation, primary-key order, projected type/default/collation,
and index ownership/uniqueness/order/direction must match the compiled route. Same-version semantic
changes remain visible through route fingerprints and additive operations rebuild projected values
from authoritative canonical JSON in bounded batches.

SQL Server retains document kind and id as binary-collated `nvarchar(450)` values and scope as
binary-collated `nvarchar(128)`. Persisted SHA-256 `binary(32)` provider-owned columns form the
nonclustered physical primary key, while every exact lookup and linked join compares both the digest
and retained original. A native key violation is probed by digest and a different retained identity
raises `PhysicalIdentityHashCollisionException` rather than masquerading as optimistic concurrency.
Provider-owned column names use the same deterministic 128-character normalizer as declared names,
and a route whose visible column collides with one is rejected before use. PostgreSQL stores
portable `DateTime` projections as UTC ticks because native timestamps round Groundwork's 100ns
contract to microseconds; SQL Server uses `datetimeoffset(7)`. Both providers restrict portable
`Decimal` projections to precision 1–28 with explicit scale, matching the exact CLR decimal
conversion boundary used by live writes, backfills, defaults, and query parameters.
`SqlServerGroundworkCapabilities.PhysicalNames` enforces SQL Server's 128-character identifier
limit; `PostgreSqlGroundworkCapabilities.PhysicalNames` truncates on a UTF-8 rune boundary within
PostgreSQL's 63-byte limit. Both append a deterministic semantic hash so long logical names do not
silently collide, while the executor quotes every final provider identifier.

`RelationalPhysicalQueryRuntime` derives every advertised source capability from an executable
handler identity. SQL Server and PostgreSQL wrappers certify the exact route/object/index/field
mapping and execute predicates, compound ordering, paging, count, any, and first server-side. Their
container conformance captures the exact count command and parameters emitted by that runtime,
without optimizer hints. SQL Server executes it with `SET STATISTICS XML ON` and inspects only the
resulting actual-plan XML result set; PostgreSQL prefixes the same command with `EXPLAIN (FORMAT
JSON)`. Both assert that the optimizer selected the declared physical index.
