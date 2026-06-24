using System.Text.Json;

namespace Groundwork.Relational.Physicalization;

public static class RelationalPhysicalizationValues
{
    public static bool TryRead(string contentJson, string path, out string value)
    {
        using var document = JsonDocument.Parse(contentJson);
        return TryRead(document.RootElement, path, out value);
    }

    public static bool TryRead(JsonElement root, string path, out string value)
    {
        value = "";
        if (!TryGetPropertyPath(root, path, out var element))
            return false;

        value = NormalizeValue(element);
        return true;
    }

    public static bool TryGetPropertyPath(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return false;
        }

        return element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    public static string NormalizeValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
}

