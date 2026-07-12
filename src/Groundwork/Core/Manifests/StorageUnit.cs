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
    /// Gets the provider-neutral physical-storage declaration used by the three-form resolver.
    /// A null value identifies a legacy unit that must be converted explicitly through
    /// <see cref="LegacyPhysicalStorageBridge"/>.
    /// </summary>
    public StorageUnitPhysicalStorage? PhysicalStorage { get; init; }
}
