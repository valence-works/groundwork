using System.Globalization;

namespace Groundwork.DiagnosticRecords;

public static class DiagnosticStringComparisonKey
{
    public const string OrdinalAlgorithmId = "groundwork-utf16-hex-v1";
    public const string AsciiIgnoreCaseAlgorithmId = "groundwork-ascii-lower-v1";

    public static bool IsPortableOrdinalValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\0' || char.IsLowSurrogate(character))
                return false;
            if (!char.IsHighSurrogate(character))
                continue;
            if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                return false;
        }
        return true;
    }

    public static string CreateOrdinal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsPortableOrdinalValue(value))
            throw new ArgumentException("Ordinal strings must be well-formed UTF-16 and cannot contain U+0000.", nameof(value));
        return string.Create(value.Length * 4, value, static (buffer, source) =>
        {
            const string hex = "0123456789ABCDEF";
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                var offset = index * 4;
                buffer[offset] = hex[(character >> 12) & 0xF];
                buffer[offset + 1] = hex[(character >> 8) & 0xF];
                buffer[offset + 2] = hex[(character >> 4) & 0xF];
                buffer[offset + 3] = hex[character & 0xF];
            }
        });
    }

    public static bool IsAsciiIgnoreCaseValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.All(character => character is >= ' ' and <= '~');
    }

    public static string CreateAsciiIgnoreCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsAsciiIgnoreCaseValue(value))
            throw new ArgumentException(
                "ASCII-ignore-case values may contain only U+0020 through U+007E.",
                nameof(value));
        return string.Create(value.Length, value, static (buffer, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                buffer[index] = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            }
        });
    }
}

public readonly record struct DiagnosticFieldValue
{
    public DiagnosticFieldValue(DiagnosticFieldType type, string canonicalValue)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        Type = type;
        CanonicalValue = type switch
        {
            DiagnosticFieldType.String when DiagnosticStringComparisonKey.IsPortableOrdinalValue(canonicalValue) => canonicalValue,
            DiagnosticFieldType.Int64 when long.TryParse(canonicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                integer.ToString(CultureInfo.InvariantCulture),
            DiagnosticFieldType.Decimal when decimal.TryParse(canonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) =>
                number.ToString("G29", CultureInfo.InvariantCulture),
            DiagnosticFieldType.Boolean when bool.TryParse(canonicalValue, out var boolean) => boolean ? "true" : "false",
            DiagnosticFieldType.Timestamp when DateTimeOffset.TryParseExact(canonicalValue, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) =>
                timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"'{canonicalValue}' is not a valid canonical {type} value.", nameof(canonicalValue))
        };
    }

    public DiagnosticFieldType Type { get; }
    public string CanonicalValue { get; }
    public bool IsInitialized => CanonicalValue is not null;

    public static DiagnosticFieldValue String(string value) => new(DiagnosticFieldType.String, value ?? throw new ArgumentNullException(nameof(value)));
    public static DiagnosticFieldValue Int64(long value) => new(DiagnosticFieldType.Int64, value.ToString(CultureInfo.InvariantCulture));
    public static DiagnosticFieldValue Decimal(decimal value) => new(DiagnosticFieldType.Decimal, value.ToString("G29", CultureInfo.InvariantCulture));
    public static DiagnosticFieldValue Boolean(bool value) => new(DiagnosticFieldType.Boolean, value ? "true" : "false");
    public static DiagnosticFieldValue Timestamp(DateTimeOffset value) => new(DiagnosticFieldType.Timestamp, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    public int CompareTo(DiagnosticFieldValue other, DiagnosticStringCasePolicy casePolicy)
    {
        if (Type != other.Type)
            throw new InvalidOperationException("Field values of different portable types cannot be compared.");
        return Type switch
        {
            DiagnosticFieldType.String => casePolicy switch
            {
                DiagnosticStringCasePolicy.Ordinal => StringComparer.Ordinal.Compare(
                    DiagnosticStringComparisonKey.CreateOrdinal(CanonicalValue),
                    DiagnosticStringComparisonKey.CreateOrdinal(other.CanonicalValue)),
                DiagnosticStringCasePolicy.AsciiIgnoreCase => StringComparer.Ordinal.Compare(
                    DiagnosticStringComparisonKey.CreateAsciiIgnoreCase(CanonicalValue),
                    DiagnosticStringComparisonKey.CreateAsciiIgnoreCase(other.CanonicalValue)),
                _ => throw new ArgumentOutOfRangeException(nameof(casePolicy))
            },
            DiagnosticFieldType.Int64 => long.Parse(CanonicalValue, CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(other.CanonicalValue, CultureInfo.InvariantCulture)),
            DiagnosticFieldType.Decimal => decimal.Parse(CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture)
                .CompareTo(decimal.Parse(other.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture)),
            DiagnosticFieldType.Boolean => bool.Parse(CanonicalValue).CompareTo(bool.Parse(other.CanonicalValue)),
            DiagnosticFieldType.Timestamp => DateTimeOffset.Parse(CanonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .CompareTo(DateTimeOffset.Parse(other.CanonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
