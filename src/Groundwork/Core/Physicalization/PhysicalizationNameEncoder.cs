using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Core.Physicalization;

public static class PhysicalizationNameEncoder
{
    private const int HashLength = 12;

    public static string Encode(string value, int? maxLength = null)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";

        var readable = ReadableSlug(value);
        var suffix = StableHash(value);
        var encoded = $"{readable}_{suffix}";
        if (maxLength is null || encoded.Length <= maxLength.Value)
            return encoded;

        if (maxLength.Value <= HashLength + 1)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum length must leave room for a readable prefix and hash suffix.");

        var prefixLength = maxLength.Value - suffix.Length - 1;
        var prefix = readable[..Math.Min(readable.Length, prefixLength)].TrimEnd('_');
        if (prefix.Length == 0)
            prefix = readable[..Math.Min(readable.Length, prefixLength)];

        return $"{prefix}_{suffix}";
    }

    private static string ReadableSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && builder.Length > 0 && IsLowercaseLetterOrDigit(builder[^1]))
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }

        var slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "value" : slug;
    }

    private static bool IsLowercaseLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string StableHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..HashLength];
    }
}
