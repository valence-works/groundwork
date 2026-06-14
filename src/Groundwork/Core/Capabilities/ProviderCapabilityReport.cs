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
    IReadOnlySet<MaterializationOperationKind> SupportedMaterializationOperations,
    bool SupportsSchemaHistory,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// A provider that serves only the portable document contract: it advertises no operational
    /// capabilities, so any unit requiring operational capabilities is <c>Unsupported</c>.
    /// </summary>
    public static ProviderCapabilityReport PortableDocumentProvider(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            Enum.GetValues<MaterializationOperationKind>().ToHashSet(),
            true,
            []);

    /// <summary>
    /// A provider that supports every built-in capability and has supplied evidence for each, so
    /// operational manifests evaluate to <c>Supported</c>.
    /// </summary>
    public static ProviderCapabilityReport OperationalProvider(ProviderIdentity provider)
    {
        var capabilities = WellKnownCapabilities.All.Select(descriptor => descriptor.Id).ToHashSet();
        return new(
            provider,
            capabilities,
            capabilities.ToHashSet(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            Enum.GetValues<MaterializationOperationKind>().ToHashSet(),
            true,
            []);
    }

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

public enum MaterializationOperationKind
{
    CreateStorageUnit,
    CreateIndex,
    CreateOptimizedProjection,
    RecordSchemaHistory
}
