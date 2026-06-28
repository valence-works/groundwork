namespace Groundwork.Core.Identity;

/// <summary>
/// Configuration for <see cref="SnowflakeIdentityGenerator"/>. <see cref="WorkerId"/> must be unique
/// per concurrently running generator instance so ids never collide across workers.
/// </summary>
public sealed class SnowflakeIdentityGeneratorOptions
{
    /// <summary>Inclusive lower bound of the valid worker-id range.</summary>
    public const long MinWorkerId = 0;

    /// <summary>Inclusive upper bound of the valid worker-id range (10-bit field).</summary>
    public const long MaxWorkerId = 1023;

    private readonly long workerId;

    /// <summary>The 10-bit worker id (0–1023). Must be unique per running instance.</summary>
    public long WorkerId
    {
        get => workerId;
        init
        {
            if (value is < MinWorkerId or > MaxWorkerId)
                throw new ArgumentOutOfRangeException(nameof(WorkerId), value,
                    $"WorkerId must be in [{MinWorkerId}, {MaxWorkerId}].");
            workerId = value;
        }
    }

    /// <summary>Epoch the millisecond timestamp is measured from. Defaults to <c>2020-01-01T00:00:00Z</c>.</summary>
    public DateTimeOffset Epoch { get; init; } = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
