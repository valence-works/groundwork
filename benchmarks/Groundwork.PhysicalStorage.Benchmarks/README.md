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
2. on a separately initialized disposable target with the exact configured measured cardinality,
   selectivity, and provider statistics, selection of the declared index through provider-native
   `EXPLAIN`, `STATISTICS XML`, or MongoDB `explain`, with a full scan of the predicate-bearing
   indexed relation rejected for every applicable timed selection and count shape. A linked form
   may still use an optimizer-selected scan of its separate primary payload relation after the
   linked predicate index has selected the bounded owner set; treating that as predicate fallback
   would be a false positive.

The backfill workload has an additional post-measurement check. Outside the timed region, it uses
the additive model to run the bounded query and directly queries the newly projected `category`
field. Both counts must match the seeded migration row count.

Relational statistics are finalized as part of deterministic seeding on both measured and plan
targets. Correctness-gate documents are removed and statistics are finalized again before timing.
Native-plan capture is read-only: it does not add or remove rows, change selectivity, or refresh
statistics. A provider that chooses a scan for the configured measured shape fails the gate; the
harness does not inflate a plan-only distribution to make the optimizer choose the declared index.

After materialization, SQLite, SQL Server, and PostgreSQL stores are opened through their public
production `OpenPhysicalAsync` factories. Factory admission must succeed before correctness gates
or timing begin, and restart paths re-enter through the same admission boundary.

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
a deliberately narrow SQLite/shared-form smoke over five representative workloads. Weekly/manual
runs split the four-provider scheduled scaffold into deterministic provider/form/dataset shards on
the controlled self-hosted runner pool. Every artifact remains non-promotable; the workflow does
not perform candidate promotion or migration-decision gating.

The scheduled cardinality is calculated, not inferred:

- `4 providers × 3 forms × 3 dataset sizes = 36` shards;
- each shard has `14 workloads × (1 untimed warm-up + 3 measured repetitions) = 56` workers;
- the complete schedule therefore has `2,016` workers: `504` warm-up and `1,512` measured; and
- the mandatory 30-second measured floor alone is `1,512 × 30 = 45,360 seconds`, or 12.6 aggregate
  measured hours before setup, seeding, validation, and artifact work.

With all 36 shard slots available, each shard carries 42 measured workers and therefore 21 minutes
of mandatory measured execution. The workflow budgets 20 minutes for one contract preflight, 280
minutes for the parallel shard critical path, and 60 minutes for final verification/aggregation:
360 minutes total execution budget, excluding external runner queueing. The controlled runner pool
must supply the declared 36-way capacity for that worst-case critical-path calculation to hold.
Reduced runner concurrency adds queue waves and increases end-to-end elapsed time; it does not
change shard contents or invalidate otherwise complete evidence. The 280-minute limit is enforced
per running shard job, not as a guarantee that the organization will schedule all shards at once.

All 36 shard artifacts are retained separately and downloaded into a retained aggregate artifact.
The final job checks the exact 2,016 request tuples, successful responses, consumer-evidence file
digests, exact Git commit, and provider/form equality of the canonical result digest. It writes
`coverage-verification.json` with `coverageVerified: true` only after every check succeeds. A
missing, timed-out, duplicated, or unequal shard therefore cannot be described as complete
scheduled coverage.

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

Run all cases represented by the scheduled scaffold locally (serial, and therefore at least 12.6
hours of mandatory measured time before setup overhead):

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
`operationLatencyP50Nanoseconds`, p95, and p99 values flatten those raw observations within one
worker; they never divide an invocation duration by its operation count. Run-group acceptance keeps
workers as independent process clusters: it computes each process statistic, uses the median of the
independent processes, and resamples processes before resampling observations within a selected
process.

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

Regression comparisons consume a coordinator run-group root, never a single warm-up or measured
worker directory. Candidate and baseline measured workers are matched by provider, storage form,
workload, complete data shape, and independent-run number. Scheduled comparisons reject tuples
with fewer than three independent measured processes. Current evidence remains non-promotable
because its evidence readiness is insufficient and its baseline eligibility is false. The
committed baseline registry is empty and disabled.

## Artifact contract

```text
run-group.json
protocol/requests/<ordinal>.json
protocol/responses/<ordinal>.json
reports/regression.json (when --baseline is supplied)
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
and a provider/machine-independent correctness result digest.

That correctness digest is SHA-256 over an ordered
`groundwork.physical-storage.observable-result/v1` vector. Vector entries carry canonical sequence,
stable identity, status, version, count, and payload outcomes. Provider identity/version,
configuration, storage form, machine metadata, timestamps, and timings do not participate. The
scheduled aggregate requires equality for every matching workload/data-shape group across all
providers, forms, and independent runs before the timing artifacts are accepted as complete
scaffold evidence. Elsa #646 can join on those fields without Groundwork embedding Elsa or EF
domain code.

The coordinator binds every worker request to the expected Git commit and worktree digest. The
run-group manifest records SHA-256 digests for every request, response, worker manifest, Elsa
evidence report, and measured consumer-evidence report. The verifier rejects path escapes, unknown
JSON members, identity mismatches, Git drift, and digest mismatches before a group can be used as a
baseline. Connection strings and provider secrets remain excluded.

Machine metadata records CPU model, memory, storage/filesystem capacity, and power/governor state
when the host exposes them, otherwise the literal `unavailable`. Provider metadata distinguishes
declared configuration from effective settings and explicitly marks settings unavailable when the
target cannot query them. Container sources include an immutable image digest when available and
otherwise record `immutableDigest=unavailable`.

## Independent review record

Three adversarial reviewers examined the initial candidate from base `c6d40b589a9296b2ada461caf6b4b0d58da401bb`
through `a7dea39d3c44809d32ff6c4313c6399424cc72e6` on distinct axes. All three blocked it:

- correctness/mechanism found that server-provider targets bypassed production factory admission
  and native-plan capture used a different, noise-inflated distribution;
- evidence integrity found weak correctness digests and provenance, incomplete group schemas and
  metadata, flattened-process statistics, and baseline comparison that did not require exact tuple
  equality; and
- scope/test preservation found that a serial 12.6-hour protocol could not fit the six-hour
  workflow, group verification was incomplete, and child exit status was not propagated.

The candidate was remediated by using the production factories and the same measured shape for
native plans; emitting canonical observable-result vectors from real outcomes for all 14
workloads; enforcing exact tuple/run identity, hierarchical process-first bootstrap statistics,
strict group schemas/digests, and nonzero child-exit propagation; and sharding the scheduled matrix
into 36 provider/form/cardinality jobs with an exact 2,016-worker aggregate verifier. The pull
request smoke remains deliberately narrow and every workflow artifact remains non-promotable.

The originating reviewers must re-verify the frozen remediation commit before merge. Their final
verdicts and any further dispositions are recorded here when that gate completes.

## Remaining issue #50 acceptance work

- Ratify concrete payload-size and selectivity vectors, then execute them across
  the 1K/100K/1M dataset matrix, including the entity-form benefit classification.
- Capture exact-HEAD live evidence from SQLite, SQL Server, PostgreSQL, and MongoDB.
- Join the Groundwork results with an Elsa-owned EF Core oracle using matched workloads and controls.
- Complete reliable provider database-work/round-trip signals and concurrent-load evidence.
- Define, approve, integrity-protect, and exercise the immutable-baseline workflow.
- Capture actual crash/failure recovery evidence required by issue #50. Client pool reset/reopen
  validation is not crash or failure recovery evidence and does not close that acceptance work.

Until all applicable items are ratified and complete, the harness stays non-promotable and
non-decisional.
