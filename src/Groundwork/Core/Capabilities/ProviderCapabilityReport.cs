using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Capabilities;

public sealed record ProviderCapabilityReport(
    ProviderIdentity Provider,
    IReadOnlySet<StorageIntentKind> SupportedStorageIntents,
    IReadOnlySet<StorageRequirement> SupportedStorageRequirements,
    IndexCapabilities Indexes,
    IReadOnlySet<PortableQueryOperation> SupportedQueryOperations,
    IReadOnlySet<ConcurrencyKind> SupportedConcurrencyModes,
    IReadOnlySet<MaterializationOperationKind> SupportedMaterializationOperations,
    bool SupportsSchemaHistory,
    IReadOnlyList<string> Warnings)
{
    public static ProviderCapabilityReport PortableDocumentProvider(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<StorageIntentKind> { StorageIntentKind.PortableDocument },
            new HashSet<StorageRequirement>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            Enum.GetValues<MaterializationOperationKind>().ToHashSet(),
            true,
            []);
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
