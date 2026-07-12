using Groundwork.Core.Manifests;

namespace Groundwork.Core.PhysicalStorage;

public enum PhysicalObjectKind
{
    PrimaryStorage,
    LinkedIndexStorage,
    EnvelopeField,
    LinkedIndexField,
    ProjectedField,
    LinkedProjectedField,
    PhysicalIndex,
    SchemaHistory
}

public sealed record PhysicalNameContext(
    StorageUnitIdentity StorageUnit,
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName);

public interface IPhysicalNamePolicy
{
    string ResolveName(PhysicalNameContext context);
}

public sealed class DelegatePhysicalNamePolicy(Func<PhysicalNameContext, string> resolver) : IPhysicalNamePolicy
{
    private readonly Func<PhysicalNameContext, string> _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    public string ResolveName(PhysicalNameContext context) => _resolver(context);
}

public static class PhysicalNamePolicy
{
    public static IPhysicalNamePolicy Identity { get; } =
        new DelegatePhysicalNamePolicy(context => context.FeatureDefaultLogicalName);
}

public sealed record PhysicalObjectNameOverride(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName);

public sealed record ProviderPhysicalNameContext(
    StorageUnitIdentity StorageUnit,
    PhysicalObjectKind ObjectKind,
    string LogicalName);

/// <summary>
/// Provider seam for identifier casing, reserved words, quoting rules, length limits, and
/// deterministic truncation. Business naming remains provider-agnostic.
/// </summary>
public interface IProviderPhysicalNameNormalizer
{
    string Normalize(ProviderPhysicalNameContext context);

    string GetCollisionScope(ProviderPhysicalNameContext context);
}

public sealed class DelegateProviderPhysicalNameNormalizer(
    Func<ProviderPhysicalNameContext, string> normalizer,
    Func<ProviderPhysicalNameContext, string>? collisionScope = null) : IProviderPhysicalNameNormalizer
{
    private readonly Func<ProviderPhysicalNameContext, string> _normalizer =
        normalizer ?? throw new ArgumentNullException(nameof(normalizer));
    private readonly Func<ProviderPhysicalNameContext, string>? _collisionScope = collisionScope;

    public string Normalize(ProviderPhysicalNameContext context) => _normalizer(context);

    public string GetCollisionScope(ProviderPhysicalNameContext context) =>
        _collisionScope?.Invoke(context) ?? ProviderPhysicalNameNormalizerDefaults.GetCollisionScope(context);
}

internal static class ProviderPhysicalNameNormalizerDefaults
{
    public static string GetCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage => "primary-storage",
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField => $"{context.StorageUnit.Value}:columns",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField => $"{context.StorageUnit.Value}:linked-columns",
        PhysicalObjectKind.PhysicalIndex => $"{context.StorageUnit.Value}:physical-indexes",
        PhysicalObjectKind.SchemaHistory => "schema-history",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context.ObjectKind, null)
    };
}

public static class ProviderPhysicalNameNormalizer
{
    public static IProviderPhysicalNameNormalizer Identity { get; } =
        new DelegateProviderPhysicalNameNormalizer(context => context.LogicalName);
}

public sealed record ResolvedPhysicalObjectName(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName,
    StorageUnitIdentity NamingOwner);

public sealed record ProviderPhysicalObjectName(
    PhysicalObjectKind ObjectKind,
    string FeatureDefaultLogicalName,
    string LogicalName,
    string Identifier,
    string CollisionScope,
    StorageUnitIdentity NamingOwner);
