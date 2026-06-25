# Groundwork

Groundwork is a provider-neutral persistence foundation. Modules describe storage intent, providers report what they can serve, and materialization prepares provider storage for those manifests.

## Language

**Provider Capability**:
The provider's runtime ability to serve a storage manifest's semantics, including query, index, concurrency, and workload requirements.
_Avoid_: Materialization capability, schema capability

**Materialization Capability**:
The provider's ability to prepare storage for a manifest, including schema history and supported materialization operations.
_Avoid_: Provider capability, runtime capability

**Materialization Plan**:
A self-contained description of the storage-preparation work needed to make a provider ready for a manifest.
_Avoid_: Storage manifest, provider plan

**Materialization Operation**:
One executable storage-preparation step inside a materialization plan.
_Avoid_: Provider capability, manifest rule
