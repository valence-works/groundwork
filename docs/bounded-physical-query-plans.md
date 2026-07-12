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

Provider SQL/BSON generation, native explain assertions, and physical-form runtime execution are
subsequent provider work units. #24 is covered at the planning/conformance level for shared,
dedicated, and entity forms; it is superseded only after those provider execution paths are proven.
