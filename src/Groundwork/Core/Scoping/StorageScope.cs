namespace Groundwork.Core.Scoping;

/// <summary>
/// A provider-neutral storage-partition identity. It is an opaque persistence-boundary value, not
/// an application authorization decision and not a value read from document payload data.
/// </summary>
public sealed record StorageScope
{
    public const int MaxValueLength = 128;

    public StorageScope(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxValueLength)
            throw new ArgumentException($"Storage scope values cannot exceed {MaxValueLength} UTF-16 code units.", nameof(value));
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Storage scope values cannot have leading or trailing whitespace.", nameof(value));
        ValidateWellFormedUnicode(value);
        if (value.StartsWith("__groundwork_", StringComparison.Ordinal))
            throw new ArgumentException("Storage scope values beginning with '__groundwork_' are reserved.", nameof(value));

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static void ValidateWellFormedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\0')
                throw new ArgumentException("Storage scope values cannot contain NUL characters.", nameof(value));

            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException("Storage scope values must contain well-formed UTF-16.", nameof(value));

                index++;
                continue;
            }

            if (char.IsLowSurrogate(current))
                throw new ArgumentException("Storage scope values must contain well-formed UTF-16.", nameof(value));
        }
    }
}

/// <summary>Whether a storage unit is deliberately global or requires a bound scope.</summary>
public enum StorageScopePolicy
{
    Global,
    Scoped
}
