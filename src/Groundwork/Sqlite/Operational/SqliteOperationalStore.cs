using Groundwork.Core.Transactions;
using Groundwork.Operational;
using Groundwork.Operational.Relational;
using Groundwork.Operational.UnitOfWork;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Operational;

/// <summary>
/// SQLite reference implementation of the operational store family. SQLite supports a single
/// cross-unit transaction, so the boundary is <see cref="TransactionBoundary.CrossUnitAtomic"/>.
/// </summary>
public sealed class SqliteOperationalStore(SqliteConnection connection, IOperationalClock? clock = null)
    : RelationalOperationalStore(connection, clock, TransactionBoundary.CrossUnitAtomic);
