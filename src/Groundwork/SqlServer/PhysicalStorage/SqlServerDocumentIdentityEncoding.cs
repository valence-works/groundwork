namespace Groundwork.SqlServer.PhysicalStorage;

internal static class SqlServerDocumentIdentityEncoding
{
    public const int MaximumOriginalCodeUnits = 450;
    public const int MaximumComparisonBytes = 1350;
    public const int LookupBytes = 32;

    public static string Original(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumOriginalCodeUnits)
        {
            throw new ArgumentException(
                $"SQL Server document identities may contain at most {MaximumOriginalCodeUnits} UTF-16 code units.",
                nameof(value));
        }
        return value;
    }

    public static byte[] Comparison(string value) => Hex(value, MaximumComparisonBytes, exactLength: false);

    public static byte[] Lookup(string value) => Hex(value, LookupBytes, exactLength: true);

    public static byte[]? ComparisonPrefixUpperBound(string value)
    {
        var lower = Comparison(value);
        for (var index = lower.Length - 1; index >= 0; index--)
        {
            if (lower[index] == byte.MaxValue)
                continue;
            var upper = lower[..(index + 1)];
            upper[index]++;
            return upper;
        }
        return null;
    }

    public static string ReadComparison(object value) =>
        Convert.ToHexString(RequireBytes(value, MaximumComparisonBytes, exactLength: false));

    public static string ReadLookup(object value) =>
        Convert.ToHexStringLower(RequireBytes(value, LookupBytes, exactLength: true));

    public static bool ValueEquals(object retained, object expected) =>
        retained is byte[] retainedBytes && expected is byte[] expectedBytes
            ? retainedBytes.AsSpan().SequenceEqual(expectedBytes)
            : object.Equals(retained, expected);

    private static byte[] Hex(string value, int maximumLength, bool exactLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Document identity evidence must be canonical hexadecimal text.", nameof(value), exception);
        }
        if (exactLength ? bytes.Length != maximumLength : bytes.Length > maximumLength)
        {
            throw new ArgumentException(
                exactLength
                    ? $"Document identity evidence must contain exactly {maximumLength} bytes."
                    : $"Document identity evidence may contain at most {maximumLength} bytes.",
                nameof(value));
        }
        return bytes;
    }

    private static byte[] RequireBytes(object value, int maximumLength, bool exactLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not byte[] bytes)
            throw new InvalidOperationException("SQL Server returned non-binary document identity evidence.");
        if (exactLength ? bytes.Length != maximumLength : bytes.Length > maximumLength)
        {
            throw new InvalidOperationException(
                exactLength
                    ? $"SQL Server returned document identity evidence that was not exactly {maximumLength} bytes."
                    : $"SQL Server returned document identity evidence longer than {maximumLength} bytes.");
        }
        return bytes;
    }
}
