using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
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
    PhysicalizationPolicy Physicalization);
