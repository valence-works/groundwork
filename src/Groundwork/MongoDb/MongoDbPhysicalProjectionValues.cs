using System.Globalization;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

internal readonly record struct MongoDbPhysicalProjectionValue(bool IsPresent, BsonValue Value)
{
    public static MongoDbPhysicalProjectionValue Omitted { get; } = new(false, BsonNull.Value);
}

/// <summary>One typed collection element ready for a provider-owned MongoDB element document.</summary>
internal readonly record struct MongoDbCollectionElementProjectionValue(int Ordinal, BsonValue Value);

/// <summary>
/// One MongoDB projection boundary shared by live writes and canonical backfills. It preserves the
/// distinction between an absent path and explicit JSON null while applying typed defaults.
/// </summary>
internal static class MongoDbPhysicalProjectionValues
{
    public static IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> ResolveAll(
        string canonicalJson,
        IReadOnlyList<ExecutableProjectedColumnRoute> projections)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        foreach (var projection in projections)
            CanonicalCollectionElementProjection.RequireScalar(projection.Definition);
        using var document = JsonDocument.Parse(canonicalJson);
        return projections.ToDictionary(
            projection => projection,
            projection => Resolve(document.RootElement, projection));
    }

    /// <summary>
    /// Materializes collection elements through MongoDB's normal projection conversion boundary.
    /// The caller receives one non-null native value per source ordinal; duplicates are retained
    /// rather than accidentally acquiring MongoDB multikey set semantics.
    /// </summary>
    public static IReadOnlyList<MongoDbCollectionElementProjectionValue> ResolveCollection(
        string canonicalJson,
        ExecutableProjectedColumnRoute projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        ArgumentNullException.ThrowIfNull(projection);
        CanonicalCollectionElementProjection.RequireCollection(projection.Definition);

        return CanonicalCollectionElementProjection.Read(canonicalJson, projection.Definition)
            .Select(element => new MongoDbCollectionElementProjectionValue(
                element.Ordinal,
                ResolveValue(element.Value, projection, collectionElement: true)))
            .ToArray();
    }

    private static MongoDbPhysicalProjectionValue Resolve(
        JsonElement canonical,
        ExecutableProjectedColumnRoute projection)
    {
        if (!TryReadPath(canonical, projection.Definition.Path, out var source))
        {
            if (projection.Definition.DefaultValue is not null)
                return new(true, ParseDefault(projection));
            if (projection.Definition.IsNullable)
                return MongoDbPhysicalProjectionValue.Omitted;
            throw Invalid(projection, "is required but the canonical JSON path is absent");
        }
        if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!projection.Definition.IsNullable)
                throw Invalid(projection, "is required but the canonical JSON value is null");
            return new(true, BsonNull.Value);
        }

        return new(true, ResolveValue(source, projection, collectionElement: false));
    }

    private static BsonValue ResolveValue(
        JsonElement source,
        ExecutableProjectedColumnRoute projection,
        bool collectionElement)
    {
        try
        {
            if (source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new FormatException("A collection-element projection cannot contain JSON null.");
            var value = projection.Definition.Type switch
            {
                PortablePhysicalType.String => new BsonString(source.GetString() ?? throw new FormatException(
                    "A string collection-element projection requires a JSON string.")),
                PortablePhysicalType.Int32 => new BsonInt32(MongoDbExactNumericLiteral.Parse(NumberText(source, collectionElement)).ToInt32()),
                PortablePhysicalType.Int64 => new BsonInt64(MongoDbExactNumericLiteral.Parse(NumberText(source, collectionElement)).ToInt64()),
                PortablePhysicalType.Decimal => Decimal(projection, NumberText(source, collectionElement)),
                PortablePhysicalType.Boolean => new BsonBoolean(source.GetBoolean()),
                PortablePhysicalType.DateTime or PortablePhysicalType.Guid or PortablePhysicalType.Binary =>
                    new BsonString(source.GetString() ?? throw new FormatException(
                        "A string-backed collection-element projection requires a JSON string.")),
                PortablePhysicalType.Json => BsonDocument.Parse($"{{\"value\":{source.GetRawText()}}}")["value"],
                _ => throw new ArgumentOutOfRangeException(nameof(projection), projection.Definition.Type, null)
            };
            return Normalize(projection, value);
        }
        catch (Exception exception) when (exception is (FormatException or OverflowException or InvalidOperationException) &&
                                         exception is not PhysicalProjectionValueValidationException)
        {
            throw Invalid(projection, $"cannot be converted to {projection.Definition.Type}", exception);
        }
    }

    public static void ValidateDefault(ExecutableProjectedColumnRoute projection)
    {
        if (projection.Definition.DefaultValue is not null)
            _ = ParseDefault(projection);
    }

    public static BsonValue ParseQueryValue(
        ExecutableProjectedColumnRoute projection,
        string? value)
    {
        if (value is null)
            return BsonNull.Value;
        try
        {
            var source = projection.Definition.Type switch
            {
                PortablePhysicalType.String or PortablePhysicalType.Guid or PortablePhysicalType.Binary =>
                    new BsonString(value),
                PortablePhysicalType.Int32 => new BsonInt32(MongoDbExactNumericLiteral.Parse(value).ToInt32()),
                PortablePhysicalType.Int64 => new BsonInt64(MongoDbExactNumericLiteral.Parse(value).ToInt64()),
                PortablePhysicalType.Decimal => Decimal(projection, value),
                PortablePhysicalType.Boolean => new BsonBoolean(bool.Parse(value)),
                PortablePhysicalType.DateTime => new BsonString(value),
                PortablePhysicalType.Json => BsonDocument.Parse($"{{\"value\":{value}}}")["value"],
                _ => throw new ArgumentOutOfRangeException(nameof(projection), projection.Definition.Type, null)
            };
            return Normalize(projection, source);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw Invalid(
                projection,
                $"cannot convert query value '{value}' to {projection.Definition.Type}",
                exception);
        }
    }

    private static bool TryReadPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static BsonValue Normalize(ExecutableProjectedColumnRoute projection, BsonValue value)
    {
        try
        {
            return projection.Definition.Type switch
            {
                PortablePhysicalType.String => String(projection, value),
                PortablePhysicalType.Int32 => new BsonInt32(MongoDbExactNumericLiteral.Parse(NumericText(value)).ToInt32()),
                PortablePhysicalType.Int64 => new BsonInt64(MongoDbExactNumericLiteral.Parse(NumericText(value)).ToInt64()),
                PortablePhysicalType.Decimal => Decimal(projection, NumericText(value)),
                PortablePhysicalType.Boolean when value.IsBoolean => value,
                PortablePhysicalType.DateTime => DateTimeTicks(value),
                PortablePhysicalType.Guid when value.IsString => new BsonString(Guid.Parse(value.AsString).ToString("D")),
                PortablePhysicalType.Binary when value.IsString => new BsonBinaryData(Convert.FromBase64String(value.AsString)),
                PortablePhysicalType.Json => value,
                _ => throw new FormatException($"Value '{value}' is not compatible with {projection.Definition.Type}.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw Invalid(projection, $"cannot be converted to {projection.Definition.Type}", exception);
        }
    }

    private static BsonValue ParseDefault(ExecutableProjectedColumnRoute projection)
    {
        var text = projection.Definition.DefaultValue!;
        try
        {
            var source = projection.Definition.Type switch
            {
                PortablePhysicalType.String or PortablePhysicalType.Guid or PortablePhysicalType.Binary => new BsonString(text),
                PortablePhysicalType.Int32 => new BsonInt32(MongoDbExactNumericLiteral.Parse(text).ToInt32()),
                PortablePhysicalType.Int64 => new BsonInt64(MongoDbExactNumericLiteral.Parse(text).ToInt64()),
                PortablePhysicalType.Decimal => Decimal(projection, text),
                PortablePhysicalType.Boolean => new BsonBoolean(bool.Parse(text)),
                PortablePhysicalType.DateTime => new BsonString(text),
                PortablePhysicalType.Json => BsonDocument.Parse($"{{\"value\":{text}}}")["value"],
                _ => throw new ArgumentOutOfRangeException(nameof(projection), projection.Definition.Type, null)
            };
            return Normalize(projection, source);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw Invalid(projection, $"has invalid default '{text}' for {projection.Definition.Type}", exception);
        }
    }

    private static BsonValue String(ExecutableProjectedColumnRoute projection, BsonValue value)
    {
        if (!value.IsString)
            throw new FormatException("A string projection requires a JSON string.");
        PhysicalProjectionValueValidation.ValidateStringLength(value.AsString, projection.Definition);
        return value;
    }

    private static BsonValue Decimal(ExecutableProjectedColumnRoute projection, string value)
    {
        var definition = projection.Definition;
        var exact = MongoDbExactNumericLiteral.Parse(value).ValidateDecimal(
            definition.Precision ?? throw new InvalidOperationException("A Decimal projection requires declared precision."),
            definition.Scale ?? throw new InvalidOperationException("A Decimal projection requires declared scale."),
            definition.LogicalName);
        return new BsonDecimal128(Decimal128.Parse(exact));
    }

    private static string NumericText(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        BsonType.Decimal128 => value.AsDecimal128.ToString(),
        _ => throw new FormatException("A numeric projection requires a JSON number.")
    };

    private static string NumberText(JsonElement value, bool requireJsonNumber = false) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String when !requireJsonNumber => value.GetString()!,
        _ => throw new FormatException("A numeric projection requires a JSON number or numeric string.")
    };

    private static BsonValue DateTimeTicks(BsonValue value)
    {
        if (!value.IsString)
            throw new FormatException("A date-time projection requires an ISO-8601 string with an offset.");
        return new BsonInt64(ParseDateTime(value.AsString).UtcTicks);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        var text = value.Trim();
        var timeSeparator = text.IndexOf('T');
        if (timeSeparator < 0) timeSeparator = text.IndexOf('t');
        var offsetSeparator = timeSeparator < 0 ? -1 : text.IndexOfAny(['+', '-'], timeSeparator + 1);
        if (!text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) && offsetSeparator < 0)
            throw new FormatException("A date-time projection requires an explicit UTC designator or numeric offset.");
        var fractionalSeparator = timeSeparator < 0 ? -1 : text.IndexOfAny(['.', ','], timeSeparator + 1);
        if (fractionalSeparator >= 0)
        {
            var fractionalEnd = offsetSeparator >= 0
                ? offsetSeparator
                : text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ? text.Length - 1 : text.Length;
            if (fractionalEnd - fractionalSeparator - 1 > 7)
                throw new FormatException("A date-time projection cannot exceed 100ns fractional-second precision.");
        }
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static InvalidDataException Invalid(
        ExecutableProjectedColumnRoute projection,
        string reason,
        Exception? innerException = null) =>
        new(
            $"MongoDB projection '{projection.Definition.LogicalName}' at canonical JSON path " +
            $"'{projection.Definition.Path}' {reason}.",
            innerException);
}
