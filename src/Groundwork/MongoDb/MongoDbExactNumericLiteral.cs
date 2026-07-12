using System.Globalization;

namespace Groundwork.MongoDb;

/// <summary>Provider-local exact decimal lexeme used before BSON numeric conversion can round it.</summary>
internal sealed class MongoDbExactNumericLiteral
{
    private MongoDbExactNumericLiteral(string original, bool negative, string digits, int fraction, int exponent)
    {
        Original = original;
        Negative = negative;
        Digits = digits.TrimStart('0') is { Length: > 0 } significant ? significant : "0";
        Fraction = fraction;
        Exponent = exponent;
    }

    private string Original { get; }
    private bool Negative { get; }
    private string Digits { get; }
    private int Fraction { get; }
    private int Exponent { get; }

    public static MongoDbExactNumericLiteral Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
            throw new FormatException("A numeric value cannot be empty.");

        var index = 0;
        var negative = false;
        if (text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            index++;
        }

        var integerStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
        var integerEnd = index;
        var fractionStart = index;
        var fractionEnd = index;
        if (index < text.Length && text[index] == '.')
        {
            fractionStart = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            fractionEnd = index;
        }
        if (integerStart == integerEnd && fractionStart == fractionEnd)
            throw new FormatException($"Numeric value '{value}' has no digits.");

        var exponent = 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = false;
            if (index < text.Length && text[index] is '+' or '-')
            {
                exponentNegative = text[index] == '-';
                index++;
            }
            var exponentStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                exponent = checked(exponent * 10 + text[index] - '0');
                index++;
            }
            if (index == exponentStart)
                throw new FormatException($"Numeric value '{value}' has no exponent digits.");
            if (exponentNegative) exponent = checked(-exponent);
        }
        if (index != text.Length)
            throw new FormatException($"Numeric value '{value}' contains unsupported characters.");

        var integers = text[integerStart..integerEnd].ToString();
        var fractions = text[fractionStart..fractionEnd].ToString();
        return new MongoDbExactNumericLiteral(value, negative, integers + fractions, fractions.Length, exponent);
    }

    public int ToInt32() => int.TryParse(
        Signed(ToIntegerDigits(10)),
        NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture,
        out var value)
        ? value
        : throw IntegerOverflow();

    public long ToInt64() => long.TryParse(
        Signed(ToIntegerDigits(19)),
        NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture,
        out var value)
        ? value
        : throw IntegerOverflow();

    public string ValidateDecimal(int precision, int scale, string logicalName)
    {
        if (precision is < 1 or > 28 || scale < 0 || scale > precision)
            throw new InvalidOperationException("A Decimal projection requires precision 1..28 and scale 0..precision.");
        if (Digits == "0") return "0";

        var coefficient = Digits;
        var shift = (long)Exponent - Fraction + scale;
        if (shift < 0)
        {
            var removed = -shift;
            if (removed > coefficient.Length ||
                coefficient.AsSpan(coefficient.Length - (int)removed).ContainsAnyExcept('0'))
            {
                throw new InvalidDataException(
                    $"Decimal value '{Original}' exceeds declared scale {scale} for '{logicalName}'.");
            }
            coefficient = coefficient[..^(int)removed];
        }
        else if (shift > 0)
        {
            if (coefficient.Length + shift > precision)
            {
                throw new InvalidDataException(
                    $"Decimal value '{Original}' exceeds declared precision {precision} for '{logicalName}'.");
            }
            coefficient += new string('0', (int)shift);
        }

        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length > precision)
        {
            throw new InvalidDataException(
                $"Decimal value '{Original}' exceeds declared precision {precision} for '{logicalName}'.");
        }
        return Original.Trim();
    }

    private string ToIntegerDigits(int maximumDigits)
    {
        if (Digits == "0") return "0";
        var coefficient = Digits;
        var shift = (long)Exponent - Fraction;
        if (shift < 0)
        {
            var removed = -shift;
            if (removed > coefficient.Length ||
                coefficient.AsSpan(coefficient.Length - (int)removed).ContainsAnyExcept('0'))
            {
                throw IntegerOverflow();
            }
            coefficient = coefficient[..^(int)removed];
        }
        else if (shift > 0)
        {
            if (coefficient.Length + shift > maximumDigits) throw IntegerOverflow();
            coefficient += new string('0', (int)shift);
        }

        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length == 0) return "0";
        if (coefficient.Length > maximumDigits) throw IntegerOverflow();
        return coefficient;
    }

    private string Signed(string value) => Negative && value != "0" ? $"-{value}" : value;

    private OverflowException IntegerOverflow() =>
        new($"Numeric value '{Original}' is not an exact integer in the requested range.");
}
