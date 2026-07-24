using Groundwork.Core.Physicalization;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Sqlite;

/// <summary>
/// Applies SQLite's schema-global object namespace to provider physical names. Index identifiers
/// include their owning storage unit because SQLite does not scope index names to their table.
/// </summary>
public sealed class SqlitePhysicalNameNormalizer : IProviderPhysicalNameNormalizer
{
    public static SqlitePhysicalNameNormalizer Instance { get; } = new();

    private SqlitePhysicalNameNormalizer()
    {
    }

    public string Normalize(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PhysicalIndex => PhysicalizationNameEncoder.Encode(
            $"{context.StorageUnit.Value}\u001f{context.LogicalName}"),
        _ => context.LogicalName
    };

    public string GetCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or
        PhysicalObjectKind.LinkedIndexStorage or
        PhysicalObjectKind.CollectionElementStorage or
        PhysicalObjectKind.PhysicalIndex or
        PhysicalObjectKind.SchemaHistory => "schema-objects",
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField =>
            $"{context.StorageUnit.Value}:columns",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField =>
            $"{context.StorageUnit.Value}:linked-columns",
        PhysicalObjectKind.CollectionElementField =>
            $"{context.StorageUnit.Value}:collection-element-columns",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context.ObjectKind, null)
    };
}
