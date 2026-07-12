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

Write transactions advance durable non-regressing provider time and perform bounded cleanup of
append/trim identities only after their tombstone admission horizon. Relational providers own the
native writer boundary used before ledger and cursor reads; SQLite uses an immediate transaction.
