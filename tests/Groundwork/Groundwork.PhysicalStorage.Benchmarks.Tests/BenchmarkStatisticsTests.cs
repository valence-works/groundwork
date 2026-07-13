using Groundwork.PhysicalStorage.Benchmarks;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkStatisticsTests
{
    [Theory]
    [InlineData(0.50, 5)]
    [InlineData(0.95, 9)]
    [InlineData(0.99, 9)]
    public void Percentile_uses_the_nearest_rank_over_ordered_samples(double percentile, double expected)
    {
        double[] samples = [9, 1, 7, 3, 5];

        Assert.Equal(expected, BenchmarkStatistics.Percentile(samples, percentile));
    }
}
