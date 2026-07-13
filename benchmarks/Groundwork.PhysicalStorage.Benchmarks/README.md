# Groundwork physical-storage benchmark harness scaffolding

This project is an honest, mergeable harness-scaffolding slice for issue #50. It exercises
Groundwork's production document-store path across SQLite, SQL Server, PostgreSQL, and MongoDB and
the three physical storage forms. It materializes real manifests, creates real storage, uses
production sessions and bounded-query translation, and records provider-native query plans.

It does **not** complete issue #50. The current profiles are not a ratified performance matrix,
contain no EF Core comparison, cannot be promoted as baselines, and cannot make an Elsa migration
go/no-go decision. `reports/elsa-migration-evidence.json` records this as `readiness: insufficient`
and lists the missing acceptance evidence.

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

## Diagnostic profiles

The profiles provide repeatable harness controls, not the final issue #50 matrix:

| Control | Smoke | Scheduled scaffold |
|---|---:|---:|
| Seed | 20260713 | 20260713 |
| Primary dataset | 250 | 10,000 |
| Migration dataset | 100 | 5,000 |
| Warmup iterations | 2 | 5 |
| Measured iterations | 7 | 30 |
| Operations per measured batch | 10 | 100 |
| Concurrency | 4 | 16 |
| Default providers | SQLite | All four |
| Storage forms | All three | All three |

Both profiles always emit `baselineEligibility.eligible: false`. Diagnostics explain that issue #50
still requires the 1K/100K/1M matrix across payload sizes and query selectivity, exact-HEAD live
evidence from all four providers, and the Elsa-owned EF Core oracle.

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
  --workloads indexed-query,mixed-compound-ordering
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

Each raw sample is one measured batch. The harness records elapsed time and operation count, then
normalizes that batch mean to nanoseconds per operation. Summary p50/p95/p99 values are percentiles
across those normalized **batch means**:

- `normalizedBatchLatencyNanosecondsPerOperation` on a raw sample;
- `normalizedBatchLatencyP50NanosecondsPerOperation`, p95, and p99 on summaries/evidence.

They are not percentiles of individual-operation latency because the harness does not record an
individual latency distribution. Reports also contain aggregate throughput, allocation per
operation, observable round trips, net storage growth per logical payload byte, net physical-row
growth per logical mutation, provider work signals, and native-plan evidence where observable. These
are net cardinality/storage ratios, not database write-amplification measurements. A missing
round-trip signal is `null`, never zero.

Regression comparisons remain available as diagnostic scaffolding. Current scheduled evidence is
explicitly incompatible with gating because its evidence readiness is insufficient and its baseline
eligibility is false. The committed baseline registry is empty and disabled.

## Artifact contract

```text
manifest.json
metadata/configuration.json
metadata/machine.json
metadata/providers.json
plans/<provider>/<form>/<workload>-<selection|count>.<native-extension>
plans/<provider>/<form>/<workload>-<selection|count>.<native-extension>.assertions.json
raw/measurements.jsonl
reports/summary.json
reports/summary.md
reports/regression.json
reports/elsa-migration-evidence.json
```

The v1 JSON Schemas live in [`schemas/v1`](schemas/v1). The evidence report deliberately exposes:

- `readiness: insufficient`;
- `elsaEfOracleRequired: true`;
- `baselineEligibility.eligible: false` with concrete diagnostics;
- Groundwork case evidence and diagnostic regression signals; and
- `remainingAcceptanceWork` for the later Elsa-owned evidence join.

No artifact in this slice is a migration decision or baseline-promotion authorization.

## Remaining issue #50 acceptance work

- Execute the ratified 1K/100K/1M dataset matrix across multiple payload sizes and selectivity
  values, including the entity-form benefit classification.
- Capture exact-HEAD live evidence from SQLite, SQL Server, PostgreSQL, and MongoDB.
- Join the Groundwork results with an Elsa-owned EF Core oracle using matched workloads and controls.
- Complete reliable provider database-work/round-trip signals and concurrent-load evidence.
- Define, approve, integrity-protect, and exercise the immutable-baseline workflow.
- Add actual crash/failure recovery workloads only if issue #50 requires those semantics.

Until all applicable items are ratified and complete, the harness stays non-promotable and
non-decisional.
