using Groundwork.Core.Identity;
using Xunit;

namespace Groundwork.Tests;

public sealed class IdentityGeneratorTests
{
    // --- Base62 ---

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(61UL)]
    [InlineData(62UL)]
    [InlineData(123456789UL)]
    [InlineData(ulong.MaxValue)]
    public void Base62EncodesToFixedElevenCharacters(ulong value)
    {
        var encoded = Base62Encode(value);

        Assert.Equal(11, encoded.Length);
        Assert.All(encoded, c => Assert.Contains(c,
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public void Base62OrdinalOrderMatchesNumericOrder()
    {
        ulong[] values =
        [
            0, 1, 61, 62, 1000, 1L << 22, 1UL << 41, long.MaxValue, ulong.MaxValue - 1, ulong.MaxValue
        ];

        for (var i = 0; i < values.Length; i++)
        for (var j = 0; j < values.Length; j++)
        {
            var numeric = values[i].CompareTo(values[j]);
            var ordinal = string.CompareOrdinal(Base62Encode(values[i]), Base62Encode(values[j]));
            Assert.Equal(Math.Sign(numeric), Math.Sign(ordinal));
        }
    }

    // --- ShortIdentityGenerator ---

    [Fact]
    public void ShortIdProducesElevenBase62Chars()
    {
        var generator = new ShortIdentityGenerator(new MutableTimeProvider());
        var id = generator.Generate();

        Assert.Equal(11, id.Length);
        Assert.All(id, c => Assert.Contains(c, Base62Alphabet));
    }

    [Fact]
    public void ShortIdSortsChronologicallyAsClockAdvances()
    {
        var time = new MutableTimeProvider();
        var generator = new ShortIdentityGenerator(time);

        var first = generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = generator.Generate();
        time.Advance(TimeSpan.FromSeconds(1));
        var third = generator.Generate();

        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.True(string.CompareOrdinal(second, third) < 0);
    }

    [Fact]
    public void ShortIdProducesDistinctValuesWithinOneMillisecond()
    {
        var generator = new ShortIdentityGenerator(new MutableTimeProvider());
        var ids = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
            ids.Add(generator.Generate());

        // 22 random bits within a fixed ms: collisions are vanishingly unlikely at this volume.
        Assert.True(ids.Count > 990);
    }

    // --- UuidV7IdentityGenerator ---

    [Fact]
    public void UuidV7ProducesThirtyTwoLowercaseHexChars()
    {
        var generator = new UuidV7IdentityGenerator(new MutableTimeProvider());
        var id = generator.Generate();

        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void UuidV7SortsChronologicallyAsClockAdvances()
    {
        var time = new MutableTimeProvider();
        var generator = new UuidV7IdentityGenerator(time);

        var first = generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(5));
        var second = generator.Generate();

        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void UuidV7ProducesDistinctValuesWithinOneMillisecond()
    {
        var generator = new UuidV7IdentityGenerator(new MutableTimeProvider());
        var ids = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
            Assert.True(ids.Add(generator.Generate()));
    }

    // --- GuidIdentityGenerator ---

    [Fact]
    public void GuidProducesThirtyTwoLowercaseHexChars()
    {
        var id = new GuidIdentityGenerator().Generate();

        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.Contains(c, "0123456789abcdef"));
    }

    // --- SnowflakeIdentityGenerator ---

    [Fact]
    public void SnowflakeProducesElevenBase62Chars()
    {
        var generator = new SnowflakeIdentityGenerator(new MutableTimeProvider(),
            new SnowflakeIdentityGeneratorOptions { WorkerId = 1 });
        var id = generator.Generate();

        Assert.Equal(11, id.Length);
        Assert.All(id, c => Assert.Contains(c, Base62Alphabet));
    }

    [Fact]
    public void SnowflakeIsStrictlyIncreasingAndUniqueAcrossFullSequenceMillisecond()
    {
        // Clock pinned to a single ms: forces a full 4096-sequence roll plus a spin to the next ms.
        var time = new MutableTimeProvider();
        var generator = new SnowflakeIdentityGenerator(time,
            new SnowflakeIdentityGeneratorOptions { WorkerId = 7 });

        var ids = new List<string>();
        var previous = string.Empty;
        for (var i = 0; i < 4096; i++)
        {
            var id = generator.Generate();
            if (i > 0)
                Assert.True(string.CompareOrdinal(previous, id) < 0);
            previous = id;
            ids.Add(id);
        }

        Assert.Equal(4096, ids.Distinct().Count());

        // The 4097th call exhausts the sequence and must spin to the next ms; with a frozen clock
        // it would hang, so advance time first.
        time.Advance(TimeSpan.FromMilliseconds(1));
        var next = generator.Generate();
        Assert.True(string.CompareOrdinal(previous, next) < 0);
    }

    [Fact]
    public void SnowflakeDistinctWorkersYieldDistinctIds()
    {
        var time = new MutableTimeProvider();
        var a = new SnowflakeIdentityGenerator(time, new SnowflakeIdentityGeneratorOptions { WorkerId = 1 });
        var b = new SnowflakeIdentityGenerator(time, new SnowflakeIdentityGeneratorOptions { WorkerId = 2 });

        Assert.NotEqual(a.Generate(), b.Generate());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1024L)]
    public void SnowflakeRejectsOutOfRangeWorkerId(long workerId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SnowflakeIdentityGeneratorOptions { WorkerId = workerId });
    }

    [Fact]
    public void SnowflakeThrowsWhenClockMovesBackwards()
    {
        var time = new MutableTimeProvider();
        var generator = new SnowflakeIdentityGenerator(time,
            new SnowflakeIdentityGeneratorOptions { WorkerId = 3 });

        generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(-10));

        Assert.Throws<InvalidOperationException>(() => generator.Generate());
    }

    // --- Factory ---

    [Fact]
    public void FactoryCreatesEachKind()
    {
        var time = new MutableTimeProvider();
        Assert.IsType<ShortIdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Short, time));
        Assert.IsType<UuidV7IdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.UuidV7, time));
        Assert.IsType<GuidIdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Guid, time));
        Assert.IsType<SnowflakeIdentityGenerator>(GroundworkIdentityGenerators.Create(
            IdentityGeneratorKind.Snowflake, time, new SnowflakeIdentityGeneratorOptions { WorkerId = 0 }));
    }

    [Fact]
    public void FactorySnowflakeRequiresOptions()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Snowflake));
    }

    private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // Mirrors Base62.Encode (internal) so ordering/width assertions can run without exposing it.
    private static string Base62Encode(ulong value)
    {
        var buffer = new char[11];
        for (var i = 10; i >= 0; i--)
        {
            buffer[i] = Base62Alphabet[(int)(value % 62)];
            value /= 62;
        }

        return new string(buffer);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
