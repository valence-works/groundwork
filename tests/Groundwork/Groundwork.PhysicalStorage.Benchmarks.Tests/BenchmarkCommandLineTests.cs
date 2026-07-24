using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkCommandLineTests
{
    [Fact]
    public void Command_line_filters_a_fixed_profile_without_changing_its_reproducibility_controls()
    {
        var command = BenchmarkCommandLine.Parse(
            ["run", "--profile", "scheduled", "--providers", "sqlite,mongodb", "--forms", "entity", "--workloads", "reused-client-point-read-batch,backfill-migration", "--dataset-sizes", "1000,100000,1000000", "--payload-padding-bytes", "0,1024", "--selectivity-bps", "1000,5000", "--independent-runs", "3", "--no-containers"],
            Environment.CurrentDirectory);

        var request = Assert.IsType<BenchmarkRunRequest>(command.Request);
        Assert.Equal(BenchmarkProfiles.Scheduled.Seed, request.Configuration.Seed);
        Assert.Equal(BenchmarkProfiles.Scheduled.MeasurementIterations, request.Configuration.MeasurementIterations);
        Assert.Equal([BenchmarkProvider.Sqlite, BenchmarkProvider.MongoDb], request.Configuration.Providers);
        Assert.Equal([PhysicalStorageForm.PhysicalEntityTable], request.Configuration.StorageForms);
        Assert.Equal([BenchmarkWorkload.ReusedClientPointReadBatch, BenchmarkWorkload.BackfillMigration], request.Workloads);
        Assert.Equal(BenchmarkProfiles.RatifiedDatasetSizes, request.Dimensions!.DatasetSizes);
        Assert.Equal([0, 1_024], request.Dimensions.PayloadPaddingBytes);
        Assert.Equal([1_000, 5_000], request.Dimensions.QuerySelectivityBasisPoints);
        Assert.Equal(3, request.Dimensions.IndependentRuns);
        Assert.False(request.AllowContainers);
    }

    [Fact]
    public void Scheduled_command_rejects_fewer_than_three_independent_processes()
    {
        var exception = Assert.Throws<ArgumentException>(() => BenchmarkCommandLine.Parse(
            ["run", "--profile", "scheduled", "--independent-runs", "2"],
            Environment.CurrentDirectory));

        Assert.Contains("at least 3", exception.Message, StringComparison.Ordinal);
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
