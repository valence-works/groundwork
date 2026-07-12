# Relational session lifecycle prototype

Program goal state: [Physical Storage and Operations Readiness](../program-goals/physical-storage-and-operations-readiness.md).
Tracking: [Issue #27](https://github.com/valence-works/Groundwork/issues/27), [ADR 0003](../adr/0003-adopt-three-physical-storage-forms.md).

Status: prototype implemented and exercised through the public document and operational interfaces.

## Decision summary

Adopt `RelationalSessionFactory` as the provider-neutral relational lifecycle seam.

- A stateless document or operational facade holds the factory, not a connection.
- Every independent operation obtains, opens, and disposes one `DbConnection`. ADO.NET provider pooling owns physical connection reuse.
- An explicit unit of work owns one connection and one transaction until commit, rollback, failed commit, or disposal.
- SQL Server and PostgreSQL use `RelationalSessionFactory.Concurrent` and therefore have no Groundwork-wide serialization gate.
- SQLite uses `RelationalSessionFactory.Serialized`. It still receives an owned connection per operation, but Groundwork admits only one operation at a time for the store instance.
- Private SQLite in-memory databases cannot support per-operation ownership because every connection sees a different database. Stateless SQLite constructors reject that configuration and direct callers to the existing direct-connection constructor, whose serialization and lifetime are explicit.

The interface is deliberately small: create concurrent or serialized factories, execute one independent operation, obtain an autonomous transactional executor for operational stores, or begin one explicit unit of work. Connection opening, cancellation cleanup, rollback-on-dispose, transaction disposal, connection disposal, and SQLite gate release remain inside the module.

## Previous behavior

| Provider path | Connection ownership | Serialization | Transaction ownership |
|---|---|---|---|
| Relational document store | One caller-supplied connection retained by the store | One `SemaphoreSlim` per store | Independent writes and document UoWs share the retained connection |
| Relational operational store | One caller-supplied connection retained by `RelationalSession` | One `SemaphoreSlim` per store | Autonomous calls and operational UoWs share the retained connection |
| SQL Server | Same generic retained-connection behavior | All calls globally serialized within a store | ADO.NET pool pressure was hidden because only one connection was used |
| PostgreSQL | Same generic retained-connection behavior | All calls globally serialized within a store | ADO.NET pool pressure was hidden because only one connection was used |
| SQLite | Same generic retained-connection behavior | Serialized | Correct for private `:memory:` databases and conservative for file databases |

This made SQL Server and PostgreSQL concurrency measurements unrepresentative and let one slow operation block every unrelated operation on the same facade.

## Prototype behavior and evidence

The relational document facade now has a session-factory constructor while retaining the direct-connection constructor as a compatibility path. SQLite, SQL Server, and PostgreSQL expose connection-string constructors that select the correct policy. The relational operational facade and SQLite operational adapter expose the same stateless path.

Lifecycle tests prove:

- an independent operation observes an open connection and leaves it disposed;
- pre-cancellation does not create a connection;
- in-flight cancellation and exceptions dispose the operation connection;
- explicit UoWs keep one connection open across enlisted operations;
- commit persists and releases both transaction and connection;
- disposal without commit rolls back and releases both resources;
- concurrent terminal calls share one completion, so exactly one commit or rollback runs and resources release once;
- an open, commit, or rollback failure remains the surfaced failure when cleanup also fails; cleanup failures are attached as diagnostic exception data;
- concurrent policy permits two operations to overlap;
- serialized policy prevents a second SQLite operation from entering until the first releases its session;
- SQL Server and PostgreSQL permit two operations to occupy a two-connection pool concurrently while a third waits at provider pool pressure and can be cancelled;
- the established SQLite document and operational UoW suites pass through the stateless path, preserving document OCC, atomic write, commit, rollback, and read-your-writes semantics.

The broad relational-provider suite is container-startup bound rather than lifecycle-deadlocked. xUnit creates a provider test-class instance per test case, and each instance currently starts and disposes a fresh Testcontainers database. A diagnostic run with a 90-second per-test hang threshold completed all 26 cases in 4 minutes 1 second; its log showed each completed case dispose its container before the next instance started. Future test-infrastructure work may share provider containers to shorten the suite, but that is independent of the production session lifecycle.

## Recommended production interface

Keep `RelationalSessionFactory` in `Groundwork.Provider.Relational` as the lifecycle module used by both document and specialized operational adapters. Provider packages should construct it rather than ask application hosts to choose concurrency policy:

- SQL Server: concurrent factory over `SqlConnection`.
- PostgreSQL: concurrent factory over `NpgsqlConnection`.
- SQLite file database: serialized factory over `SqliteConnection`.
- SQLite private in-memory database: explicitly retained direct connection, limited to development/test scenarios. The provider's session-factory injection seam is internal so public callers cannot bypass this policy or select concurrent SQLite access.

Provider-facing registration should eventually accept an async connection/session source so future providers can use native data sources (`NpgsqlDataSource`, for example) without changing document or operational contracts. Do not expose provider pools, gates, or ambient transactions to feature modules.

## Compatibility and migration impact

The prototype retains existing direct-connection constructors and the existing `RelationalSession` implementation so current callers continue to compile. Those paths remain single-connection and serialized. Migration is opt-in through the new factory or connection-string constructors.

Before declaring the production migration complete:

1. Change SQL Server and PostgreSQL DI/factory registrations to construct stateless facades by default.
2. Change file-backed SQLite registrations to the serialized stateless path; keep the direct-connection path only for explicit private in-memory usage.
3. Decide whether existing document-store handles should stop exposing an owned connection. Keeping a materialization connection alive solely for a handle would consume pool capacity and confuse ownership.
4. Add provider-native async source adapters where useful, then obsolete the retained-connection relational constructors in a separately reviewed compatibility change.
5. Add session/pool diagnostics required by ADR 0003 before final provider conformance.

No consuming `IDocumentStore`, `IDocumentUnitOfWork`, `IOperationalSessionFactory`, or operational-store interface changes are required.

## Benchmark prerequisites

Performance comparisons are meaningful only after the benchmark host uses the stateless constructors or factory registrations. Record at minimum:

- connection string and provider pool limits;
- active/idle connections, pool wait time, pool timeouts, and cancellation count;
- operation and UoW duration separated from connection-open/pool-wait duration;
- concurrency levels 1, 8, 32, and 64;
- cold and warm pool runs;
- p50/p95/p99 latency, throughput, allocations, database CPU/reads/writes/locks, and round trips;
- UoW commit/rollback/failure counts and OCC conflict/retry behavior;
- SQLite serialized wait time separately from database execution time.

The EF Core comparison must use equivalent provider pool sizes and transaction boundaries. A result produced by the retained-connection compatibility path is not admissible evidence for SQL Server or PostgreSQL performance.

## Follow-up decisions

- Whether `RelationalSessionFactory` should accept `Func<CancellationToken, ValueTask<DbConnection>>` or provider-native data sources before its interface is declared stable.
- Whether SQLite should later permit explicitly configured concurrent read sessions while retaining serialized writes; this prototype intentionally chooses the narrower all-operation boundary.
- Exact public vocabulary should be reconciled with the planned Groundwork vocabulary and public-interface review rather than stabilized from this prototype alone.
- The accepted [vocabulary and public-API reconciliation](groundwork-vocabulary-and-public-api.md) does not prescribe session vocabulary. Revisit `RelationalSessionFactory` with the rest of the additive bridge before treating the name or synchronous connection-source delegate as stable.
