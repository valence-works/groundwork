using System.Globalization;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Relational.Physicalization;

/// <summary>
/// Converts authoritative canonical JSON into portable typed projection values. Live maintenance
/// and provider backfills use this one conversion boundary so they cannot produce different rows.
/// </summary>
public static class RelationalPhysicalProjectionValues
{
    public static IReadOnlyDictionary<string, object?> Read(
        string canonicalJson,
        IReadOnlyList<ExecutableProjectedColumnRoute> columns)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        ArgumentNullException.ThrowIfNull(columns);
        foreach (var column in columns)
            CanonicalCollectionElementProjection.RequireScalar(column.Definition);
        using var document = JsonDocument.Parse(canonicalJson);
        return columns.ToDictionary(
            column => column.Definition.LogicalName,
            column => Read(document.RootElement, column.Definition),
            StringComparer.Ordinal);
    }

    public static object ConvertScalar(string value, PortablePhysicalType type)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return type switch
            {
                PortablePhysicalType.String or PortablePhysicalType.Json => value,
                PortablePhysicalType.Int32 => ExactNumericLiteral.Parse(value).ToInt32(),
                PortablePhysicalType.Int64 => ExactNumericLiteral.Parse(value).ToInt64(),
                PortablePhysicalType.Decimal => ParseNumber(value),
                PortablePhysicalType.Boolean => bool.Parse(value),
                PortablePhysicalType.DateTime => ParseDateTime(value),
                PortablePhysicalType.Guid => Guid.Parse(value),
                PortablePhysicalType.Binary => Convert.FromBase64String(value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException($"Query value cannot be converted to '{type}'.", exception);
        }
    }

    public static object ConvertScalar(string value, ProjectedColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            var converted = definition.Type == PortablePhysicalType.Decimal
                ? ExactNumericLiteral.Parse(value).ToDecimal(
                    definition.Precision ?? throw new InvalidOperationException("A Decimal projection requires declared precision."),
                    definition.Scale ?? throw new InvalidOperationException("A Decimal projection requires declared scale."),
                    definition.LogicalName)
                : ConvertScalar(value, definition.Type);
            if (converted is string text && definition.Type == PortablePhysicalType.String)
                PhysicalProjectionValueValidation.ValidateStringLength(text, definition);
            return converted;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException($"Value cannot be converted to projected column '{definition.LogicalName}'.", exception);
        }
    }

    public static object ConvertScalar(string value, IndexValueKind valueKind)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return valueKind switch
            {
                IndexValueKind.String or IndexValueKind.Keyword => value,
                IndexValueKind.Number => ParseNumber(value),
                IndexValueKind.Boolean => bool.Parse(value),
                IndexValueKind.DateTime => ParseDateTime(value),
                _ => throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, null)
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException($"Query value cannot be converted to '{valueKind}'.", exception);
        }
    }

    public static object ConvertScalar(
        string value,
        IndexValueKind valueKind,
        PortablePhysicalType physicalType)
    {
        if (!PortableQueryOperationCompatibility.Supports(valueKind, physicalType))
        {
            throw new InvalidOperationException(
                $"Logical value kind '{valueKind}' cannot be bound to projected physical type '{physicalType}' without changing query semantics.");
        }

        return ConvertScalar(value, physicalType);
    }

    public static object ConvertScalar(
        string value,
        IndexValueKind valueKind,
        ProjectedColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!PortableQueryOperationCompatibility.Supports(valueKind, definition.Type))
        {
            throw new InvalidOperationException(
                $"Logical value kind '{valueKind}' cannot be bound to projected physical type '{definition.Type}' without changing query semantics.");
        }
        return ConvertScalar(value, definition);
    }

    public static decimal ParseNumber(string value) =>
        ExactNumericLiteral.Parse(value).ToDecimal();

    public static DateTimeOffset ParseDateTime(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        var timeSeparator = text.IndexOf('T');
        if (timeSeparator < 0)
            timeSeparator = text.IndexOf('t');
        var offsetSeparator = timeSeparator < 0
            ? -1
            : text.IndexOfAny(['+', '-'], timeSeparator + 1);
        if (!text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) && offsetSeparator < 0)
            throw new FormatException("A DateTime value requires an explicit UTC designator or numeric offset.");
        var fractionalSeparator = timeSeparator < 0 ? -1 : text.IndexOfAny(['.', ','], timeSeparator + 1);
        if (fractionalSeparator >= 0)
        {
            var fractionalEnd = offsetSeparator >= 0
                ? offsetSeparator
                : text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ? text.Length - 1 : text.Length;
            var fractionalDigits = fractionalEnd - fractionalSeparator - 1;
            if (fractionalDigits > 7)
                throw new FormatException("A DateTime value cannot exceed 100ns fractional-second precision.");
        }
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static object? Read(JsonElement root, ProjectedColumnDefinition definition)
    {
        if (!RelationalPhysicalizationValues.TryGetPropertyPath(root, definition.Path, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (definition.DefaultValue is not null)
                return ConvertScalar(definition.DefaultValue, definition);
            if (definition.IsNullable)
                return null;
            throw new InvalidDataException(
                $"Canonical JSON path '{definition.Path}' is required for projected column '{definition.LogicalName}'.");
        }

        try
        {
            var value = definition.Type switch
            {
                PortablePhysicalType.String => element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : RelationalPhysicalizationValues.NormalizeValue(element),
                PortablePhysicalType.Int32 or PortablePhysicalType.Int64 or PortablePhysicalType.Decimal =>
                    ConvertScalar(NumberText(element), definition),
                PortablePhysicalType.Boolean => ReadBoolean(element),
                PortablePhysicalType.DateTime => ParseDateTime(element.GetString()!),
                PortablePhysicalType.Guid => element.GetGuid(),
                PortablePhysicalType.Binary => element.GetBytesFromBase64(),
                PortablePhysicalType.Json => element.GetRawText(),
                _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
            };
            if (value is string text && definition.Type == PortablePhysicalType.String)
                PhysicalProjectionValueValidation.ValidateStringLength(text, definition);
            return value;
        }
        catch (Exception exception) when (exception is (FormatException or InvalidOperationException or OverflowException) &&
                                         exception is not PhysicalProjectionValueValidationException)
        {
            throw new InvalidDataException(
                $"Canonical JSON path '{definition.Path}' cannot be converted to '{definition.Type}'.",
                exception);
        }
    }

    private static bool ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.Parse(element.GetString()!),
        JsonValueKind.Number when element.TryGetInt32(out var value) && value is 0 or 1 => value == 1,
        _ => throw new FormatException("A Boolean projection requires true, false, 0, or 1.")
    };

    private static string NumberText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.String => element.GetString()!,
        _ => throw new FormatException("A numeric projection requires a JSON number or numeric string.")
    };
}
