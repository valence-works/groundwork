namespace Groundwork.Operational;

/// <summary>
/// Abstracts "now" for operational stores so visibility timeouts, lease expiry, and retry delays can
/// be driven deterministically in tests. All values are expected to be UTC.
/// </summary>
public interface IOperationalClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Default clock backed by the system wall clock.</summary>
public sealed class SystemOperationalClock : IOperationalClock
{
    public static SystemOperationalClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
