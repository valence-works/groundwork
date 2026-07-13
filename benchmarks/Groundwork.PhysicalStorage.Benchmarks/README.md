# Groundwork physical-storage benchmarks

This harness measures Groundwork's production document-store path across every supported provider
and each of the three physical storage forms. It is a macrobenchmark and conformance harness: it
materializes real manifests, creates real storage, uses production sessions and bounded-query
translation, and records provider-native query plans. It does not contain an EF Core comparison or
a benchmark-only persistence path.

## Safety gates

Timing starts only after the selected provider and storage form pass both gates:

1. The correctness gate proves storage-scope isolation, optimistic concurrency, unit-of-work
   rollback, bounded query/count agreement, and mixed-direction ordering.
2. The native-plan gate executes the production-rendered scale-bearing query through `EXPLAIN`,
   `STATISTICS XML`, or MongoDB `explain`, and rejects a missing declared index, a full collection or
   sequential scan, or a client-side fallback.

A failed gate fails the run and leaves a failed `manifest.json`. Failed and incomplete runs cannot
be promoted as baselines.

## Fixed profiles

Profiles fix every reproducibility control. Provider, form, and workload options only filter a
profile for diagnosis; they do not mutate its seed or measurement sizes.

| Control | Smoke | Scheduled |
|---|---:|---:|
| Seed | 20260713 | 20260713 |
| Primary dataset | 250 | 10,000 |
| Migration dataset | 100 | 5,000 |
| Warmup iterations | 2 | 5 |
| Measured iterations | 7 | 30 |
| Operations per iteration | 10 | 100 |
| Concurrency | 4 | 16 |
| Default providers | SQLite | All four |
| Storage forms | All three | All three |

Smoke runs are fast engineering signals. They are intentionally underpowered for release decisions
and are never baseline-eligible. Scheduled runs are the comparison and baseline profile.

The `Physical Storage Benchmarks` GitHub workflow runs the SQLite smoke matrix for relevant pull
requests on GitHub-hosted Ubuntu. The complete four-provider matrix runs weekly or on manual
dispatch on a controlled self-hosted runner labeled `self-hosted`, `linux`, `x64`, and
`groundwork-benchmark`, with .NET 10 and Docker available. Scheduled runs upload candidate evidence
even before a baseline exists.
Once an approved immutable baseline archive is available, set the repository variable
`GROUNDWORK_BENCHMARK_BASELINE_URL` to its HTTPS ZIP URL; CI then rejects incompatible evidence and
automatically repeats candidate regressions as confirmation runs.

## Running the harness

Run the default SQLite smoke matrix:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run --profile smoke
```

Run a narrow diagnostic case while retaining scheduled controls:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers postgresql \
  --forms entity \
  --workloads indexed-query,mixed-compound-ordering
```

Run the complete scheduled matrix:

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers all \
  --forms all \
  --workloads all
```

By default, server providers use pinned Testcontainers images:

- SQL Server: `mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04`
- PostgreSQL: `postgres:17.6-alpine3.22`
- MongoDB: `mongo:7.0.24` with a replica set

For controlled infrastructure, set the relevant variables and add `--no-containers`:

```text
GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING
GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING
GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING
```

Connection strings are not written to artifacts. Provider metadata records only the environment
variable name or pinned image, database-reported version, isolation strategy, pooling behavior,
and session lifecycle.

## Workloads

| Workload | Measured operation |
|---|---|
| `cold-point-read` | Point load after client/provider state is cleared once per iteration |
| `warm-point-read` | Repeated point loads without the client reset |
| `indexed-query` | Bounded equality query using the declared physical index |
| `mixed-compound-ordering` | Equality query with descending rank after ascending scope/status keys |
| `insert`, `update`, `delete` | Single-document mutations through the production store |
| `unit-of-work` | Batched writes and one commit |
| `concurrent-create` | Concurrent creates for one identity; exactly one must win |
| `optimistic-concurrency` | Stale writes that must return concurrency conflicts |
| `pagination-and-count` | Page and count operations with agreement asserted |
| `backfill-migration` | Materialization/backfill of a fixed secondary dataset |
| `restart-recovery` | Provider client/factory restart followed by verified reads |
| `storage-growth` | Writes with a fixed 1 KiB payload padding |

`cold-point-read` is client-cold and database-warm. It does not flush the database buffer pool,
operating-system page cache, or disk cache. Results must not be described as cold-disk latency.

## Metrics

Each measured iteration is retained in `raw/measurements.jsonl`. Reports contain:

- nearest-rank p50, p95, and p99 latency per logical operation;
- aggregate throughput and allocated bytes per operation;
- observable round trips per operation;
- storage and index bytes, primary/linked row counts, storage growth, write amplification, and
  physical rows per logical mutation where the workload exposes those quantities;
- provider-specific work counters, migration counts, and native plan evidence.

Round trips prefer provider diagnostic command-start events. If those are unavailable, database
client activities are used as an explicitly marked proxy. A missing signal is `null`, never zero.
Allocation uses process-wide `GC.GetTotalAllocatedBytes`; isolate the benchmark process and avoid
unrelated background work. Storage snapshots and correctness/plan gates run outside timed regions.

## Regression policy

Comparisons use deterministic bootstrap resampling of candidate-to-baseline ratios with a 95%
confidence interval. Lower is better for latency, allocation, and storage amplification; higher is
better for throughput.

| Policy | Minimum samples | Resamples | Latency | Throughput | Allocation | Storage |
|---|---:|---:|---:|---:|---:|---:|
| Smoke | 5 | 1,000 | +100% | -50% | +50% | +50% |
| Scheduled | 20 | 5,000 | +10% | -10% | +10% | +15% |

A regression is reported only when the complete confidence interval lies beyond the budget. A
scheduled regression is a candidate signal, not an immediate release failure. Repeat the same
scheduled run on the same controlled machine and pass `--confirm-regression`; exit code `2` means
the regression reproduced. Exit code `1` means the run or a safety gate failed, and `130` means it
was cancelled.

Scheduled comparisons accept only a complete baseline run directory whose schema, fixed controls,
machine identity/runtime/GC/build-configuration metadata, provider versions, and provider
configuration match the candidate.
The baseline decision report must also be baseline-eligible. Passing a raw JSONL file is supported
only for smoke diagnostics; its report explicitly records that reproducibility provenance is
unavailable.

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --baseline artifacts/physical-storage/v1/<approved-run>

dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --baseline artifacts/physical-storage/v1/<approved-run> \
  --confirm-regression
```

## Artifact contract

Every run uses this versioned layout:

```text
manifest.json
metadata/configuration.json
metadata/machine.json
metadata/providers.json
plans/<provider>/<form>/<workload>.<native-extension>
plans/<provider>/<form>/<workload>.<native-extension>.assertions.json
raw/measurements.jsonl
reports/summary.json
reports/summary.md
reports/regression.json
reports/elsa-migration-decision.json
```

The v1 JSON Schemas live in [`schemas/v1`](schemas/v1). Elsa automation should consume
`reports/elsa-migration-decision.json`; raw measurements remain available for independent analysis.
Do not compare different schema versions without an explicit converter.

### Elsa decision consumption

Consumers should first require a completed `manifest.json` and the exact supported
`schemaVersion`. In `elsa-migration-decision.json`:

- `baselineEligibility` answers only whether the run may be promoted; it is not a performance
  recommendation by itself.
- `cases` contains stable provider/form/workload metrics and the native-plan artifact reference.
- `regressions[].isComparable` must be true before interpreting its metrics.
- `regressionDetected` means at least one confidence interval exceeded policy.
- `confirmationRequired` means a scheduled regression must reproduce in a confirmation run before
  automation treats it as a blocking regression.

An automated Elsa migration gate should reject failed/incomplete runs and non-comparable cases,
require human review of plan changes, and require the confirmation exit code for scheduled
regressions. It should not infer a decision from raw latency alone.

## Baseline promotion

Baseline promotion is deliberately stricter than successful execution. The generated decision
report is eligible only when it comes from:

- the exact scheduled controls and the complete provider/form/workload matrix;
- exactly 30 measured samples per case;
- a known clean Git commit;
- passing correctness gates and native plan evidence for every case.

The committed [`baselines/v1/baseline-index.json`](baselines/v1/baseline-index.json) starts empty.
No performance values are fabricated in source control. Promote a controlled scheduled run by
archiving its immutable artifact directory, reviewing its machine/provider metadata and plan
evidence, and adding a reference to that run to the index in a reviewed change. Never replace an
existing baseline artifact in place.

For defensible comparisons, keep CPU architecture, operating system, .NET runtime, GC mode,
database versions/configuration, container or external-host topology, power policy, and competing
machine load fixed. Record a new baseline when any of those intentionally changes.
