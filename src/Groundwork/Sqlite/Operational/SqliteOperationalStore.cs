using Groundwork.Core.Identity;
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
public sealed class SqliteOperationalStore : RelationalOperationalStore
{
    public SqliteOperationalStore(
        SqliteConnection connection,
        IOperationalClock? clock = null,
        IIdentityGenerator? identityGenerator = null)
        : base(connection, clock, TransactionBoundary.CrossUnitAtomic, identityGenerator)
    {
    }

    public SqliteOperationalStore(
        string connectionString,
        IOperationalClock? clock = null,
        IIdentityGenerator? identityGenerator = null)
        : base(
            SqliteRelationalSessions.CreateSerialized(connectionString),
            clock,
            TransactionBoundary.CrossUnitAtomic,
            identityGenerator)
    {
    }
}
