using Groundwork.Core.Capabilities;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.MongoDb.Documents;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbProviderCapabilityTests
{
    [Fact]
    public void Runtime_report_supports_atomic_commit_but_requires_deployment_evidence()
    {
        var report = MongoDbGroundworkCapabilities.Runtime();

        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.SupportedCapabilities);
        Assert.DoesNotContain(WellKnownCapabilities.AtomicCommit, report.EvidencedCapabilities);
        Assert.Contains(report.Warnings, warning => warning.Contains("replica set or sharded cluster", StringComparison.Ordinal));
        Assert.IsType<ProviderFit.RequiresEvidence>(
            new ProviderCapabilityValidator().Evaluate(MongoDbTestManifests.AtomicCommitManifest(), report));
    }

    [Fact]
    public void Transaction_capable_runtime_report_evidences_atomic_commit()
    {
        var report = MongoDbGroundworkCapabilities.RuntimeForTransactionCapableDeployment();

        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.SupportedCapabilities);
        Assert.Contains(WellKnownCapabilities.AtomicCommit, report.EvidencedCapabilities);
        Assert.Empty(report.Warnings);
        Assert.IsType<ProviderFit.Supported>(
            new ProviderCapabilityValidator().Evaluate(MongoDbTestManifests.AtomicCommitManifest(), report));
    }

    [Fact]
    public async Task Conventional_store_reports_atomic_boundary_after_fallback_probe_evidence()
    {
        var capability = new MongoDbTransactionCapability(_ => Task.FromResult(true));
        Assert.True(await capability.SupportsTransactionsAsync(CancellationToken.None));
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_capability_probe");
        var store = new MongoDbDocumentStore(
            database,
            MongoDbTestManifests.MetadataManifest(),
            DocumentStoreAccess.Global,
            scopeObserver: null,
            capability.SupportsTransactionsAsync,
            startSessionAsync: null,
            isTransactionSupportKnown: () => capability.IsKnownSupported);

        Assert.Equal(TransactionBoundary.CrossUnitAtomic, store.TransactionBoundary);
    }
}
