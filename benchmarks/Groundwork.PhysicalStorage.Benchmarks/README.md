# Groundwork physical-storage benchmark harness scaffolding

This project is an honest, mergeable harness-scaffolding slice for issue #50. It exercises
Groundwork's production document-store path across SQLite, SQL Server, PostgreSQL, and MongoDB and
the three physical storage forms. It materializes real manifests, creates real storage, uses
production sessions and bounded-query translation, and records provider-native query plans.

It does **not** complete issue #50. The scheduled protocol now carries the ratified 1K/100K/1M
dataset dimension and the accepted one-warm-up/three-measured-process scheduled protocol, but
concrete payload/selectivity vectors remain explicit inputs until they are ratified. The harness contains no EF Core comparison, cannot promote
baselines, and cannot make an Elsa migration go/no-go decision.

## Current correctness and plan gates

Before timing, every selected provider and storage form must prove:

1. storage-scope isolation, optimistic concurrency, unit-of-work rollback, bounded query/count
   agreement, and mixed-direction ordering; and
2. on a separately initialized and identically seeded disposable target, selection of the declared
   index through provider-native `EXPLAIN`, `STATISTICS XML`, or MongoDB `explain`, with full scans
   rejected for every applicable timed selection and count shape.

The backfill workload has an additional post-measurement check. Outside the timed region, it uses
the additive model to run the bounded query and directly queries the newly projected `category`
field. Both counts must match the seeded migration row count.

After the identical deterministic seed, a plan target may add internally consistent plan-only
documents to reach a provider-specific optimizer floor and refresh statistics. SQL Server removes
its larger temporary set after capture. Each plan target is disposable and distinct from the
measured database, so optimizer assistance cannot mutate measured workload data or statistics.

These are harness correctness gates only. Passing them does not make performance evidence complete.

## Matrix and independent-run protocol

The profiles provide repeatable controls. Each provider/form/workload/data-shape/repetition tuple is
serialized as an immutable worker request and measured in a separate process:

| Control | Smoke | Scheduled scaffold |
|---|---:|---:|
| Seed | 20260713 | 20260713 |
| Primary dataset | 250 | 1,000; 100,000; 1,000,000 |
| Payload padding | 0 bytes | 0 bytes (explicit control value) |
| Query selectivity | 5,000 basis points | 5,000 basis points (explicit control value) |
| Untimed warm-up processes | 1 per tuple | 1 per tuple |
| Independent measured processes | 1 | 3 |
| Migration dataset | 100 | 5,000 |
| Warmup iterations | 2 | 5 |
| Minimum measured iterations | 7 | 30 |
| Minimum measured operations | 1 | 100 |
| Minimum steady-state execution | 0 seconds | 30 seconds |
| Operations per measured batch | 10 | 100 |
| Concurrency | 4 | 16 |
| Default providers | SQLite | All four |
| Storage forms | All three | All three |

Use `--payload-padding-bytes`, `--selectivity-bps`, and `--independent-runs` to supply reviewed
overrides without changing code. These values are recorded in worker requests, fingerprints, and
consumer evidence; providing payload/selectivity values does not ratify them or make a run
promotable.

Both profiles always emit `baselineEligibility.eligible: false`. Diagnostics explain that issue #50
still requires controlled execution of the complete reviewed matrix, exact-HEAD live evidence from
all four providers, and the Elsa-owned EF Core oracle.

The GitHub workflow is named `Physical Storage Benchmark Evidence (Scaffolding)`. Pull requests run
SQLite smoke evidence. Weekly/manual jobs run the four-provider scheduled scaffold on a controlled
self-hosted runner. Both jobs upload non-promotable evidence and do not perform baseline download,
candidate promotion, confirmation, or migration-decision gating.

## Running the harness

Run SQLite smoke evidence:

```bash
dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run --profile smoke
```

Run a narrow scheduled-control diagnostic:

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers postgresql \
  --forms entity \
  --workloads indexed-query,mixed-compound-ordering \
  --payload-padding-bytes 0,1024 \
  --selectivity-bps 1000,5000 \
  --independent-runs 3
```

Run all cases represented by the scheduled scaffold:

```bash
dotnet run -c Release --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run \
  --profile scheduled \
  --providers all \
  --forms all \
  --workloads all
```

Server providers use pinned Testcontainers images by default:

- SQL Server: `mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04`
- PostgreSQL: `postgres:17.6-alpine3.22`
- MongoDB: `mongo:7.0.24` with a replica set

For controlled infrastructure, set the relevant variable and pass `--no-containers`:

```text
GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING
GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING
GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING
```

Connection strings are not written to artifacts. Metadata records the source description,
database-reported version, isolation strategy, pooling behavior, and session lifecycle.

## Workloads and precise semantics

| Workload | Measured batch |
|---|---|
| `client-reset-point-read-batch` | Clear provider/client pools once, reopen stores, then perform the point-read batch |
| `reused-client-point-read-batch` | Perform a point-read batch through already-open client state |
| `indexed-query` | Repeat the bounded equality query using the declared physical index |
| `mixed-compound-ordering` | Repeat equality queries with descending rank after ascending scope/status keys |
| `insert`, `update`, `delete` | Repeat single-document mutations through the production store |
| `unit-of-work` | Perform batched writes and one commit |
| `concurrent-create` | Concurrent creates for one identity; exactly one must win |
| `optimistic-concurrency` | Stale writes that must return concurrency conflicts |
| `pagination-and-count` | Page and count operations with agreement asserted |
| `backfill-migration` | Time materialization/backfill, then validate projection/query correctness outside timing |
| `client-restart-validation` | Dispose/clear client-side state, recreate it, and verify durable reads |
| `storage-growth` | Writes with fixed 1 KiB payload padding |

`client-reset-point-read-batch` resets client/provider state once before the batch. It does not flush
the database buffer pool, operating-system page cache, or disk cache, and it is not cold-disk or
individual-cold-read latency.

`client-restart-validation` is limited to client/factory/pool restart. It is not process-crash,
database-crash, power-loss, or disaster-recovery evidence.

## Metric semantics

Each raw sample is one measured target invocation and retains the invocation's aggregate elapsed
time for throughput and steady-state accounting. It also carries `operationLatencyNanoseconds`: one
positive, directly timed observation for every operation reported by the target. Summary
`operationLatencyP50Nanoseconds`, p95, and p99 values and latency bootstraps flatten those raw
observations; they never divide an invocation duration by its operation count.

An operation is the smallest semantically complete unit that the workload promises:

- point-read batch: the complete reused-client or reset-client batch (including reset when selected);
- indexed/mixed query, insert, update, delete, stale write, and storage-growth: one store call;
- unit of work: one begin/save-batch/commit transaction;
- concurrent create: one competing create attempt;
- pagination and count: one page query or one count query;
- backfill: one complete materialization/backfill application;
- client restart validation: one client/factory/pool restart plus its durable-read validation batch.

The scheduled process therefore continues whole invocations until it has at least 100 of these raw
operation observations and at least 30 seconds of measured target execution. Reports also contain
aggregate throughput, allocation per operation, observable round trips, net storage growth per
logical payload byte, net physical-row growth per logical mutation, provider work signals, and
native-plan evidence where observable. These are net cardinality/storage ratios, not database
write-amplification measurements. A missing round-trip signal is `null`, never zero.

Consumer evidence binds this behavior as measurement protocol
`direct-operation-latency/v1`; the protocol participates in each workload fingerprint so evidence
produced by the former batch-mean implementation cannot compare as the same workload evidence.

Regression comparisons remain available as diagnostic scaffolding. Current scheduled evidence is
explicitly incompatible with gating because its evidence readiness is insufficient and its baseline
eligibility is false. The committed baseline registry is empty and disabled.

## Artifact contract

```text
run-group.json
protocol/requests/<ordinal>.json
protocol/responses/<ordinal>.json
runs/<ordinal>/manifest.json
runs/<ordinal>/metadata/configuration.json
runs/<ordinal>/metadata/machine.json
runs/<ordinal>/metadata/providers.json
runs/<ordinal>/plans/<provider>/<form>/<workload>-<selection|count>.<native-extension>
runs/<ordinal>/raw/measurements.jsonl
runs/<ordinal>/reports/summary.json
runs/<ordinal>/reports/summary.md
runs/<ordinal>/reports/regression.json
runs/<ordinal>/reports/elsa-migration-evidence.json
runs/<ordinal>/reports/consumer-evidence.json
```

The v1 JSON Schemas live in [`schemas/v1`](schemas/v1). The evidence report deliberately exposes:

- `readiness: insufficient`;
- `elsaEfOracleRequired: true`;
- `baselineEligibility.eligible: false` with concrete diagnostics;
- Groundwork case evidence and diagnostic regression signals; and
- `remainingAcceptanceWork` for the later Elsa-owned evidence join.

No artifact in this slice is a migration decision or baseline-promotion authorization.

Measured workers do not repeat warm-up iterations internally. The preceding warm-up worker executes
the configured untimed warm-up iterations and emits no consumer evidence. Measured workers continue
writing whole raw samples until the iteration, operation-count, and steady-state execution-duration
floors are all satisfied; setup, schema materialization, seeding, correctness, and validation time
do not contribute to the duration floor.

`consumer-evidence.json` deliberately omits provider configuration values. It records a digest of
the redacted provider configuration plus workload identity/version/fingerprint, provider identity
and version, storage form, data shape, raw-sample digest, measurement digest, native-plan digest,
and a provider/machine-independent correctness result digest. Elsa #646 can join on those fields
without Groundwork embedding Elsa or EF domain code.

## Remaining issue #50 acceptance work

- Ratify concrete payload-size and selectivity vectors, then execute them across
  the 1K/100K/1M dataset matrix, including the entity-form benefit classification.
- Capture exact-HEAD live evidence from SQLite, SQL Server, PostgreSQL, and MongoDB.
- Join the Groundwork results with an Elsa-owned EF Core oracle using matched workloads and controls.
- Complete reliable provider database-work/round-trip signals and concurrent-load evidence.
- Define, approve, integrity-protect, and exercise the immutable-baseline workflow.
- Add actual crash/failure recovery workloads only if issue #50 requires those semantics.

Until all applicable items are ratified and complete, the harness stays non-promotable and
non-decisional.
