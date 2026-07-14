using System.Globalization;

namespace Groundwork.DiagnosticRecords.Relational;

internal static class DiagnosticComparisonKeys
{
    public static string Create(DiagnosticFieldValue value, DiagnosticStringCasePolicy casePolicy) => value.Type switch
    {
        DiagnosticFieldType.String => DiagnosticStringComparisonKey.Create(value.CanonicalValue, casePolicy),
        DiagnosticFieldType.Int64 => Int64(long.Parse(value.CanonicalValue, CultureInfo.InvariantCulture)),
        DiagnosticFieldType.Decimal => Decimal(decimal.Parse(value.CanonicalValue, CultureInfo.InvariantCulture)),
        DiagnosticFieldType.Boolean => value.CanonicalValue == "true" ? "1" : "0",
        DiagnosticFieldType.Timestamp => Timestamp(DateTimeOffset.Parse(
            value.CanonicalValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string Int64(long value) =>
        unchecked((ulong)(value - long.MinValue)).ToString("D20", CultureInfo.InvariantCulture);

    public static string Timestamp(DateTimeOffset value) =>
        value.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);

    private static string Decimal(decimal value)
    {
        if (value == 0)
            return $"1{new string('0', 32)}";

        var absolute = decimal.Abs(value);
        var bits = decimal.GetBits(absolute);
        var scale = (bits[3] >> 16) & 0x7F;
        var coefficient = ((System.Numerics.BigInteger)(uint)bits[2] << 64) |
                          ((System.Numerics.BigInteger)(uint)bits[1] << 32) |
                          (uint)bits[0];
        var digits = coefficient.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        var removed = coefficient.ToString(CultureInfo.InvariantCulture).Length - digits.Length;
        scale -= removed;
        var exponent = digits.Length - scale - 1;
        var magnitude = $"{exponent + 100:D3}{digits.PadRight(29, '0')}";
        if (value > 0)
            return $"2{magnitude}";

        return $"0{new string(magnitude.Select(character => character is >= '0' and <= '9' ? (char)('9' - (character - '0')) : character).ToArray())}";
    }
}
