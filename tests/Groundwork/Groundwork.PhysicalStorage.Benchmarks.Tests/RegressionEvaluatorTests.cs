using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class RegressionEvaluatorTests
{
    [Fact]
    public void Scheduled_gate_flags_a_repeatable_latency_regression_beyond_the_budget()
    {
        var baseline = Samples(30, elapsedNanoseconds: 1_000);
        var candidate = Samples(30, elapsedNanoseconds: 1_300);

        var result = RegressionEvaluator.Compare(
            "sqlite/entity/read", baseline, candidate, RegressionPolicy.Scheduled);

        Assert.True(result.IsComparable);
        Assert.True(result.RequiresConfirmation);
        Assert.True(result.Regressed);
        Assert.All(result.Metrics.Where(metric => metric.Name.StartsWith("operation_latency", StringComparison.Ordinal)),
            metric => Assert.True(metric.LowerConfidenceRatio > 1.10));
        var throughput = Assert.Single(result.Metrics, metric => metric.Name == "throughput_ops_per_second");
        Assert.Equal(MetricDirection.HigherIsBetter, throughput.Direction);
        Assert.True(throughput.UpperConfidenceRatio < 0.90);
        Assert.True(throughput.Regressed);
    }

    [Fact]
    public void Scheduled_gate_applies_the_storage_budget_to_write_amplification()
    {
        var baseline = SamplesWithStorage(30, storageGrowthBytes: 100, logicalPayloadBytes: 100);
        var candidate = SamplesWithStorage(30, storageGrowthBytes: 130, logicalPayloadBytes: 100);

        var result = RegressionEvaluator.Compare(
            "sqlite/entity/write", baseline, candidate, RegressionPolicy.Scheduled);

        var storage = Assert.Single(
            result.Metrics,
            metric => metric.Name == "storage_bytes_per_logical_byte");
        Assert.Equal(MetricDirection.LowerIsBetter, storage.Direction);
        Assert.Equal(0.15, storage.Budget);
        Assert.True(storage.LowerConfidenceRatio > 1.15);
        Assert.True(storage.Regressed);
    }

    [Fact]
    public void Scheduled_gate_omits_storage_when_bootstrap_resamples_have_no_baseline_growth()
    {
        var baseline = SamplesWithStorage(30, storageGrowthBytes: 0, logicalPayloadBytes: 100);
        baseline[0] = baseline[0] with
        {
            StorageAfter = baseline[0].StorageAfter! with { TotalBytes = 10_100 }
        };
        var candidate = SamplesWithStorage(30, storageGrowthBytes: 100, logicalPayloadBytes: 100);

        var result = RegressionEvaluator.Compare(
            "sqlite/entity/write", baseline, candidate, RegressionPolicy.Scheduled);

        Assert.DoesNotContain(result.Metrics, metric => metric.Name == "storage_bytes_per_logical_byte");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("unstable", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduled_gate_refuses_to_compare_underpowered_runs()
    {
        var result = RegressionEvaluator.Compare(
            "sqlite/entity/read", Samples(1, 1_000), Samples(1, 2_000), RegressionPolicy.Scheduled);

        Assert.False(result.IsComparable);
        Assert.False(result.Regressed);
        Assert.Contains("20", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_refuses_invalid_raw_samples_instead_of_emitting_non_finite_metrics()
    {
        var baseline = Samples(30, 1_000);
        baseline[0] = baseline[0] with { Operations = 0 };

        var result = RegressionEvaluator.Compare(
            "sqlite/entity/read", baseline, Samples(30, 1_000), RegressionPolicy.Scheduled);

        Assert.False(result.IsComparable);
        Assert.Empty(result.Metrics);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("invalid", StringComparison.Ordinal));
    }

    private static BenchmarkSample[] Samples(int count, long elapsedNanoseconds) =>
        Enumerable.Range(0, count)
            .Select(iteration => new BenchmarkSample(
                iteration, 10, elapsedNanoseconds + iteration, 1_000, 1, 0, 0,
                null, null, new Dictionary<string, long>(),
                Enumerable.Repeat(elapsedNanoseconds, 10).ToArray()))
            .ToArray();

    private static BenchmarkSample[] SamplesWithStorage(
        int count,
        long storageGrowthBytes,
        long logicalPayloadBytes) =>
        Enumerable.Range(0, count)
            .Select(iteration => new BenchmarkSample(
                iteration,
                Operations: 10,
                ElapsedNanoseconds: 1_000,
                AllocatedBytes: 1_000,
                RoundTrips: 1,
                LogicalPayloadBytes: logicalPayloadBytes,
                LogicalMutations: 10,
                StorageBefore: new StorageSnapshot(10_000, 1_000, 100, 0, new Dictionary<string, long>()),
                StorageAfter: new StorageSnapshot(
                    10_000 + storageGrowthBytes,
                    1_000,
                    110,
                    0,
                    new Dictionary<string, long>()),
                ProviderWork: new Dictionary<string, long>(),
                OperationLatencyNanoseconds: Enumerable.Repeat(100L, 10).ToArray()))
            .ToArray();
}
