using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Materialization;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.SqlServer;

public static class SqlServerGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<MaterializationOperationKind> MaterializationOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static ProviderIdentity Provider { get; } = new("groundwork-sqlserver", "1.0.0");

    /// <summary>SQL Server identifier normalization with its native 128-character limit.</summary>
    public static IProviderPhysicalNameNormalizer PhysicalNames { get; } =
        new DelegateProviderPhysicalNameNormalizer(context => NormalizePhysicalName(context.LogicalName));

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

    private static string NormalizePhysicalName(string value)
    {
        const int maximumLength = 128;
        if (value.Length <= maximumLength)
            return value;
        var suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        var take = maximumLength - suffix.Length;
        while (take > 0 && char.IsHighSurrogate(value[take - 1]))
            take--;
        return value[..take] + suffix;
    }
}
