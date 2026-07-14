using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.Manifests;

public sealed partial record StorageManifest(
    StorageManifestIdentity Identity,
    StorageManifestOwner Owner,
    StorageManifestVersion Version,
    IReadOnlyList<StorageUnit> StorageUnits,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string> CompatibilityNotes)
{
    /// <summary>
    /// Gets manifest/composition-owned shared document stores. Storage units reference these by
    /// <see cref="SharedStorageBinding"/> and cannot redefine their primary envelope or name.
    /// </summary>
    public IReadOnlyList<SharedDocumentStorageDefinition> SharedDocumentStorages { get; init; } = [];
}
