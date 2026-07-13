using System.Buffers;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>Lossless standard-JSON boundary for the provider's addressable BSON canonical value.</summary>
internal static class MongoDbCanonicalJson
{
    public static BsonDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement) as BsonDocument
            ?? throw new InvalidDataException("A physical document requires a JSON object root.");
    }

    public static string Serialize(BsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static BsonValue Read(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new BsonDocument(value.EnumerateObject()
            .Select(property => new BsonElement(property.Name, Read(property.Value)))),
        JsonValueKind.Array => new BsonArray(value.EnumerateArray().Select(Read)),
        JsonValueKind.String => new BsonString(value.GetString()!),
        JsonValueKind.Number => Number(value.GetRawText()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        JsonValueKind.Null => BsonNull.Value,
        _ => throw new InvalidDataException($"Unsupported canonical JSON value kind '{value.ValueKind}'.")
    };

    private static BsonValue Number(string text)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var int32))
            return new BsonInt32(int32);
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var int64))
            return new BsonInt64(int64);
        return new BsonBinaryData(
            Encoding.UTF8.GetBytes(text),
            BsonBinarySubType.UserDefined);
    }

    private static void Write(Utf8JsonWriter writer, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    writer.WritePropertyName(element.Name);
                    Write(writer, element.Value);
                }
                writer.WriteEndObject();
                return;
            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                    Write(writer, item);
                writer.WriteEndArray();
                return;
            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                return;
            case BsonType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                return;
            case BsonType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                return;
            case BsonType.Double:
                writer.WriteNumberValue(value.AsDouble);
                return;
            case BsonType.Decimal128:
                writer.WriteRawValue(value.AsDecimal128.ToString(), skipInputValidation: false);
                return;
            case BsonType.Binary when value.AsBsonBinaryData.SubType == BsonBinarySubType.UserDefined:
                writer.WriteRawValue(
                    Encoding.UTF8.GetString(value.AsBsonBinaryData.Bytes),
                    skipInputValidation: false);
                return;
            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                return;
            case BsonType.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new InvalidDataException(
                    $"BSON value kind '{value.BsonType}' cannot be emitted as canonical standard JSON.");
        }
    }
}
