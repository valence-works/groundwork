# Separate runtime provider and materialization capabilities

Groundwork separates runtime provider capability from materialization capability. `ProviderCapabilityReport` describes whether a provider can serve a manifest's runtime semantics; `MaterializationCapabilityReport` describes whether a provider can prepare storage for a manifest.

`Groundwork.Materialization` owns `MaterializationPlan`, typed `MaterializationOperation` records, `MaterializationCapabilityReport`, and `MaterializationPlanner`. `Groundwork.Core` owns runtime provider capability semantics and does not reference materialization, preserving a clean dependency direction even though this requires breaking changes to existing planner and materializer interfaces.

Provider packages expose runtime and materialization capability reports separately. Provider materializers execute a self-contained `MaterializationPlan` directly rather than accepting `StorageManifest` and re-deriving operation details inside each adapter.
