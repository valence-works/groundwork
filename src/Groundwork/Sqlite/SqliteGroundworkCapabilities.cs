using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Materialization;

namespace Groundwork.Sqlite;

public static class SqliteGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<MaterializationOperationKind> MaterializationOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static ProviderIdentity Provider { get; } = new("groundwork-sqlite", "1.0.0");

    /// <summary>SQLite physical naming with schema-global table and index collision semantics.</summary>
    public static IProviderPhysicalNameNormalizer PhysicalNames { get; } =
        SqlitePhysicalNameNormalizer.Instance;

    public static ProviderCapabilityReport Runtime() => Runtime(Provider);

    public static ProviderCapabilityReport Runtime(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            QueryOperations,
            ConcurrencyModes,
            []);

    public static MaterializationCapabilityReport Materialization() => Materialization(Provider);

    public static MaterializationCapabilityReport Materialization(ProviderIdentity provider) =>
        new(provider, MaterializationOperations, SupportsSchemaHistory: true);
}
