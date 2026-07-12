# Groundwork Documents

Groundwork Documents owns the `DocumentQuery` runtime request and compatibility bridge alongside
provider-neutral document/envelope/index plan descriptions. Runtime requests name a declared
bounded query and never select physical storage or carry scope.

`PortableDocumentQuery` and `DocumentStoreQuery` are obsolete bridge types. Concrete providers
register `IPhysicalDocumentQueryHandler` implementations with `PhysicalQueryDocumentStore`, which
compiles before traffic and binds each `DocumentQuery` identity to one plan. Every handler supplies
exact provider/route/object/index/field certifications; a source-wide capability claim alone cannot
authorize traffic. The explicit legacy handler certifies only single-field ordinary shapes the old
provider contract can represent. See
[`docs/bounded-physical-query-plans.md`](../../../docs/bounded-physical-query-plans.md).
