namespace Groundwork.Core.Identity;

/// <summary>
/// Default generator. Packs a 64-bit value as a 42-bit millisecond timestamp (relative to the epoch
/// <c>2020-01-01T00:00:00Z</c>, valid until ~2159) in the high bits and 22 random bits in the low
/// bits, then <see cref="Base62.Encode"/>s it to a fixed 11-character string. Sortable to the
/// millisecond by ordinal string comparison and requires no coordination, at the cost of a small
/// per-millisecond collision probability under very high throughput.
/// </summary>
public sealed class ShortIdentityGenerator(TimeProvider? timeProvider = null) : IIdentityGenerator
{
    /// <summary>Epoch the millisecond timestamp is measured from, shared with the Snowflake default.</summary>
    internal static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int RandomBits = 22;
    private const int TimestampBits = 64 - RandomBits; // 42
    private const long MaxMilliseconds = (1L << TimestampBits) - 1;
    private const long RandomMask = (1L << RandomBits) - 1;

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Generate()
    {
        var milliseconds = (long)(timeProvider.GetUtcNow() - Epoch).TotalMilliseconds;
        if (milliseconds is < 0 or > MaxMilliseconds)
            throw new InvalidOperationException(
                $"Timestamp {milliseconds} ms is outside the representable {TimestampBits}-bit range [0, {MaxMilliseconds}] relative to epoch {Epoch:O}; cannot generate a short id.");

        var random = Random.Shared.NextInt64(1L << RandomBits);
        var value = ((ulong)milliseconds << RandomBits) | (ulong)(random & RandomMask);
        return Base62.Encode(value);
    }
}
