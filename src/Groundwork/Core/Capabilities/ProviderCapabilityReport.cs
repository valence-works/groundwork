using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Capabilities;

public sealed record ProviderCapabilityReport(
    ProviderIdentity Provider,
    IReadOnlySet<CapabilityId> SupportedCapabilities,
    IReadOnlySet<CapabilityId> EvidencedCapabilities,
    IndexCapabilities Indexes,
    IReadOnlySet<PortableQueryOperation> SupportedQueryOperations,
    IReadOnlySet<ConcurrencyKind> SupportedConcurrencyModes,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Returns a copy that additionally supports (and evidences) the given capabilities.</summary>
    public ProviderCapabilityReport WithCapabilities(params CapabilityId[] capabilities) =>
        this with
        {
            SupportedCapabilities = SupportedCapabilities.Concat(capabilities).ToHashSet(),
            EvidencedCapabilities = EvidencedCapabilities.Concat(capabilities).ToHashSet()
        };
}

public sealed record ProviderIdentity(string Name, string Version)
{
    public override string ToString() => $"{Name} {Version}";
}

public sealed record IndexCapabilities(
    IReadOnlySet<IndexValueKind> SupportedValueKinds,
    bool SupportsUniqueIndexes,
    bool SupportsSortableIndexes,
    IReadOnlySet<MissingValueBehavior> SupportedMissingValueBehaviors)
{
    public static IndexCapabilities All { get; } =
        new(
            Enum.GetValues<IndexValueKind>().ToHashSet(),
            true,
            true,
            Enum.GetValues<MissingValueBehavior>().ToHashSet());
}
