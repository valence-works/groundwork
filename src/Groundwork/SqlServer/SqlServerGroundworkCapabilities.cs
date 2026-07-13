using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Materialization;

namespace Groundwork.SqlServer;

public static class SqlServerGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<IndexValueKind> IndexValueKinds =
        new HashSet<IndexValueKind>
        {
            IndexValueKind.String,
            IndexValueKind.Number,
            IndexValueKind.Boolean,
            IndexValueKind.DateTime,
            IndexValueKind.Keyword
        };

    private static readonly IReadOnlySet<MissingValueBehavior> MissingValueBehaviors =
        Enum.GetValues<MissingValueBehavior>().ToHashSet();

    private static readonly IReadOnlySet<MaterializationOperationKind> MaterializationOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static ProviderIdentity Provider { get; } = new("groundwork-sqlserver", "1.0.0");

    /// <summary>SQL Server identifier normalization with its native 128-character limit.</summary>
    public static IProviderPhysicalNameNormalizer PhysicalNames { get; } =
        new DelegateProviderPhysicalNameNormalizer(context => SqlServerPhysicalName.Normalize(context.LogicalName));

    public static ProviderCapabilityReport Runtime() => Runtime(Provider);

    public static ProviderCapabilityReport Runtime(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            new IndexCapabilities(
                IndexValueKinds,
                SupportsUniqueIndexes: true,
                SupportsSortableIndexes: true,
                MissingValueBehaviors),
            QueryOperations,
            ConcurrencyModes,
            []);

    public static MaterializationCapabilityReport Materialization() => Materialization(Provider);

    public static MaterializationCapabilityReport Materialization(ProviderIdentity provider) =>
        new(provider, MaterializationOperations, SupportsSchemaHistory: true);

}
