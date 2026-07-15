using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;

namespace Groundwork.MongoDb;

public static class MongoDbGroundworkCapabilities
{
    private const string TransactionEvidenceWarning =
        "Atomic commit requires deployment evidence from a MongoDB replica set or sharded cluster.";

    private static readonly IReadOnlySet<PortableQueryOperation> QueryOperations =
        Enum.GetValues<PortableQueryOperation>().ToHashSet();

    private static readonly IReadOnlySet<ConcurrencyKind> ConcurrencyModes =
        Enum.GetValues<ConcurrencyKind>().ToHashSet();

    private static readonly IReadOnlySet<MaterializationOperationKind> MaterializationOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static ProviderIdentity Provider { get; } = new("groundwork-mongodb", "1.0.0");

    public static ProviderCapabilityReport Runtime() => Runtime(Provider);

    public static ProviderCapabilityReport Runtime(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            QueryOperations,
            ConcurrencyModes,
            [TransactionEvidenceWarning]);

    /// <summary>
    /// Reports runtime capabilities after the connected MongoDB deployment has been verified as a
    /// replica set or sharded cluster capable of multi-document transactions.
    /// </summary>
    public static ProviderCapabilityReport RuntimeForTransactionCapableDeployment() =>
        RuntimeForTransactionCapableDeployment(Provider);

    /// <summary>
    /// Reports runtime capabilities after the connected MongoDB deployment has been verified as a
    /// replica set or sharded cluster capable of multi-document transactions.
    /// </summary>
    public static ProviderCapabilityReport RuntimeForTransactionCapableDeployment(ProviderIdentity provider)
    {
        var report = Runtime(provider).WithCapabilities(WellKnownCapabilities.AtomicCommit);
        return report with
        {
            Warnings = report.Warnings
                .Where(warning => !string.Equals(warning, TransactionEvidenceWarning, StringComparison.Ordinal))
                .ToArray()
        };
    }

    public static MaterializationCapabilityReport Materialization() => Materialization(Provider);

    public static MaterializationCapabilityReport Materialization(ProviderIdentity provider) =>
        new(provider, MaterializationOperations, SupportsSchemaHistory: true);
}
