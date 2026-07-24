# Benchmark artifact schemas v1

These schemas define the stable interchange surface for Groundwork physical-storage benchmark
automation:

- `run-manifest.schema.json` describes run status and artifact locations.
- `raw-measurement.schema.json` describes one line of `raw/measurements.jsonl`, including the
  directly timed per-operation latency observations used for percentiles and bootstraps.
- `elsa-migration-evidence.schema.json` describes explicitly insufficient Groundwork-only evidence;
  it is not an Elsa migration decision.
- `worker-invocation.schema.json` describes the immutable parent-to-worker subprocess request.
- `worker-response.schema.json` describes the Git-bound response and artifact digest set.
- `run-group.schema.json` describes the coordinator manifest and integrity index.
- `run-group-regression.schema.json` describes process-cluster comparison results.
- `consumer-evidence.schema.json` describes redacted evidence joinable by workload, provider, form,
  version, fingerprint, data shape, and result digest. It is always non-promotable until the
  external EF oracle is joined.

Batch elapsed time remains available for throughput and steady-state accounting. Latency
percentiles and confidence intervals consume `operationLatencyNanoseconds` only; a normalized batch
mean is not a latency observation.

This software and contract are unreleased. After v1 is released, a breaking rename, meaning change,
enum removal, or required-field change will require a new schema directory and an explicit
converter. Consumers must reject unknown major schema versions rather than guessing.
