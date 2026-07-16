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

Every compiled plan also owns one document-identity binding for its selected primary, linked, or
native source. The binding carries the original, comparison-key, and lookup-key fields plus the
versioned projection algorithms. Equality, membership, and inequality bind lookup plus full
comparison evidence; prefix and range operations bind the comparison key; identity ordering and
the implicit identity tie-break use the comparison key. Identity substring matching is rejected
during plan compilation. Provider handlers consume these fields and projected values and do not
reapply the manifest's case policy.

An explicit physical index certifies the evidence shape that execution actually consumes. Exact
identity predicates require lookup-key-leading comparison-key evidence, while prefix and range
predicates require comparison-key evidence only. An index over the retained original identity does
not certify either projected shape. A scale-bearing declaration that mixes exact and ordered
identity operations is rejected with an explicit unsupported-shape diagnostic; this work does not
choose an automatic synthesized index order for that mixed demand, and automatic index synthesis
otherwise remains unchanged.

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

## Runtime plan explanation

`IPhysicalDocumentQueryExplainer.ExplainAsync` accepts the same `DocumentQuery` used for execution
and dispatches through its `ResultOperation` (`Documents`, `Count`, `First`, or `Any`). Its result
contains the compiled `PhysicalQueryPlan`, a runtime-invocation fingerprint, and the ordered
`Commands` planned for that operation. Commands have stable stage identities such as count, page,
first, any, linked-identity collision check, and primary hydration; shape-conditional stages are
omitted when they are not needed, while data-dependent early exits may still stop later work.

Each command carries a provider-native plan and format. Current formats are `sqlite-query-plan`,
`sqlserver-statistics-xml`, `postgresql-json`, and `mongodb-json`. Explanation is a diagnostic
operation, not a dry-run contract: SQL Server executes the exact parameterized read under runtime
statistics collection, and MongoDB may execute bounded selector reads to explain the exact linked
primary hydration. It can therefore consume database resources and observe live data.

Native plans are provider output and are returned unsanitized; treat them as sensitive. The
runtime-invocation fingerprint excludes raw query values, but it is only a pseudonymous correlation
identifier. Low-entropy inputs may still be guessable, so the fingerprint is not a secrecy boundary.

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
SQL Server, PostgreSQL, SQLite, and MongoDB now expose provider-native bounded-query explanations.
#24 is superseded by the provider implementations in this execution slice.
