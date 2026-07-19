using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

/// <summary>
/// The single construction point for the SQLite connections Groundwork owns. Every connection it
/// returns applies <see cref="SqliteConnectionPragmaOptions"/> the moment it opens, so the pragma
/// policy lives in exactly one place regardless of which store, materializer, or inspector opens
/// the connection.
/// </summary>
public static class SqliteConnectionFactory
{
    /// <summary>
    /// Creates a connection that applies <paramref name="options"/> (or
    /// <see cref="SqliteConnectionPragmaOptions.Default"/>) on every open. The returned instance is a
    /// <see cref="SqliteConnection"/> and is a drop-in for any connection factory delegate.
    /// </summary>
    public static SqliteConnection Create(string connectionString, SqliteConnectionPragmaOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new PragmaSqliteConnection(connectionString, options ?? SqliteConnectionPragmaOptions.Default);
    }

    private sealed class PragmaSqliteConnection(string connectionString, SqliteConnectionPragmaOptions options)
        : SqliteConnection(connectionString)
    {
        // OpenAsync's base implementation dispatches to Open(), so overriding Open() covers both the
        // synchronous and asynchronous open paths the relational infrastructure uses.
        public override void Open()
        {
            base.Open();
            SqliteConnectionPragmas.Apply(this, options);
        }
    }
}

/// <summary>Builds and applies the connection-open pragma statements for a SQLite connection.</summary>
internal static class SqliteConnectionPragmas
{
    /// <summary>
    /// Returns the pragma statements to run against a freshly opened connection. WAL is emitted only
    /// for writable, file-backed databases; <c>synchronous</c> only for writable databases;
    /// <c>busy_timeout</c> for every connection (including read-only readers that benefit from
    /// waiting out a writer's lock).
    /// </summary>
    public static IReadOnlyList<string> BuildStatements(
        SqliteConnectionStringBuilder builder,
        SqliteConnectionPragmaOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var isReadOnly = builder.Mode == SqliteOpenMode.ReadOnly;
        var isInMemory = SqliteRelationalSessions.IsInMemory(builder);
        var statements = new List<string>(3);

        if (options.BusyTimeout > TimeSpan.Zero)
        {
            var milliseconds = (long)Math.Round(options.BusyTimeout.TotalMilliseconds, MidpointRounding.AwayFromZero);
            statements.Add($"PRAGMA busy_timeout={milliseconds.ToString(CultureInfo.InvariantCulture)};");
        }

        if (isReadOnly)
            return statements;

        // WAL is a no-op error case for an in-memory database, so it is only issued for file-backed
        // databases. It is persistent per-database but harmless (and cheap) to re-issue on each open.
        if (options.WriteAheadLogging && !isInMemory)
            statements.Add("PRAGMA journal_mode=WAL;");

        statements.Add($"PRAGMA synchronous={SqliteConnectionPragmaOptions.ToPragmaValue(options.Synchronous)};");

        return statements;
    }

    public static void Apply(SqliteConnection connection, SqliteConnectionPragmaOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        var statements = BuildStatements(builder, options);
        if (statements.Count == 0)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(statements);
        command.ExecuteNonQuery();
    }
}
