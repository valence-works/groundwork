using Groundwork.Core.PhysicalStorage;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Sqlite.PhysicalStorage;

/// <summary>Exact SQLite encodings for portable values whose native affinities are lossy.</summary>
internal static class SqlitePhysicalValueConverter
{
    public const int MaximumDecimalPrecision = 18;

    public static void Validate(ProjectedColumnDefinition definition)
    {
        if (definition.Type != PortablePhysicalType.Decimal)
            return;
        if (definition.Precision is null || definition.Scale is null)
            throw new InvalidOperationException("SQLite Decimal projections require declared precision and scale.");
        if (definition.Precision is < 1 or > MaximumDecimalPrecision ||
            definition.Scale is < 0 || definition.Scale > definition.Precision)
        {
            throw new InvalidOperationException(
                $"SQLite Decimal projections support precision 1..{MaximumDecimalPrecision} and scale 0..precision.");
        }
    }

    public static object? ToStorage(object? value, ProjectedColumnDefinition definition)
    {
        Validate(definition);
        if (value is null)
            return null;
        return definition.Type switch
        {
            PortablePhysicalType.Decimal => ToScaledInteger((decimal)value, definition),
            PortablePhysicalType.DateTime => ((DateTimeOffset)value).UtcDateTime.Ticks,
            _ => value
        };
    }

    public static object FromQuery(
        string value,
        Groundwork.Core.Indexing.IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        ToStorage(
            RelationalPhysicalProjectionValues.ConvertScalar(value, valueKind, definition),
            definition)!;

    public static long DefaultLiteral(ProjectedColumnDefinition definition) =>
        Convert.ToInt64(ToStorage(
            RelationalPhysicalProjectionValues.ConvertScalar(definition.DefaultValue!, definition),
            definition));

    private static long ToScaledInteger(decimal value, ProjectedColumnDefinition definition)
    {
        var precision = definition.Precision!.Value;
        var scale = definition.Scale!.Value;
        var factor = Pow10(scale);
        var scaled = checked(value * factor);
        if (scaled != decimal.Truncate(scaled))
            throw new InvalidDataException(
                $"Decimal value '{value}' exceeds declared scale {scale} for '{definition.LogicalName}'.");
        if (decimal.Abs(scaled) >= Pow10(precision))
            throw new InvalidDataException(
                $"Decimal value '{value}' exceeds declared precision {precision} for '{definition.LogicalName}'.");
        try
        {
            return decimal.ToInt64(scaled);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"Decimal value '{value}' cannot be represented exactly by SQLite for '{definition.LogicalName}'.",
                exception);
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++)
            result *= 10m;
        return result;
    }
}
