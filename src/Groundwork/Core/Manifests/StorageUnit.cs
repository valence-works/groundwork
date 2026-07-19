using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

namespace Groundwork.Core.Manifests;

public sealed record StorageUnit(
    StorageUnitIdentity Identity,
    string DisplayName,
    StorageIntent Intent,
    LifecyclePolicy Lifecycle,
    IdentityPolicy IdentityPolicy,
    TenancyPolicy Tenancy,
    ConcurrencyPolicy Concurrency,
    SerializationPolicy Serialization,
    IReadOnlyList<IndexDeclaration> Indexes,
    IReadOnlyList<PortableQueryDeclaration> Queries,
    PhysicalizationPolicy Physicalization)
{
    /// <summary>
    /// Creates a storage unit using the current physical-storage declaration surface without
    /// requiring callers to supply obsolete index, query, and physicalization constructor values.
    /// The legacy values are retained only for binary compatibility and are intentionally empty;
    /// all current routing intent belongs to <paramref name="physicalStorage"/>.
    /// </summary>
    public static StorageUnit Create(
        StorageUnitIdentity identity,
        string displayName,
        StorageIntent intent,
        LifecyclePolicy lifecycle,
        IdentityPolicy identityPolicy,
        TenancyPolicy tenancy,
        ConcurrencyPolicy concurrency,
        SerializationPolicy serialization,
        StorageUnitPhysicalStorage physicalStorage)
    {
        ArgumentNullException.ThrowIfNull(physicalStorage);
#pragma warning disable GW0001, GW0002, GW0003
        return new StorageUnit(
            identity,
            displayName,
            intent,
            lifecycle,
            identityPolicy,
            tenancy,
            concurrency,
            serialization,
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = physicalStorage
        };
#pragma warning restore GW0001, GW0002, GW0003
    }

    /// <summary>
    /// Gets the provider-neutral physical-storage declaration used by the three-form resolver.
    /// A null value identifies a legacy unit that must be converted explicitly through
    /// <see cref="LegacyPhysicalStorageBridge"/>.
    /// </summary>
    public StorageUnitPhysicalStorage? PhysicalStorage { get; init; }
}
