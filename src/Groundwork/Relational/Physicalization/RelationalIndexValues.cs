using System.Text.Json;
using Groundwork.Core.Indexing;

namespace Groundwork.Relational.Physicalization;

/// <summary>
/// Single source of truth for extracting the portable index value of a document, shared by the
/// save-time index projection maintenance and the materialize-time additive-index backfill so the
/// two paths cannot drift.
/// </summary>
public static class RelationalIndexValues
{
    public static bool TryGetIndexValue(JsonElement root, IReadOnlyList<IndexField> fields, out string value)
    {
        if (fields.Count != 1)
        {
            value = "";
            return false;
        }

        return TryGetSingleFieldValue(root, fields[0].Path, out value);
    }

    public static bool TryGetIndexValue(JsonElement root, IReadOnlyList<string> fieldPaths, out string value)
    {
        if (fieldPaths.Count != 1)
        {
            value = "";
            return false;
        }

        return TryGetSingleFieldValue(root, fieldPaths[0], out value);
    }

    private static bool TryGetSingleFieldValue(JsonElement root, string path, out string value)
    {
        value = "";
        if (!RelationalPhysicalizationValues.TryGetPropertyPath(root, path, out var element))
            return false;

        value = RelationalPhysicalizationValues.NormalizeValue(element);
        return value.Length > 0 || element.ValueKind == JsonValueKind.String;
    }
}
