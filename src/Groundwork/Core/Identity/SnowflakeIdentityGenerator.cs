namespace Groundwork.Core.Identity;

/// <summary>
/// Short 64-bit Snowflake generator, encoded as a fixed 11-character <see cref="Base62"/> string.
/// Layout (high → low): 41-bit millisecond timestamp (from the configured epoch) | 10-bit worker id
/// (0–1023) | 12-bit sequence. Strictly increasing and collision-free per worker; distinct workers
/// never collide. A single instance holds the monotonic state, so create one per worker and reuse it.
/// </summary>
public sealed class SnowflakeIdentityGenerator : IGroundworkIdentityGenerator
{
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const long MaxSequence = (1L << SequenceBits) - 1;
    private const int TimestampShift = WorkerIdBits + SequenceBits;
    private const int WorkerIdShift = SequenceBits;

    private readonly TimeProvider timeProvider;
    private readonly SnowflakeIdentityGeneratorOptions options;
    private readonly Lock gate = new();

    private long lastTimestamp = -1;
    private long sequence;

    public SnowflakeIdentityGenerator(TimeProvider timeProvider, SnowflakeIdentityGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.timeProvider = timeProvider;
        this.options = options;
    }

    public string Generate()
    {
        lock (gate)
        {
            var timestamp = CurrentMilliseconds();

            if (timestamp < lastTimestamp)
                throw new InvalidOperationException(
                    $"Clock moved backwards. Refusing to generate id for {lastTimestamp - timestamp} ms.");

            if (timestamp == lastTimestamp)
            {
                sequence = (sequence + 1) & MaxSequence;
                if (sequence == 0)
                    timestamp = WaitForNextMillisecond(lastTimestamp);
            }
            else
            {
                sequence = 0;
            }

            lastTimestamp = timestamp;

            var value = ((ulong)timestamp << TimestampShift)
                        | ((ulong)options.WorkerId << WorkerIdShift)
                        | (ulong)sequence;
            return Base62.Encode(value);
        }
    }

    private long CurrentMilliseconds() => (long)(timeProvider.GetUtcNow() - options.Epoch).TotalMilliseconds;

    private long WaitForNextMillisecond(long currentTimestamp)
    {
        var timestamp = CurrentMilliseconds();
        while (timestamp <= currentTimestamp)
            timestamp = CurrentMilliseconds();
        return timestamp;
    }
}
