using System.Globalization;
using Groundwork.Core.Text;

namespace Groundwork.DiagnosticRecords;

public readonly record struct DiagnosticStringComparisonProjection(
    string ComparisonKey,
    string ComparisonKeyPrefix,
    string ComparisonKeyHash,
    string SearchKey);

public static class DiagnosticStringComparisonKey
{
    public const string OrdinalAlgorithmId = PortableStringComparison.OrdinalAlgorithmId;
    public const string AsciiIgnoreCaseAlgorithmId = PortableStringComparison.AsciiIgnoreCaseAlgorithmId;
    public const string LookupHashAlgorithmId = PortableStringComparison.LookupHashAlgorithmId;
    public const string SearchKeyAlgorithmId = "groundwork-boundary-delimited-search-key-v1";
    public const int BoundedPrefixLength = 256;

    public static string UnicodeOrdinalIgnoreCaseAlgorithmId => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId;
    public static bool IsPortableOrdinalValue(string value) => PortableStringComparison.IsWellFormedUnicode(value);
    public static string CreateOrdinal(string value) => PortableStringComparison.CreateOrdinal(value);
    public static string CreateUnicodeOrdinalIgnoreCase(string value) => PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value);
    public static string CreateAsciiIgnoreCase(string value) => PortableStringComparison.CreateAsciiIgnoreCase(value);
    public static bool IsAsciiIgnoreCaseValue(string value) => PortableStringComparison.IsAsciiIgnoreCaseValue(value);
    public static string CreateBoundedPrefix(string comparisonKey) =>
        PortableStringComparison.CreateBoundedPrefix(comparisonKey, BoundedPrefixLength);
    public static string CreateHash(string comparisonKey) => PortableStringComparison.CreateHash(comparisonKey);
    public static string Create(string value, DiagnosticStringCasePolicy casePolicy) =>
        PortableStringComparison.Create(value, Map(casePolicy));
    public static string CreateSearchKey(string value, DiagnosticStringCasePolicy casePolicy)
    {
        var policy = Map(casePolicy);
        return CreateSearchKeyFromComparison(PortableStringComparison.Create(value, policy), casePolicy);
    }
    public static DiagnosticStringComparisonProjection Project(
        string value,
        DiagnosticStringCasePolicy casePolicy)
    {
        var policy = Map(casePolicy);
        var identity = PortableStringComparison.ProjectIdentity(value, policy);
        return new(
            identity.ComparisonKey,
            PortableStringComparison.CreateBoundedPrefix(identity.ComparisonKey, BoundedPrefixLength),
            identity.ComparisonKeyHash,
            CreateSearchKeyFromComparison(identity.ComparisonKey, casePolicy));
    }

    private static string CreateSearchKeyFromComparison(
        string comparisonKey,
        DiagnosticStringCasePolicy casePolicy)
    {
        if (casePolicy == DiagnosticStringCasePolicy.AsciiIgnoreCase)
        {
            return string.Create(comparisonKey.Length * 5, comparisonKey, static (buffer, source) =>
            {
                const string hex = "0123456789ABCDEF";
                for (var index = 0; index < source.Length; index++)
                {
                    var character = source[index];
                    var offset = index * 5;
                    buffer[offset] = '|';
                    buffer[offset + 1] = hex[(character >> 12) & 0xF];
                    buffer[offset + 2] = hex[(character >> 8) & 0xF];
                    buffer[offset + 3] = hex[(character >> 4) & 0xF];
                    buffer[offset + 4] = hex[character & 0xF];
                }
            });
        }

        var unitLength = casePolicy == DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase ? 6 : 4;
        var unitCount = (comparisonKey.Length + unitLength - 1) / unitLength;
        return string.Create(
            comparisonKey.Length + unitCount,
            (comparisonKey, unitLength),
            static (buffer, state) =>
            {
                var target = 0;
                for (var source = 0; source < state.comparisonKey.Length; source += state.unitLength)
                {
                    buffer[target++] = '|';
                    var length = Math.Min(state.unitLength, state.comparisonKey.Length - source);
                    state.comparisonKey.AsSpan(source, length).CopyTo(buffer[target..]);
                    target += length;
                }
            });
    }

    private static PortableStringComparisonPolicy Map(DiagnosticStringCasePolicy casePolicy) => casePolicy switch
    {
        DiagnosticStringCasePolicy.Ordinal => PortableStringComparisonPolicy.Ordinal,
        DiagnosticStringCasePolicy.AsciiIgnoreCase => PortableStringComparisonPolicy.AsciiIgnoreCase,
        DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase => PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(casePolicy), casePolicy, null)
    };
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
                DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase => StringComparer.Ordinal.Compare(
                    DiagnosticStringComparisonKey.CreateUnicodeOrdinalIgnoreCase(CanonicalValue),
                    DiagnosticStringComparisonKey.CreateUnicodeOrdinalIgnoreCase(other.CanonicalValue)),
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
