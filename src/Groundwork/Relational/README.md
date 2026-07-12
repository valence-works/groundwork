# Groundwork Relational

Groundwork Relational converts validated Groundwork manifests into provider-neutral relational plan descriptions and provides the reusable route-driven relational document/query execution engine.

`RelationalPhysicalDocumentStore` atomically maintains the selected primary envelope plus linked or
in-primary projections from `ExecutableStorageRoute`. `RelationalPhysicalDocumentQueryHandler`
executes certified `PhysicalQueryPlan` mappings without `IQueryable` or client fallback. Provider
packages supply the small SQL dialect boundary, a pooled `RelationalSessionFactory`, and their
physical-schema executor; no provider SDK types leak into Core. See
[the relational physical storage runtime](../../../docs/relational-physical-storage-runtime.md).
