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

Provider runtimes also expose `IPhysicalDocumentQueryExplainer` for diagnostic explanation of the
same bounded `DocumentQuery`. It dispatches by `ResultOperation` and returns the compiled plan plus
ordered provider-native command plans. Explanation may execute bounded reads, native output is not
sanitized, and its pseudonymous invocation fingerprint is not a secrecy boundary. See
[`Runtime plan explanation`](../../../docs/bounded-physical-query-plans.md#runtime-plan-explanation).

Named update/delete lifecycle work uses `BoundedMutationDeclaration` and
`PhysicalMutationDocumentStore`. Mutation declarations reuse a scale-bearing bounded query as their
closed selector and fix either a field transition or deletion at manifest construction time. See
[`docs/bounded-document-mutations.md`](../../../docs/bounded-document-mutations.md).

## Versioned canonical JSON

`VersionedJsonDocumentCodec` is the guarded typed path for canonical JSON whose shape evolves. A
caller declares a `DocumentSchemaVersionPolicy` per document kind, contributes contiguous
`IDocumentJsonUpcaster` steps from the minimum readable version to the current version, and supplies
a `DocumentSchemaVersionFormat` that owns its persisted stamp syntax and any legacy aliases.
Construction validates the complete policy, upcaster, and stamp-format contract before traffic.

The codec always writes the current stamp. On reads it parses and bounds-checks the envelope's
`SchemaVersion` before parsing `ContentJson`; malformed, unsupported older, and future stamps raise a
structured `DocumentSchemaVersionException`. Supported older JSON objects are upcasted one integer
step at a time before typed deserialization. Groundwork deliberately does not assign meaning to
application-specific stamps or choose clean-break boundaries for callers.

`JsonDocumentStoreExtensions` remains the raw, explicitly version-blind compatibility surface.
Durable typed stores should use `VersionedJsonDocumentCodec.CreateSaveRequest` and
`VersionedJsonDocumentCodec.Deserialize` instead.

## Cursor continuations

A bounded query declared with `QueryPagingSupport.Cursor` returns an opaque
`DocumentQueryResult.NextContinuation` when another page exists. The token is bound to the compiled
provider plan, route version, inherited storage scope, predicate shape and values, and effective
compound order. Page size is deliberately not bound, so callers may change `Take` between pages.
Malformed, checksum-invalid, stale-plan, cross-query, and cross-scope tokens raise
`InvalidDocumentQueryContinuationException` before provider traffic.

Cursor indexes include Groundwork's fixed-width document-identity lookup key as the final unique
tie-break. Providers fetch one sentinel row beyond `Take`, return only the requested page, and encode
the last returned physical order tuple. Tokens contain provider paging state and are neither
authorization capabilities nor confidentiality boundaries; applications must not inspect or
rewrite them.

Document cursors use **live-view keyset semantics**. A provider may keep count, page, and hydration
under one request-local snapshot, but that is not part of the portable cursor guarantee; otherwise
concurrent writes between those statements can make `TotalCount` and returned documents describe
adjacent live views. Across page requests, inserts or updates that land after the boundary may
appear, while those at or before it do not. Deletes disappear. Moving a returned document's sort key
after the boundary can repeat it, while moving an unseen document before the boundary can omit it.
`TotalCount` is recalculated from the base predicate for every page and can change. Exact snapshot
traversal requires a separate durable high-water model, such as the one owned by Diagnostic Records.

For scoped storage units, cursor traversal must use one inherited scope. Privileged across-scope
cursor traversal is rejected because the scope-prefixed physical index cannot certify the declared
order across all scopes.
