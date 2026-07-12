# Groundwork Operational Relational

Shared relational plumbing for the `Groundwork.Operational` contracts. It implements
`IWorkQueueStore`, `ILeaseStore`, `IOutboxStore`, and the cross-unit
`IOperationalUnitOfWork` / `IOperationalSessionFactory` against ADO.NET (`System.Data.Common`) using
ANSI-ish SQL that the relational providers specialize.

Concurrency follows the stateless document-store model. Autonomous calls acquire, open, and dispose
one provider-pooled `DbConnection` and run inside their own transaction. A unit of work owns one
connection and transaction for its lifetime and commits every enlisted operation in that
`DbTransaction`, giving cross-unit atomic commit (`TransactionBoundary.CrossUnitAtomic`). Providers
may select a serialized session policy where required (SQLite) without globally serializing SQL
Server or PostgreSQL. A provider whose boundary is `PerOperation` throws
`UnsupportedAtomicCommitException` from `BeginAsync`.

`RelationalOperationalSchema` exposes the table-creation SQL so each provider's materializer can
create the operational tables. This package references `Groundwork.Core` and `Groundwork.Operational`.
