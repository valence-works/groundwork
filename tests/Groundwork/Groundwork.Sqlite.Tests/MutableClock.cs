using Groundwork.Operational;

namespace Groundwork.Sqlite.Tests;

/// <summary>Test clock allowing deterministic control of visibility/lease/expiry timing.</summary>
internal sealed class MutableClock(DateTimeOffset start) : IOperationalClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
