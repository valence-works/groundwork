using Groundwork.Core.Capabilities;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalProviderCapabilityTests
{
    [Fact]
    public void PostgreSql_runtime_report_supports_and_evidences_atomic_commit() =>
        AssertAtomicCommit(PostgreSqlGroundworkCapabilities.Runtime());

    [Fact]
    public void SqlServer_runtime_report_supports_and_evidences_atomic_commit() =>
        AssertAtomicCommit(SqlServerGroundworkCapabilities.Runtime());

    private static void AssertAtomicCommit(ProviderCapabilityReport report)
    {
        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.SupportedCapabilities);
        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.EvidencedCapabilities);
        Assert.IsType<ProviderFit.Supported>(
            new ProviderCapabilityValidator().Evaluate(AtomicCommitManifest(), report));
    }

    private static StorageManifest AtomicCommitManifest()
    {
        var manifest = RelationalTestManifests.MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Intent = StorageIntent.Operational(
                        "Configuration changes require an atomic commit.",
                        WorkloadIntent.RuntimeContinuationState,
                        WellKnownCapabilities.AtomicCommit)
                }
            ]
        };
    }
}
