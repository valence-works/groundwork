using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Physicalization;

[Obsolete(
    "Use ProjectedColumnDefinition within PhysicalTableDefinition. This helper remains only for LegacyPhysicalStorageBridge.",
    DiagnosticId = "GW0004")]
public static class PhysicalizationProjection
{
    public static IReadOnlyList<PhysicalizedFieldPlan> EligibleFields(StorageUnit unit)
    {
        return unit.Indexes
            .Where(index => IsPhysicalized(unit, index) && IsEligible(index))
            .Select(index => new PhysicalizedFieldPlan(
                index.Identity,
                index.Fields[0].Path,
                index.ValueKind,
                index.IsUnique,
                index.IsSortable))
            .ToList();
    }

    public static bool IsEligible(StorageUnit unit, string indexName) =>
        EligibleFields(unit).Any(field => field.Name == indexName);

    private static bool IsPhysicalized(StorageUnit unit, IndexDeclaration index) =>
        index.Physicalization switch
        {
            IndexPhysicalizationPolicy.Optimized => true,
            IndexPhysicalizationPolicy.Portable => false,
            IndexPhysicalizationPolicy.Default => unit.Physicalization.Kind == PhysicalizationKind.Optimized,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index.Physicalization, "Unsupported index physicalization policy.")
        };

    private static bool IsEligible(IndexDeclaration index) =>
        index.Fields.Count == 1 &&
        index.MissingValueBehavior == MissingValueBehavior.Excluded &&
        index.SupportedOperations.Contains(PortableQueryOperation.Equal);
}
