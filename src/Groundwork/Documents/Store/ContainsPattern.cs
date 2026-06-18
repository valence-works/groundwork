namespace Groundwork.Documents.Store;

/// <summary>
/// Builds the <c>LIKE</c> pattern for a case-insensitive Contains comparison, escaping the LIKE
/// wildcard characters so the supplied value is matched literally as a substring.
/// </summary>
public static class ContainsPattern
{
    public const char EscapeCharacter = '\\';

    public static string Build(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
