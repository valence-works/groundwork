# Groundwork Operational Relational

Shared relational plumbing for the `Groundwork.Operational` contracts. It implements
`IWorkQueueStore`, `ILeaseStore`, `IOutboxStore`, and the cross-unit
`IOperationalUnitOfWork` / `IOperationalSessionFactory` against ADO.NET (`System.Data.Common`) using
ANSI-ish SQL that the relational providers specialize.

Concurrency follows the document store's model: a single `DbConnection` guarded by a
`SemaphoreSlim`. Autonomous store calls take the gate, run inside their own transaction, and commit.
A unit of work holds the gate for its lifetime and commits every enlisted operation in one
`DbTransaction`, giving cross-unit atomic commit (`TransactionBoundary.CrossUnitAtomic`). A provider
whose boundary is `PerOperation` throws `UnsupportedAtomicCommitException` from `BeginAsync`.

`RelationalOperationalSchema` exposes the table-creation SQL so each provider's materializer can
create the operational tables. This package references `Groundwork.Core` and `Groundwork.Operational`.
