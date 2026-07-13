# Benchmark artifact schemas v1

These schemas define the stable interchange surface for Groundwork physical-storage benchmark
automation:

- `run-manifest.schema.json` describes run status and artifact locations.
- `raw-measurement.schema.json` describes one line of `raw/measurements.jsonl`.
- `elsa-migration-evidence.schema.json` describes explicitly insufficient Groundwork-only evidence;
  it is not an Elsa migration decision.

This software and contract are unreleased. After v1 is released, a breaking rename, meaning change,
enum removal, or required-field change will require a new schema directory and an explicit
converter. Consumers must reject unknown major schema versions rather than guessing.
