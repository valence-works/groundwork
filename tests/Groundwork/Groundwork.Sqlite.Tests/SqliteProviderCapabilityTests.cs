using Groundwork.Core.Capabilities;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderCapabilityTests
{
    [Fact]
    public void Runtime_report_supports_and_evidences_atomic_commit()
    {
        var report = SqliteGroundworkCapabilities.Runtime();

        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.SupportedCapabilities);
        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.EvidencedCapabilities);
        Assert.IsType<ProviderFit.Supported>(
            new ProviderCapabilityValidator().Evaluate(AtomicCommitManifest(), report));
    }

    private static StorageManifest AtomicCommitManifest()
    {
        var manifest = SqliteTestManifests.MetadataManifest();
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
