namespace Groundwork.Core.Identity;

/// <summary>Built-in identity generator kinds, for callers that prefer a single factory entry point.</summary>
public enum IdentityGeneratorKind
{
    /// <summary>Short, time-ordered 11-char Base62 id. The default. See <see cref="ShortIdentityGenerator"/>.</summary>
    Short,

    /// <summary>UUID v7 as 32 lowercase hex chars. See <see cref="UuidV7IdentityGenerator"/>.</summary>
    UuidV7,

    /// <summary>Coordinated 11-char Base62 Snowflake id. See <see cref="SnowflakeIdentityGenerator"/>.</summary>
    Snowflake,

    /// <summary>Random GUID as 32 lowercase hex chars; not sortable. See <see cref="GuidIdentityGenerator"/>.</summary>
    Guid
}

/// <summary>Convenience factory over the built-in identity generator catalog.</summary>
public static class GroundworkIdentityGenerators
{
    /// <summary>
    /// Creates an <see cref="IIdentityGenerator"/> for the requested <paramref name="kind"/>.
    /// <paramref name="snowflakeOptions"/> is required when <paramref name="kind"/> is
    /// <see cref="IdentityGeneratorKind.Snowflake"/> and ignored otherwise.
    /// </summary>
    public static IIdentityGenerator Create(
        IdentityGeneratorKind kind,
        TimeProvider? timeProvider = null,
        SnowflakeIdentityGeneratorOptions? snowflakeOptions = null)
    {
        timeProvider ??= TimeProvider.System;
        return kind switch
        {
            IdentityGeneratorKind.Short => new ShortIdentityGenerator(timeProvider),
            IdentityGeneratorKind.UuidV7 => new UuidV7IdentityGenerator(timeProvider),
            IdentityGeneratorKind.Snowflake => new SnowflakeIdentityGenerator(
                timeProvider,
                snowflakeOptions ?? throw new ArgumentNullException(nameof(snowflakeOptions),
                    "Snowflake generator requires options with a worker id.")),
            IdentityGeneratorKind.Guid => new GuidIdentityGenerator(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity generator kind.")
        };
    }
}
