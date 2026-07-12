using System.Globalization;

namespace Groundwork.Relational.Physicalization;

/// <summary>Exact base-ten lexical value used before any CLR numeric conversion can round it.</summary>
internal sealed class ExactNumericLiteral
{
    private const string DecimalMaximumCoefficient = "79228162514264337593543950335";

    private ExactNumericLiteral(string original, bool isNegative, string digits, int fractionalDigits, int exponent)
    {
        Original = original;
        IsNegative = isNegative;
        Digits = digits.TrimStart('0') is { Length: > 0 } significant ? significant : "0";
        FractionalDigits = fractionalDigits;
        Exponent = exponent;
    }

    private string Original { get; }
    private bool IsNegative { get; }
    private string Digits { get; }
    private int FractionalDigits { get; }
    private int Exponent { get; }

    public static ExactNumericLiteral Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
            throw new FormatException("A numeric value cannot be empty.");

        var index = 0;
        var isNegative = false;
        if (text[index] is '+' or '-')
        {
            isNegative = text[index] == '-';
            index++;
        }

        var integerStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
            index++;
        var integerEnd = index;

        var fractionStart = index;
        var fractionEnd = index;
        if (index < text.Length && text[index] == '.')
        {
            fractionStart = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
                index++;
            fractionEnd = index;
        }

        if (integerEnd == integerStart && fractionEnd == fractionStart)
            throw new FormatException($"Numeric value '{value}' has no digits.");

        var exponent = 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            var exponentIsNegative = false;
            if (index < text.Length && text[index] is '+' or '-')
            {
                exponentIsNegative = text[index] == '-';
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
            if (exponentIsNegative)
                exponent = checked(-exponent);
        }

        if (index != text.Length)
            throw new FormatException($"Numeric value '{value}' contains unsupported characters.");

        var integerDigits = text[integerStart..integerEnd].ToString();
        var fractionDigits = text[fractionStart..fractionEnd].ToString();
        return new ExactNumericLiteral(
            value,
            isNegative,
            integerDigits + fractionDigits,
            fractionDigits.Length,
            exponent);
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

    public decimal ToDecimal()
    {
        if (Digits == "0")
            return 0m;

        var coefficient = Digits;
        long scale = (long)FractionalDigits - Exponent;
        while (scale > 0 && coefficient[^1] == '0')
        {
            coefficient = coefficient[..^1];
            scale--;
        }

        if (scale < 0)
        {
            var appendedZeros = -scale;
            if (coefficient.Length + appendedZeros > DecimalMaximumCoefficient.Length)
                throw DecimalOverflow();
            coefficient += new string('0', (int)appendedZeros);
            scale = 0;
        }

        if (scale > 28 || ExceedsDecimalMaximum(coefficient))
            throw DecimalOverflow();
        return DecimalFromCoefficient(coefficient, (int)scale);
    }

    public decimal ToDecimal(int precision, int scale, string logicalName)
    {
        if (precision is < 1 or > 28 || scale < 0 || scale > precision)
            throw new InvalidOperationException("A Decimal projection requires precision 1..28 and scale 0..precision.");
        var coefficient = ToScaledDigits(precision, scale, logicalName);
        return DecimalFromCoefficient(coefficient, scale);
    }

    private string ToIntegerDigits(int maximumDigits)
    {
        if (Digits == "0")
            return "0";
        var coefficient = Digits;
        long shift = (long)Exponent - FractionalDigits;
        if (shift < 0)
        {
            var removedDigits = -shift;
            if (removedDigits > coefficient.Length ||
                coefficient.AsSpan(coefficient.Length - (int)removedDigits).ContainsAnyExcept('0'))
            {
                throw IntegerOverflow();
            }
            coefficient = coefficient[..^(int)removedDigits];
        }
        else if (shift > 0)
        {
            if (coefficient.Length + shift > maximumDigits)
                throw IntegerOverflow();
            coefficient += new string('0', (int)shift);
        }

        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length == 0)
            return "0";
        if (coefficient.Length > maximumDigits)
            throw IntegerOverflow();
        return coefficient;
    }

    private string ToScaledDigits(int precision, int scale, string logicalName)
    {
        if (Digits == "0")
            return "0";
        var coefficient = Digits;
        long shift = (long)Exponent - FractionalDigits + scale;
        if (shift < 0)
        {
            var removedDigits = -shift;
            if (removedDigits > coefficient.Length ||
                coefficient.AsSpan(coefficient.Length - (int)removedDigits).ContainsAnyExcept('0'))
            {
                throw new InvalidDataException(
                    $"Decimal value '{Original}' exceeds declared scale {scale} for '{logicalName}'.");
            }
            coefficient = coefficient[..^(int)removedDigits];
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
        if (coefficient.Length == 0)
            return "0";
        if (coefficient.Length > precision)
        {
            throw new InvalidDataException(
                $"Decimal value '{Original}' exceeds declared precision {precision} for '{logicalName}'.");
        }
        return coefficient;
    }

    private decimal DecimalFromCoefficient(string coefficient, int scale)
    {
        var decimalPoint = coefficient.Length - scale;
        var text = decimalPoint switch
        {
            <= 0 => $"0.{new string('0', -decimalPoint)}{coefficient}",
            _ when scale == 0 => coefficient,
            _ => $"{coefficient[..decimalPoint]}.{coefficient[decimalPoint..]}"
        };
        return decimal.Parse(Signed(text), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private string Signed(string value) => IsNegative && value != "0" ? $"-{value}" : value;

    private static bool ExceedsDecimalMaximum(string coefficient) =>
        coefficient.Length > DecimalMaximumCoefficient.Length ||
        coefficient.Length == DecimalMaximumCoefficient.Length &&
        string.CompareOrdinal(coefficient, DecimalMaximumCoefficient) > 0;

    private OverflowException IntegerOverflow() =>
        new($"Numeric value '{Original}' is not an exact integer in the requested range.");

    private OverflowException DecimalOverflow() =>
        new($"Numeric value '{Original}' cannot be represented exactly as Decimal.");
}
