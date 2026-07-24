using System.Text.Json;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>One immediate, authoritative canonical JSON collection element and its stable ordinal.</summary>
public sealed record CanonicalCollectionElement(int Ordinal, JsonElement Value);

/// <summary>
/// Reads a declared bounded collection projection from authoritative canonical JSON. This is the
/// single provider-neutral admission boundary for collection shapes; provider writers can then
/// convert each returned element using their existing scalar conversion rules.
/// </summary>
public static class CanonicalCollectionElementProjection
{
    /// <summary>
    /// Returns whether a path can address exactly one JSON collection property without relying on
    /// provider-specific JSON-path syntax. Collection projections deliberately support only this
    /// portable dotted-property subset.
    /// </summary>
    public static bool IsSupportedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Split('.', StringSplitOptions.None).All(segment =>
            !string.IsNullOrWhiteSpace(segment) && string.Equals(segment, segment.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// JSON projections describe an arbitrary JSON value and therefore cannot prove the one native
    /// element type required by an equality-membership index. All other portable scalar types have
    /// an unambiguous immediate JSON representation handled by provider conversion boundaries.
    /// </summary>
    public static bool SupportsElementType(PortablePhysicalType type) =>
        type != PortablePhysicalType.Json;

    /// <summary>
    /// Guards scalar-only provider paths until they explicitly opt into element-row maintenance.
    /// This prevents a collection declaration from silently becoming a provider-specific JSON or
    /// multi-key projection with different containment semantics.
    /// </summary>
    public static void RequireScalar(ProjectedColumnDefinition projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Cardinality != ProjectionCardinality.Scalar)
        {
            throw new NotSupportedException(
                $"Projected column '{projection.LogicalName}' declares '{projection.Cardinality}' and requires an element-row maintenance path.");
        }
    }

    /// <summary>Guards collection-only maintenance paths before they derive any provider values.</summary>
    public static void RequireCollection(ProjectedColumnDefinition projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Cardinality != ProjectionCardinality.CollectionElements ||
            projection.MaxCollectionElements is not > 0 ||
            !IsSupportedPath(projection.Path) ||
            !SupportsElementType(projection.Type))
        {
            throw new InvalidOperationException(
                $"Projection '{projection.LogicalName}' is not a valid bounded collection-element projection.");
        }
    }

    public static IReadOnlyList<CanonicalCollectionElement> Read(
        string canonicalJson,
        ProjectedColumnDefinition projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        ArgumentNullException.ThrowIfNull(projection);
        RequireCollection(projection);

        using var document = JsonDocument.Parse(canonicalJson);
        if (!TryGetPropertyPath(document.RootElement, projection.Path, out var collection) ||
            collection.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(projection, "must be a present JSON array");
        }

        var result = new List<CanonicalCollectionElement>();
        foreach (var element in collection.EnumerateArray())
        {
            if (result.Count == projection.MaxCollectionElements)
                throw Invalid(projection, $"cannot contain more than {projection.MaxCollectionElements} elements");
            if (element.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw Invalid(projection, "can contain only immediate non-null primitive elements");
            result.Add(new CanonicalCollectionElement(result.Count, element.Clone()));
        }

        return result;
    }

    private static bool TryGetPropertyPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.None))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }

        return true;
    }

    private static InvalidDataException Invalid(ProjectedColumnDefinition projection, string reason) =>
        new($"Canonical JSON path '{projection.Path}' for collection projection '{projection.LogicalName}' {reason}.");
}
