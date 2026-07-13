namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record RegressionPolicy(
    int MinimumSamples,
    int BootstrapIterations,
    double ConfidenceLevel,
    double LatencyBudget,
    double ThroughputBudget,
    double AllocationBudget,
    double StorageBudget,
    bool RequiresConfirmation)
{
    public static RegressionPolicy Smoke { get; } = new(
        MinimumSamples: 5,
        BootstrapIterations: 1_000,
        ConfidenceLevel: 0.95,
        LatencyBudget: 1.00,
        ThroughputBudget: 0.50,
        AllocationBudget: 0.50,
        StorageBudget: 0.50,
        RequiresConfirmation: false);

    public static RegressionPolicy Scheduled { get; } = new(
        MinimumSamples: 20,
        BootstrapIterations: 5_000,
        ConfidenceLevel: 0.95,
        LatencyBudget: 0.10,
        ThroughputBudget: 0.10,
        AllocationBudget: 0.10,
        StorageBudget: 0.15,
        RequiresConfirmation: true);
}

public enum MetricDirection
{
    LowerIsBetter,
    HigherIsBetter
}

public sealed record RegressionMetric(
    string Name,
    MetricDirection Direction,
    double Baseline,
    double Candidate,
    double Ratio,
    double LowerConfidenceRatio,
    double UpperConfidenceRatio,
    double Budget,
    bool Regressed);

public sealed record RegressionEvaluation(
    string CaseIdentity,
    bool IsComparable,
    bool RequiresConfirmation,
    IReadOnlyList<RegressionMetric> Metrics,
    IReadOnlyList<string> Diagnostics)
{
    public bool Regressed => Metrics.Any(metric => metric.Regressed);
}

public static class RegressionEvaluator
{
    public static RegressionEvaluation Compare(
        string caseIdentity,
        IReadOnlyList<BenchmarkSample> baseline,
        IReadOnlyList<BenchmarkSample> candidate,
        RegressionPolicy policy,
        int seed = BenchmarkProfiles.ReproducibleSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseIdentity);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        if (baseline.Count < policy.MinimumSamples || candidate.Count < policy.MinimumSamples)
        {
            return new RegressionEvaluation(
                caseIdentity,
                false,
                policy.RequiresConfirmation,
                [],
                [$"At least {policy.MinimumSamples} baseline and candidate samples are required."]);
        }
        if (baseline.Any(sample => !IsValid(sample)) || candidate.Any(sample => !IsValid(sample)))
        {
            return new RegressionEvaluation(
                caseIdentity,
                false,
                policy.RequiresConfirmation,
                [],
                ["Baseline or candidate contains an invalid raw sample."]);
        }

        var baselineLatency = baseline.Select(sample => sample.NormalizedBatchLatencyNanosecondsPerOperation).ToArray();
        var candidateLatency = candidate.Select(sample => sample.NormalizedBatchLatencyNanosecondsPerOperation).ToArray();
        var diagnostics = new List<string>();
        var metrics = new List<RegressionMetric>
        {
            BootstrapMetric(
                "normalized_batch_latency_p50_ns_per_operation", MetricDirection.LowerIsBetter, baselineLatency, candidateLatency,
                0.50, policy.LatencyBudget, policy, seed),
            BootstrapMetric(
                "normalized_batch_latency_p95_ns_per_operation", MetricDirection.LowerIsBetter, baselineLatency, candidateLatency,
                0.95, policy.LatencyBudget, policy, seed + 1),
            BootstrapMetric(
                "throughput_ops_per_second", MetricDirection.HigherIsBetter,
                baseline.Select(sample => sample.ThroughputOperationsPerSecond).ToArray(),
                candidate.Select(sample => sample.ThroughputOperationsPerSecond).ToArray(),
                0.50, policy.ThroughputBudget, policy, seed + 2)
        };
        var baselineAllocations = baseline.Select(sample => sample.AllocatedBytesPerOperation).ToArray();
        var candidateAllocations = candidate.Select(sample => sample.AllocatedBytesPerOperation).ToArray();
        if (baselineAllocations.All(value => value > 0))
        {
            metrics.Add(BootstrapMetric(
                "allocated_bytes_per_op", MetricDirection.LowerIsBetter,
                baselineAllocations, candidateAllocations, 0.50,
                policy.AllocationBudget, policy, seed + 3));
        }
        else
        {
            diagnostics.Add("Allocation regression was not evaluated: baseline allocation contains a zero value.");
        }

        AddStorageMetric(baseline, candidate, policy, seed + 4, metrics, diagnostics);

        return new RegressionEvaluation(caseIdentity, true, policy.RequiresConfirmation, metrics, diagnostics);
    }

    private static RegressionMetric BootstrapMetric(
        string name,
        MetricDirection direction,
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        double percentile,
        double budget,
        RegressionPolicy policy,
        int seed)
    {
        var baselineValue = BenchmarkStatistics.Percentile(baseline, percentile);
        var candidateValue = BenchmarkStatistics.Percentile(candidate, percentile);
        var ratios = new double[policy.BootstrapIterations];
        var random = new Random(seed);
        for (var iteration = 0; iteration < ratios.Length; iteration++)
        {
            var baselineResample = Resample(baseline, random);
            var candidateResample = Resample(candidate, random);
            var denominator = BenchmarkStatistics.Percentile(baselineResample, percentile);
            ratios[iteration] = denominator <= 0
                ? double.PositiveInfinity
                : BenchmarkStatistics.Percentile(candidateResample, percentile) / denominator;
        }

        var alpha = (1 - policy.ConfidenceLevel) / 2;
        var lower = BenchmarkStatistics.Percentile(ratios, alpha);
        var upper = BenchmarkStatistics.Percentile(ratios, 1 - alpha);
        return new RegressionMetric(
            name,
            direction,
            baselineValue,
            candidateValue,
            baselineValue <= 0 ? double.PositiveInfinity : candidateValue / baselineValue,
            lower,
            upper,
            budget,
            direction switch
            {
                MetricDirection.LowerIsBetter => lower > 1 + budget,
                MetricDirection.HigherIsBetter => upper < 1 - budget,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            });
    }

    private static void AddStorageMetric(
        IReadOnlyList<BenchmarkSample> baseline,
        IReadOnlyList<BenchmarkSample> candidate,
        RegressionPolicy policy,
        int seed,
        ICollection<RegressionMetric> metrics,
        ICollection<string> diagnostics)
    {
        var baselineStorage = StorageObservations(baseline);
        var candidateStorage = StorageObservations(candidate);
        if (baselineStorage.Length == 0 && candidateStorage.Length == 0)
            return;
        if (baselineStorage.Length < policy.MinimumSamples || candidateStorage.Length < policy.MinimumSamples)
        {
            diagnostics.Add(
                $"Storage regression was not evaluated: at least {policy.MinimumSamples} storage observations are required.");
            return;
        }

        var baselineValue = AggregateStorageRatio(baselineStorage);
        if (baselineValue <= 0)
        {
            diagnostics.Add("Storage regression was not evaluated: the baseline has no positive storage growth.");
            return;
        }

        var candidateValue = AggregateStorageRatio(candidateStorage);
        var ratios = new double[policy.BootstrapIterations];
        var random = new Random(seed);
        var unstableBaseline = false;
        for (var iteration = 0; iteration < ratios.Length; iteration++)
        {
            var baselineResample = Resample(baselineStorage, random);
            var candidateResample = Resample(candidateStorage, random);
            var denominator = AggregateStorageRatio(baselineResample);
            if (denominator <= 0)
            {
                unstableBaseline = true;
                break;
            }
            ratios[iteration] = AggregateStorageRatio(candidateResample) / denominator;
        }
        if (unstableBaseline)
        {
            diagnostics.Add(
                "Storage regression was not evaluated: baseline growth is too sparse and unstable for a bootstrap ratio.");
            return;
        }

        var alpha = (1 - policy.ConfidenceLevel) / 2;
        var lower = BenchmarkStatistics.Percentile(ratios, alpha);
        var upper = BenchmarkStatistics.Percentile(ratios, 1 - alpha);
        metrics.Add(new RegressionMetric(
            "storage_bytes_per_logical_byte",
            MetricDirection.LowerIsBetter,
            baselineValue,
            candidateValue,
            candidateValue / baselineValue,
            lower,
            upper,
            policy.StorageBudget,
            lower > 1 + policy.StorageBudget));
    }

    private static StorageObservation[] StorageObservations(IReadOnlyList<BenchmarkSample> samples) => samples
        .Where(sample => sample.StorageBefore is not null &&
                         sample.StorageAfter is not null &&
                         sample.LogicalPayloadBytes > 0)
        .Select(sample => new StorageObservation(
            Math.Max(0, sample.StorageAfter!.TotalBytes - sample.StorageBefore!.TotalBytes),
            sample.LogicalPayloadBytes))
        .ToArray();

    private static bool IsValid(BenchmarkSample sample) =>
        sample.Operations > 0 &&
        sample.ElapsedNanoseconds > 0 &&
        sample.AllocatedBytes >= 0 &&
        (!sample.RoundTrips.HasValue || sample.RoundTrips.Value >= 0) &&
        sample.LogicalPayloadBytes >= 0 &&
        sample.LogicalMutations >= 0 &&
        ValidStorage(sample.StorageBefore) &&
        ValidStorage(sample.StorageAfter);

    private static bool ValidStorage(StorageSnapshot? snapshot) => snapshot is null ||
        snapshot.TotalBytes >= 0 &&
        snapshot.IndexBytes >= 0 &&
        snapshot.PrimaryRows >= 0 &&
        snapshot.LinkedRows >= 0;

    private static double AggregateStorageRatio(IReadOnlyList<StorageObservation> values) =>
        values.Sum(value => value.GrowthBytes) / (double)values.Sum(value => value.LogicalPayloadBytes);

    private static double[] Resample(IReadOnlyList<double> values, Random random)
    {
        var result = new double[values.Count];
        for (var index = 0; index < result.Length; index++)
            result[index] = values[random.Next(values.Count)];
        return result;
    }

    private static StorageObservation[] Resample(IReadOnlyList<StorageObservation> values, Random random)
    {
        var result = new StorageObservation[values.Count];
        for (var index = 0; index < result.Length; index++)
            result[index] = values[random.Next(values.Count)];
        return result;
    }

    private sealed record StorageObservation(long GrowthBytes, long LogicalPayloadBytes);
}
