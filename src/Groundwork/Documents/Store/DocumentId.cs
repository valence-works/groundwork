using System.Text;

namespace Groundwork.Documents.Store;

public static class DocumentId
{
    private const char Separator = ':';
    private const char EscapeMarker = '%';

    public static string Compose(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Length == 0)
            throw new ArgumentException("At least one document id part is required.", nameof(parts));

        return string.Join(Separator, parts.Select(Escape));
    }
    public static IReadOnlyList<string> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var parts))
            throw new FormatException("The composite document id contains an invalid percent-escape sequence.");

        return parts;
    }

    public static bool TryParse(string? value, out IReadOnlyList<string> parts)
    {
        parts = Array.Empty<string>();
        if (value is null)
            return false;

        var result = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == Separator)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (character == EscapeMarker)
            {
                if (i + 2 >= value.Length || !TryReadHex(value[i + 1], value[i + 2], out var escaped))
                    return false;

                current.Append(escaped);
                i += 2;
                continue;
            }

            current.Append(character);
        }

        result.Add(current.ToString());
        parts = result;
        return true;
    }

    private static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(":", "%3A", StringComparison.Ordinal);
    }

    private static bool TryReadHex(char high, char low, out char value)
    {
        value = default;
        if (!TryHexValue(high, out var highValue) || !TryHexValue(low, out var lowValue))
            return false;

        value = (char)((highValue << 4) + lowValue);
        return true;
    }

    private static bool TryHexValue(char character, out int value)
    {
        if (character is >= '0' and <= '9')
        {
            value = character - '0';
            return true;
        }

        if (character is >= 'A' and <= 'F')
        {
            value = character - 'A' + 10;
            return true;
        }

        if (character is >= 'a' and <= 'f')
        {
            value = character - 'a' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}
