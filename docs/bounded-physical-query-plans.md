# Bounded physical query plans

Tracking: [Groundwork #45](https://github.com/valence-works/Groundwork/issues/45), including the
type-filtered lookup requested by [#24](https://github.com/valence-works/Groundwork/issues/24).

Groundwork has one logical declaration/runtime-query family and one provider-selected diagnostic
plan. Feature code declares a `BoundedQueryDeclaration` and submits a `DocumentQuery`; it never
submits a table, index, provider expression, `IQueryable`, or `PhysicalQueryPlan`.

## Startup compilation

`PhysicalQueryPlanCompiler` combines:

- the immutable `ExecutableStorageRoute` compiled from the provider physical definition;
- the storage unit's logical indexes and bounded query declarations; and
- a provider-owned `PhysicalQueryPlannerCapabilities` profile backed by executable handlers.

The provider profile supplies an ordered source preference, registered handler identity for every
source, and provider-resolved native field identifiers. Each handler additionally supplies immutable
`PhysicalQueryHandlerCertification` values that bind its provider, storage unit, bounded-query and
logical-index identities, logical paths, access kind, physical target, lookup and primary objects,
physical index, provider field identifiers, and executable-route fingerprint.
`PhysicalQueryDocumentStore` verifies those claims against executable handler instances and rejects
wrong-provider, stale-route, unrelated-object/index, or mismatched-field certifications before
returning a traffic-capable store. The compiler selects the first compatible server-side handler and
records one of these access strategies:

- linked index lookup followed by primary-document lookup;
- primary envelope/index access;
- primary canonical-JSON path access;
- in-primary entity projected columns; or
- provider-native document fields.

This ordering lets a document provider prefer native fields while a relational provider prefers
linked, envelope/JSON, or entity-column handlers. Core contains no SQL, BSON, provider SDK types,
native explain model, or client-evaluation plan.

## Closed declaration

A bounded declaration owns:

- equality, inequality, membership, prefix/declared substring, and range operators;
- explicit predicate paths for compound-prefix validation;
- per-path compound sort directions;
- offset or cursor/keyset paging;
- document, count, any, and first result operations;
- optional disjunction and latest-per-key selection; and
- its `Ordinary` or binding `ScaleBearing` execution class.

A logical index supplies one default `IndexValueKind`. `IndexField.ValueKind` may override that
default for a field in a heterogeneous compound index, making differences such as keyword identity
plus date-time ordering explicit rather than inferring semantics from provider storage.

For compatibility, a declaration without explicit predicate fields filters the first path in its
logical index. New compound declarations should always list their predicate fields. An equality
predicate prefix may be followed by a sort suffix; requested directions must match the physical
index either forward or fully reversed. Runtime requests using that suffix must provide exactly one
standalone equality comparison for every skipped prefix field; an absent prefix or an equality inside
a disjunction is rejected before dispatch. Every ordered plan appends the document identity as an
ascending total-order tie-breaker.

## Isolation and failure rules

Every plan contains a mandatory scope field and the scoped/global-sentinel policy compiled from the
storage route. It is not copied from caller payload or exposed as a removable query predicate.
Shared linked lookups use the linked relationship scope and discriminator before primary lookup;
primary routes use the envelope scope and discriminator.

Compilation is atomic. Unsupported operations, terminals, disjunction, compound predicates,
paging, latest selection, field paths, prefixes, directions, or sources return diagnostics and no
plans. Scale-bearing declarations additionally require an indexed physical or provider-native
route. Groundwork never emits an unbounded client fallback.

`PortableQueryOperationCompatibility` is the provider-neutral executable floor beneath provider
capabilities. Equality, inequality, and membership apply to every logical value kind; substring and
prefix operations apply only to string/keyword values; range operations apply to string/keyword,
number, and date-time values. Projected fields are checked against their compiled physical scalar
type as well, so a numeric, Boolean, date-time, GUID, JSON, or binary column cannot acquire text
semantics from a mismatched logical declaration. Incompatible explicit logical/physical pairs fail
storage resolution, and plan compilation repeats the check before certification. Providers may
certify a subset of this matrix but cannot compile or certify a combination outside it.

Plan diagnostics are canonically serialized and fingerprinted with the provider, selected objects,
index/fields, mandatory scope, predicates/operators, ordering/tie-break, paging, result operations,
latest selection, scale class, and executable-route fingerprint.

## Compatibility bridge

`DocumentQuery` is the runtime contract and binds each request to a bounded-query identity.
`PortableDocumentQuery` and `DocumentStoreQuery` carry `GW0004` obsolete guidance.
`PhysicalQueryDocumentStore` is the executable runtime seam: construction verifies registered
handler identities, compiles every declaration, and returns no traffic-capable store when planning
fails. Runtime requests resolve by bounded-query identity and stable predicate/order paths before
dispatch to the selected handler. `DocumentStoreQuery.ToDocumentQuery(queryIdentity, path)` requires
both values explicitly; it never guesses a query identity from an index name.

`LegacyPortableDocumentQueryHandler` is an explicit ordinary-query bridge for the old provider
surface. It certifies only single-field logical indexes, applies a representable planned default
order, and rejects scale-bearing, compound/multi-path, keyset, latest, and operator shapes the legacy
contract cannot express. It never collapses several stable paths into one legacy index identity.
Providers must not add a third query family.

The [relational physical storage runtime](relational-physical-storage-runtime.md) implements the
reusable relational handler and SQLite reference execution for linked+primary, dedicated, and entity
plans. Exact handler certifications are built from compiled plans; predicates, compound filters,
ordering, offset pages, counts, any, and first execute in SQL, and SQLite explain assertions prove
physical-index selection. The SQLite profile does not advertise keyset or latest-per-key execution.
Typed projected predicates bind provider values through the same portable conversion used by live
writes and backfills. Plan fields retain the declared logical semantic kind; a checked conversion
boundary then emits the compatible native representation, including GUID and binary parameters,
without changing comparison semantics. Numeric literals are validated lexically before CLR
conversion, including exponent and fixed-scale representability, and date-time literals reject
sub-100ns fractions before parsing. Literal `LIKE` wildcard input is escaped, and provider
parameter ceilings are enforced before SQL dispatch.
Intrinsic envelope paths reject a conflicting declared logical kind instead of silently switching
between numeric and lexical semantics. SQLite uses exact fixed-scale integer Decimal projections and
UTC-tick DateTime projections; its canonical-JSON source does not certify Number or DateTime plans.
SQL Server and PostgreSQL plus their native explain assertions remain #47. MongoDB remains #48.
#24 is superseded for SQLite only after this execution slice.
