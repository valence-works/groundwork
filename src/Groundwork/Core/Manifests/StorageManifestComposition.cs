namespace Groundwork.Core.Manifests;

/// <summary>
/// Composes several <see cref="StorageManifest"/> instances whose storage units are disjoint into a
/// single union manifest. This lets one materialized provider instance (one <c>IDocumentStore</c>)
/// back the combined set of document kinds — for example a runtime manifest unioned with a design
/// manifest — without changing the store, which resolves units purely by identity.
/// </summary>
public static class StorageManifestComposition
{
    /// <summary>
    /// Builds a union manifest from <paramref name="manifests"/>. Storage-unit identities must be
    /// disjoint across the inputs; a collision throws <see cref="StorageManifestCompositionException"/>.
    /// Required capabilities are unioned and compatibility notes are concatenated (de-duplicated).
    /// </summary>
    /// <param name="identity">Identity to assign the composed manifest.</param>
    /// <param name="owner">Owner to assign the composed manifest.</param>
    /// <param name="version">Version to assign the composed manifest.</param>
    /// <param name="manifests">The manifests to union. Must contain at least one entry.</param>
    public static StorageManifest Union(
        StorageManifestIdentity identity,
        StorageManifestOwner owner,
        StorageManifestVersion version,
        params StorageManifest[] manifests)
        => Union(identity, owner, version, (IReadOnlyCollection<StorageManifest>)manifests);

    /// <inheritdoc cref="Union(StorageManifestIdentity, StorageManifestOwner, StorageManifestVersion, StorageManifest[])"/>
    public static StorageManifest Union(
        StorageManifestIdentity identity,
        StorageManifestOwner owner,
        StorageManifestVersion version,
        IReadOnlyCollection<StorageManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(manifests);

        if (manifests.Count == 0)
            throw new ArgumentException("At least one manifest is required to compose a union.", nameof(manifests));

        var units = new List<StorageUnit>();
        var seenUnitIdentities = new HashSet<string>(StringComparer.Ordinal);
        var requiredCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var compatibilityNotes = new List<string>();
        var seenNotes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            foreach (var unit in manifest.StorageUnits)
            {
                if (!seenUnitIdentities.Add(unit.Identity.Value))
                    throw new StorageManifestCompositionException(unit.Identity.Value);

                units.Add(unit);
            }

            foreach (var capability in manifest.RequiredCapabilities)
                requiredCapabilities.Add(capability);

            foreach (var note in manifest.CompatibilityNotes)
            {
                if (seenNotes.Add(note))
                    compatibilityNotes.Add(note);
            }
        }

        return new StorageManifest(
            identity,
            owner,
            version,
            units,
            requiredCapabilities,
            compatibilityNotes);
    }
}

/// <summary>
/// Thrown when composing a union manifest encounters a storage-unit identity declared by more than
/// one source manifest. The union requires disjoint document kinds.
/// </summary>
public sealed class StorageManifestCompositionException : InvalidOperationException
{
    public StorageManifestCompositionException(string unitIdentity)
        : base($"Storage unit identity '{unitIdentity}' is declared by more than one manifest; a union manifest requires disjoint storage units.")
    {
        UnitIdentity = unitIdentity;
    }

    public string UnitIdentity { get; }
}
