using Xunit;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkSummarizerTests
{
    [Fact]
    public void Summary_reports_normalized_batch_latency_throughput_allocation_round_trips_and_write_amplification()
    {
        var before = new StorageSnapshot(1_000, 200, 10, 10, new Dictionary<string, long>());
        var after = new StorageSnapshot(1_400, 300, 12, 12, new Dictionary<string, long>());
        BenchmarkSample[] samples =
        [
            new(0, 10, 1_000, 200, 5, 100, 2, before, after, new Dictionary<string, long> { ["commands"] = 5 }),
            new(1, 10, 2_000, 400, 7, 100, 2, before, after, new Dictionary<string, long> { ["commands"] = 7 })
        ];

        var summary = BenchmarkSummarizer.Summarize("case", samples);

        Assert.Equal(100, summary.NormalizedBatchLatencyP50NanosecondsPerOperation);
        Assert.Equal(200, summary.NormalizedBatchLatencyP95NanosecondsPerOperation);
        Assert.Equal(20 * 1_000_000_000d / 3_000, summary.ThroughputOperationsPerSecond);
        Assert.Equal(30, summary.AllocatedBytesPerOperation);
        Assert.Equal(0.6, summary.RoundTripsPerOperation);
        Assert.Equal(400, summary.StorageGrowthBytes);
        Assert.Equal(2, summary.NetStorageGrowthBytesPerLogicalPayloadByte);
        Assert.Equal(1, summary.NetPhysicalRowGrowthPerLogicalMutation);
        Assert.Equal(0.6, summary.ProviderWorkPerOperation["commands"]);

        var json = JsonSerializer.Serialize(samples[0], BenchmarkJson.Options);
        Assert.Contains("normalizedBatchLatencyNanosecondsPerOperation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"latencyNanosecondsPerOperation\"", json, StringComparison.Ordinal);
    }
}
