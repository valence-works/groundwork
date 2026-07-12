using Groundwork.Provider.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

internal static class SqliteRelationalSessions
{
    public static RelationalSessionFactory CreateSerialized(string connectionString)
    {
        ValidateStatelessConnectionString(connectionString);

        return RelationalSessionFactory.Serialized(() => new SqliteConnection(connectionString));
    }

    internal static void ValidateStatelessConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException(
                "A per-operation SQLite store requires a non-empty file-backed data source.",
                nameof(connectionString));
        }

        var queryIndex = dataSource.IndexOf('?');
        var uriPath = queryIndex >= 0 ? dataSource[..queryIndex] : dataSource;
        if (builder.Mode == SqliteOpenMode.Memory ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            uriPath.Equals("file::memory:", StringComparison.OrdinalIgnoreCase) ||
            HasMemoryUriMode(dataSource, queryIndex))
        {
            throw new ArgumentException(
                "A per-operation SQLite store cannot use a private in-memory database. " +
                "Use the direct-connection constructor for an explicitly serialized in-memory store.",
                nameof(connectionString));
        }
    }

    private static bool HasMemoryUriMode(string dataSource, int queryIndex)
    {
        if (queryIndex < 0 || !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var parameter in dataSource[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = parameter.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var name = Uri.UnescapeDataString(parameter[..separatorIndex]);
            var value = Uri.UnescapeDataString(parameter[(separatorIndex + 1)..]);
            if (name.Equals("mode", StringComparison.OrdinalIgnoreCase) &&
                value.Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
