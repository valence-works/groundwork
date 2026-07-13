using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Materialization;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.PostgreSql;

public static class PostgreSqlGroundworkCapabilities
{
    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<MaterializationOperationKind> MaterializationOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static ProviderIdentity Provider { get; } = new("groundwork-postgresql", "1.0.0");

    /// <summary>PostgreSQL identifier normalization with its native 63-byte UTF-8 limit.</summary>
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
        const int maximumBytes = 63;
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        var suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        var prefixBudget = maximumBytes - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (used + bytes > prefixBudget)
                break;
            builder.Append(rune);
            used += bytes;
        }
        return builder.Append(suffix).ToString();
    }
}
