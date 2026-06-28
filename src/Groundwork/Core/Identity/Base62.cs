namespace Groundwork.Core.Identity;

/// <summary>
/// Fixed-width Base62 encoder shared by the time-ordered generators. The alphabet is in ascending
/// ASCII order (digits, then upper-case, then lower-case), and <see cref="Encode"/> always returns
/// exactly 11 characters. Fixed width plus an ascending alphabet means ordinal string comparison of
/// two encoded values matches the numeric order of the underlying <see cref="ulong"/>; 11 is the
/// smallest width that holds the full <see cref="ulong"/> range (62^11 &gt; 2^64).
/// </summary>
internal static class Base62
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>The fixed length of every encoded value.</summary>
    public const int Length = 11;

    public static string Encode(ulong value)
    {
        Span<char> buffer = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(value % 62)];
            value /= 62;
        }

        return new string(buffer);
    }
}
