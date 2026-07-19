namespace Groundwork.Sqlite;

/// <summary>
/// The <c>synchronous</c> durability level applied to every SQLite connection Groundwork opens.
/// <see cref="Normal"/> pairs with write-ahead logging to keep commits durable across application
/// crashes while avoiding an <c>fsync</c> on every commit.
/// </summary>
public enum SqliteSynchronousMode
{
    Off,
    Normal,
    Full,
    Extra
}

/// <summary>
/// Connection pragmas Groundwork applies to every file-backed SQLite connection it opens. The
/// defaults enable write-ahead logging with <c>synchronous=NORMAL</c> and a busy timeout so that
/// writers no longer serialize against readers and commits avoid the multiple <c>fsync</c> calls
/// that dominate slow-fsync hosts (for example Docker Desktop on macOS).
/// </summary>
/// <remarks>
/// <para><see cref="WriteAheadLogging"/> switches the database journal into WAL mode. WAL is a
/// persistent, per-database property, so it is only meaningful for file-backed databases; it is
/// skipped for in-memory and read-only connections.</para>
/// <para><see cref="Synchronous"/> and <see cref="BusyTimeout"/> are per-connection settings and are
/// re-applied on every open.</para>
/// </remarks>
public sealed record SqliteConnectionPragmaOptions
{
    /// <summary>Enables WAL journal mode on file-backed databases. Defaults to <see langword="true"/>.</summary>
    public bool WriteAheadLogging { get; init; } = true;

    /// <summary>The <c>synchronous</c> durability level. Defaults to <see cref="SqliteSynchronousMode.Normal"/>.</summary>
    public SqliteSynchronousMode Synchronous { get; init; } = SqliteSynchronousMode.Normal;

    /// <summary>
    /// How long a connection waits for a lock before failing with <c>SQLITE_BUSY</c>. Defaults to five
    /// seconds. A non-positive value disables the pragma.
    /// </summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The shared default pragma set (WAL on, <c>synchronous=NORMAL</c>, five-second busy timeout).</summary>
    public static SqliteConnectionPragmaOptions Default { get; } = new();

    internal static string ToPragmaValue(SqliteSynchronousMode mode) => mode switch
    {
        SqliteSynchronousMode.Off => "OFF",
        SqliteSynchronousMode.Normal => "NORMAL",
        SqliteSynchronousMode.Full => "FULL",
        SqliteSynchronousMode.Extra => "EXTRA",
        _ => "NORMAL"
    };
}
