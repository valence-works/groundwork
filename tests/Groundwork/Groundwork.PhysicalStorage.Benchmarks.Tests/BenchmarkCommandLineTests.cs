using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkCommandLineTests
{
    [Fact]
    public void Command_line_filters_a_fixed_profile_without_changing_its_reproducibility_controls()
    {
        var command = BenchmarkCommandLine.Parse(
            ["run", "--profile", "scheduled", "--providers", "sqlite,mongodb", "--forms", "entity", "--workloads", "reused-client-point-read-batch,backfill-migration", "--no-containers"],
            Environment.CurrentDirectory);

        var request = Assert.IsType<BenchmarkRunRequest>(command.Request);
        Assert.Equal(BenchmarkProfiles.Scheduled.Seed, request.Configuration.Seed);
        Assert.Equal(BenchmarkProfiles.Scheduled.MeasurementIterations, request.Configuration.MeasurementIterations);
        Assert.Equal([BenchmarkProvider.Sqlite, BenchmarkProvider.MongoDb], request.Configuration.Providers);
        Assert.Equal([PhysicalStorageForm.PhysicalEntityTable], request.Configuration.StorageForms);
        Assert.Equal([BenchmarkWorkload.ReusedClientPointReadBatch, BenchmarkWorkload.BackfillMigration], request.Workloads);
        Assert.False(request.AllowContainers);
    }

    [Theory]
    [InlineData("cold-point-read")]
    [InlineData("warm-point-read")]
    [InlineData("restart-recovery")]
    public void Command_line_rejects_workload_names_that_overstate_measurement_semantics(string workload)
    {
        Assert.Throws<ArgumentException>(() => BenchmarkCommandLine.Parse(
            ["run", "--workloads", workload],
            Environment.CurrentDirectory));
    }
}
