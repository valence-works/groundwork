# Groundwork Diagnostic Records — Relational

`Groundwork.DiagnosticRecords.Relational` is the shared relational implementation kernel for the
provider-neutral `IDiagnosticRecordStore` contract. It does not depend on ordinary Groundwork
document storage and it does not expose `IQueryable` or provider expressions.

The package owns a portable relational schema model and the transactional behavior shared by SQLite,
SQL Server, and PostgreSQL providers: scope-local cursor allocation, immutable records and field
comparison keys, durable provider time, append/trim outcomes and tombstones, exact `KeepNewest`
retention, and bounded SQL query translation. Provider packages supply native DDL, connection/session
policy, pagination/contains syntax, transaction semantics, and provider-specific cleanup SQL.

All storage keys include tenant, host storage scope, and stream identity. Query pages hydrate fields
only for the bounded page selected by SQL; predicates, latest-per-key grouping, exact counts,
ordering, and continuation boundaries never fall back to client filtering.

Server writes acquire a session-scoped writer boundary for one scope and stream, then advance
durable non-regressing provider time in a short atomic transaction before beginning the mutation
transaction on that same pooled connection. This keeps commit-time expiry ordered with same-stream
writers without holding the provider-clock row for the mutation transaction or consuming a nested
pool connection. SQL Server uses `sp_getapplock`, PostgreSQL uses a session advisory lock, and SQLite
retains its immediate-transaction serialization. Record mutation performs bounded cleanup only
after the tombstone admission horizon. Unrelated server streams therefore remain live; no singleton
row or process-wide Groundwork semaphore serializes their mutations.

Provider dialects also own lexical identifier preparation, parameter typing, count/limit rewriting,
binary-text mapping, partial/filtered latest-per-key indexes, and bounded cleanup syntax. This keeps
the contract/kernel free of SQL Server, Npgsql, and SQLite SDK types while still allowing every
provider to generate and execute native plans.

Ordinal strings use the versioned `groundwork-utf16-hex-v1` comparison key. It preserves .NET
UTF-16 ordinal order in every provider's binary text domain; malformed UTF-16 and U+0000 are outside
the portable string domain. Predicate values, conflict probes, and field hydration are independently
bounded or chunked so provider wire-parameter ceilings are never accidental runtime limits.
