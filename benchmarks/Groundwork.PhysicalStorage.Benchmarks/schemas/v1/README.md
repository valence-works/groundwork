# Benchmark artifact schemas v1

These schemas define the stable interchange surface for Groundwork physical-storage benchmark
automation:

- `run-manifest.schema.json` describes run status and artifact locations.
- `raw-measurement.schema.json` describes one line of `raw/measurements.jsonl`.
- `elsa-migration-decision.schema.json` describes the compact Elsa-facing decision report.

Schema version `groundwork.physical-storage.benchmark/v1` permits additive source-code changes only
when the serialized shape remains compatible. A breaking rename, meaning change, enum removal, or
required-field change requires a new schema directory and an explicit converter. Consumers must
reject unknown major schema versions rather than guessing.
