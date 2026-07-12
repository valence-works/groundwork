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

**Diagnostic Record Store**:
A specialized, provider-neutral append/query/inspection/retention contract for immutable,
time-ordered, tenant-scoped diagnostic streams. It is separate from ordinary document storage and
from destructive queue/outbox semantics.
_Avoid_: Document physical-storage form, event store, outbox, arbitrary query engine

**Diagnostic Cursor**:
An opaque, provider-assigned monotonic position within one tenant, storage scope, and diagnostic
stream. It is the total-order tie-breaker and survives record trim through stream metadata.
_Avoid_: Application sequence, occurrence timestamp, cross-stream global sequence

**Diagnostic Continuation**:
A query-shape-bound keyset value carrying the first page's committed cursor high-water and the last
ordered key/cursor. It provides a stable traversal that excludes later and backdated appends.
_Avoid_: Offset, live-view page token
