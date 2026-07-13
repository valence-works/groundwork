namespace Groundwork.PhysicalStorage.Benchmarks;

public static class BenchmarkStatistics
{
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(values));
        if (percentile is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in (0, 1].");

        var ordered = values.Order().ToArray();
        var rank = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[rank];
    }
}
