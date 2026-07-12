using Groundwork.Core.PhysicalStorage;
using Groundwork.Relational.Physicalization;
using Xunit;

namespace Groundwork.Tests;

public sealed class RelationalPhysicalProjectionValuesTests
{
    [Fact]
    public void Exact_numeric_parser_accepts_representable_positive_and_negative_exponents()
    {
        Assert.Equal(100, RelationalPhysicalProjectionValues.ConvertScalar("1e2", PortablePhysicalType.Int32));
        Assert.Equal(long.MinValue, RelationalPhysicalProjectionValues.ConvertScalar(
            "-9.223372036854775808e18",
            PortablePhysicalType.Int64));
        Assert.Equal(0.0000000000000000000000000001m, RelationalPhysicalProjectionValues.ParseNumber("1e-28"));
        Assert.Equal(-125m, RelationalPhysicalProjectionValues.ParseNumber("-1.25e2"));
    }

    [Fact]
    public void Exact_integer_parser_preserves_adjacent_boundaries()
    {
        Assert.Equal(int.MaxValue, RelationalPhysicalProjectionValues.ConvertScalar("2147483647", PortablePhysicalType.Int32));
        Assert.Equal(int.MinValue, RelationalPhysicalProjectionValues.ConvertScalar("-2147483648", PortablePhysicalType.Int32));
        Assert.Equal(long.MaxValue, RelationalPhysicalProjectionValues.ConvertScalar("9223372036854775807", PortablePhysicalType.Int64));
        Assert.Equal(long.MinValue, RelationalPhysicalProjectionValues.ConvertScalar("-9223372036854775808", PortablePhysicalType.Int64));
        Assert.Throws<InvalidDataException>(() =>
            RelationalPhysicalProjectionValues.ConvertScalar("9223372036854775808", PortablePhysicalType.Int64));
    }

    [Fact]
    public void Declared_decimal_shape_is_proved_from_the_original_exponent_lexeme()
    {
        var definition = new ProjectedColumnDefinition(
            "value",
            "value",
            PortablePhysicalType.Decimal,
            Precision: 18,
            Scale: 4);

        Assert.Equal(99999999999999.9999m, RelationalPhysicalProjectionValues.ConvertScalar(
            "9.99999999999999999e13",
            definition));
        Assert.Equal(-99999999999999.9999m, RelationalPhysicalProjectionValues.ConvertScalar(
            "-9.99999999999999999e13",
            definition));
        Assert.Throws<InvalidDataException>(() => RelationalPhysicalProjectionValues.ConvertScalar(
            "99999999999999.99990000000000001",
            definition));
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData("0.00000000000000000000000000004")]
    [InlineData("99999999999999.99990000000000001")]
    [InlineData("79228162514264337593543950336")]
    public void Exact_numeric_parser_rejects_values_that_decimal_would_round_or_overflow(string value)
    {
        Assert.Throws<OverflowException>(() => RelationalPhysicalProjectionValues.ParseNumber(value));
    }

    [Theory]
    [InlineData(PortablePhysicalType.Int32)]
    [InlineData(PortablePhysicalType.Int64)]
    public void Exact_integer_parser_rejects_nonzero_underflow(PortablePhysicalType type)
    {
        Assert.Throws<InvalidDataException>(() => RelationalPhysicalProjectionValues.ConvertScalar("1e-29", type));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData(".")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1.2.3")]
    [InlineData("1_000")]
    public void Exact_numeric_parser_rejects_malformed_forms(string value)
    {
        Assert.Throws<FormatException>(() => RelationalPhysicalProjectionValues.ParseNumber(value));
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00.00000001Z")]
    [InlineData("2026-01-01T00:00:00.00000014Z")]
    [InlineData("2026-01-01T00:00:00.00000015Z")]
    [InlineData("2026-01-01T00:00:00,00000015Z")]
    [InlineData("2025-12-31T19:00:00.00000015-05:00")]
    public void DateTime_parser_rejects_sub_tick_fractional_seconds(string value)
    {
        Assert.Throws<FormatException>(() => RelationalPhysicalProjectionValues.ParseDateTime(value));
    }

    [Fact]
    public void DateTime_parser_preserves_exact_ticks_and_equivalent_offsets()
    {
        var utc = RelationalPhysicalProjectionValues.ParseDateTime("2026-01-01T00:00:00.0000001Z");
        var offset = RelationalPhysicalProjectionValues.ParseDateTime("2025-12-31T19:00:00.0000001-05:00");

        Assert.Equal(utc.UtcTicks, offset.UtcTicks);
    }
}
