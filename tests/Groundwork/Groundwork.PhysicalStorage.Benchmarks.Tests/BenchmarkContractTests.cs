using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkContractTests
{
    [Fact]
    public void Smoke_runs_every_storage_form_on_sqlite_with_fixed_reproducibility_controls()
    {
        var profile = BenchmarkProfiles.Smoke;

        Assert.Equal(BenchmarkProfiles.ReproducibleSeed, profile.Seed);
        Assert.Equal([BenchmarkProvider.Sqlite], profile.Providers);
        Assert.Equal(Enum.GetValues<PhysicalStorageForm>(), profile.StorageForms);
        Assert.True(profile.WarmupIterations >= 1);
        Assert.True(profile.MeasurementIterations >= 5);
    }

    [Fact]
    public void Scheduled_matrix_covers_every_provider_form_and_required_workload()
    {
        var matrix = BenchmarkMatrix.Create(BenchmarkProfiles.Scheduled);

        Assert.Equal(
            Enum.GetValues<BenchmarkProvider>().Length *
            Enum.GetValues<PhysicalStorageForm>().Length *
            Enum.GetValues<BenchmarkWorkload>().Length,
            matrix.Count);
        Assert.All(Enum.GetValues<BenchmarkWorkload>(), workload =>
            Assert.Contains(matrix, benchmarkCase => benchmarkCase.Workload == workload));
    }

    [Fact]
    public void Artifact_layout_keeps_raw_plans_metadata_and_reports_separate()
    {
        var layout = new ArtifactLayout("artifacts/run");
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.PostgreSql,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);

        Assert.EndsWith("raw/measurements.jsonl", layout.RawMeasurements, StringComparison.Ordinal);
        Assert.EndsWith("reports/summary.json", layout.SummaryJson, StringComparison.Ordinal);
        Assert.EndsWith("reports/elsa-migration-decision.json", layout.ElsaMigrationDecisionJson, StringComparison.Ordinal);
        Assert.EndsWith("metadata/machine.json", layout.MachineMetadata, StringComparison.Ordinal);
        Assert.Equal("reports/summary.json", layout.RelativePath(layout.SummaryJson));
        Assert.EndsWith(
            "plans/postgresql/physicalentitytable/indexedquery.json",
            layout.Plan(benchmarkCase, "json"),
            StringComparison.Ordinal);
    }
}
