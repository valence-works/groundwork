using Groundwork.Provider.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

internal static class SqliteRelationalSessions
{
    public static RelationalSessionFactory CreateSerialized(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:")
        {
            throw new ArgumentException(
                "A per-operation SQLite store cannot use a private in-memory database. " +
                "Use the direct-connection constructor for an explicitly serialized in-memory store.",
                nameof(connectionString));
        }

        return RelationalSessionFactory.Serialized(() => new SqliteConnection(connectionString));
    }
}
