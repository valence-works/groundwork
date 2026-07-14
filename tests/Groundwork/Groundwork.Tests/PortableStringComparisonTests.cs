using System.Text;
using Groundwork.Core.Text;
using Xunit;

namespace Groundwork.Tests;

public sealed class PortableStringComparisonTests
{
    [Theory]
    [InlineData("API-Z9", "api-z9")]
    [InlineData("already lower", "already lower")]
    [InlineData("[]_@", "[]_@")]
    public void Ascii_ignore_case_uses_a_versioned_culture_independent_comparison_key(
        string value,
        string expected)
    {
        Assert.Equal("groundwork-ascii-lower-v1", PortableStringComparison.AsciiIgnoreCaseAlgorithmId);
        Assert.Equal(expected, PortableStringComparison.CreateAsciiIgnoreCase(value));
    }

    [Theory]
    [InlineData("Å")]
    [InlineData("İ")]
    [InlineData("ß")]
    [InlineData("é")]
    [InlineData("line\nbreak")]
    public void Ascii_ignore_case_rejects_non_portable_unicode_and_control_values(string value)
    {
        Assert.False(PortableStringComparison.IsAsciiIgnoreCaseValue(value));
        Assert.Throws<ArgumentException>(() => PortableStringComparison.CreateAsciiIgnoreCase(value));
    }

    [Theory]
    [InlineData("å", "Å")]
    [InlineData("ς", "σ")]
    [InlineData("\U00010428", "\U00010400")]
    [InlineData("i", "I")]
    public void Unicode_ordinal_ignore_case_uses_a_versioned_provider_neutral_key(
        string left,
        string right)
    {
        var leftKey = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(left);
        var rightKey = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(right);

        Assert.StartsWith(
            "groundwork-unicode-ordinal-ignore-case-v1-",
            PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId);
        Assert.Equal(leftKey, rightKey);
        Assert.Equal(
            Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(left, "z")),
            Math.Sign(StringComparer.Ordinal.Compare(
                leftKey,
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase("z"))));
    }

    [Fact]
    public void Unicode_key_matches_dotnet_ordinal_ignore_case_for_case_fold_boundaries()
    {
        string[] values =
        [
            "i", "I", "İ", "ı", "ß", "ẞ", "ss", "Σ", "σ", "ς", "K", "K", "k",
            "ﬀ", "FF", "é", "É", "e\u0301", "ſ", "\U00010D70", "\U00010D50",
            "\U00016EBB", "\U00016EA0", "\uE000", "\U00010000",
            "\U00010400", "\U00010428", "😀", "\0"
        ];

        foreach (var left in values)
            foreach (var right in values)
            {
                var expected = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                var actual = StringComparer.Ordinal.Compare(
                    PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(left),
                    PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(right));
                Assert.True(
                    Math.Sign(expected) == Math.Sign(actual),
                    $"Ordinal-ignore-case order differs for U+{string.Join(" U+", left.EnumerateRunes().Select(rune => rune.Value.ToString("X4")))} and U+{string.Join(" U+", right.EnumerateRunes().Select(rune => rune.Value.ToString("X4")))}: expected {expected}, actual {actual}.");
            }
    }

    [Fact]
    public void Unicode_key_exhaustively_guards_runtime_uppercase_mappings_with_ordinal_ignore_case()
    {
        const int chunkSize = 65_536;
        var nonIdentityMappings = new List<(Rune Source, Rune Upper)>();
        for (var start = 0; start <= 0x10FFFF; start += chunkSize)
        {
            var source = new StringBuilder();
            var expected = new StringBuilder();
            var end = Math.Min(0x10FFFF, start + chunkSize - 1);
            for (var scalar = start; scalar <= end; scalar++)
            {
                if (!Rune.IsValid(scalar))
                    continue;
                var rune = new Rune(scalar);
                var upper = Rune.ToUpperInvariant(rune);
                source.Append(rune);
                expected.Append(StringComparer.OrdinalIgnoreCase.Equals(rune.ToString(), upper.ToString())
                    ? upper
                    : rune);
                if (rune != upper)
                    nonIdentityMappings.Add((rune, upper));
            }

            Assert.Equal(
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(expected.ToString()),
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(source.ToString()));
        }

        foreach (var (source, upper) in nonIdentityMappings)
        {
            Assert.Equal(
                Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(source.ToString(), upper.ToString())),
                Math.Sign(StringComparer.Ordinal.Compare(
                    PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(source.ToString()),
                    PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(upper.ToString()))));
        }

        Assert.False(StringComparer.OrdinalIgnoreCase.Equals("ſ", "S"));
        Assert.NotEqual(
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase("ſ"),
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase("S"));
    }

    [Fact]
    public void Bounded_keys_preserve_exact_identity_order()
    {
        var value = new string('Å', 448) + "😀";
        var comparison = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value);
        var projection = PortableStringComparison.ProjectIdentity(
            value,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var prefix = PortableStringComparison.CreateBoundedPrefix(comparison, 256);

        Assert.Equal(value, projection.OriginalValue);
        Assert.Equal(comparison, projection.ComparisonKey);
        Assert.Equal(projection.LookupKey, projection.ComparisonKeyHash);
        Assert.Equal(256, prefix.Length);
        Assert.Equal(comparison[..256], prefix);
        Assert.Equal(64, projection.ComparisonKeyHash.Length);
        Assert.Equal(
            PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
            projection.ComparisonAlgorithmId);
        Assert.Equal(
            PortableStringComparison.LookupHashAlgorithmId,
            projection.LookupAlgorithmId);
        Assert.Equal("groundwork-sha256-utf8-lowerhex-v1", PortableStringComparison.LookupHashAlgorithmId);
    }

    [Theory]
    [InlineData(PortableStringComparisonPolicy.Ordinal)]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase)]
    public void Document_identity_projection_rejects_values_beyond_the_portable_boundary(
        PortableStringComparisonPolicy policy)
    {
        Assert.Equal(450, PortableStringComparison.MaximumIdentityCodeUnits);
        Assert.Throws<ArgumentException>(() => PortableStringComparison.ProjectIdentity(new string('x', 451), policy));
    }
}
