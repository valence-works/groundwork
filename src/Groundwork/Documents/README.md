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
