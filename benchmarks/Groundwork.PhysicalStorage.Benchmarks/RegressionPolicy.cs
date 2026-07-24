namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record RegressionPolicy(
    int MinimumSamples,
    int MinimumIndependentRuns,
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
        MinimumIndependentRuns: 1,
        BootstrapIterations: 1_000,
        ConfidenceLevel: 0.95,
        LatencyBudget: 1.00,
        ThroughputBudget: 0.50,
        AllocationBudget: 0.50,
        StorageBudget: 0.50,
        RequiresConfirmation: false);

    public static RegressionPolicy Scheduled { get; } = new(
        MinimumSamples: 20,
        MinimumIndependentRuns: 3,
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
        => CompareIndependentRuns(
            caseIdentity,
            [baseline],
            [candidate],
            policy with { MinimumIndependentRuns = 1 },
            seed);

    public static RegressionEvaluation CompareIndependentRuns(
        string caseIdentity,
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> baselineRuns,
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> candidateRuns,
        RegressionPolicy policy,
        int seed = BenchmarkProfiles.ReproducibleSeed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseIdentity);
        ArgumentNullException.ThrowIfNull(baselineRuns);
        ArgumentNullException.ThrowIfNull(candidateRuns);
        ArgumentNullException.ThrowIfNull(policy);
        if (baselineRuns.Count < policy.MinimumIndependentRuns ||
            candidateRuns.Count < policy.MinimumIndependentRuns)
        {
            return new RegressionEvaluation(
                caseIdentity,
                false,
                policy.RequiresConfirmation,
                [],
                [$"At least {policy.MinimumIndependentRuns} independent baseline and candidate processes are required."]);
        }
        if (baselineRuns.Any(run => run.Any(sample => !IsValid(sample))) ||
            candidateRuns.Any(run => run.Any(sample => !IsValid(sample))))
        {
            return new RegressionEvaluation(
                caseIdentity,
                false,
                policy.RequiresConfirmation,
                [],
                ["Baseline or candidate contains an invalid raw sample."]);
        }
        if (baselineRuns.Any(run => OperationLatencies(run).Length < policy.MinimumSamples) ||
            candidateRuns.Any(run => OperationLatencies(run).Length < policy.MinimumSamples))
        {
            return new RegressionEvaluation(
                caseIdentity,
                false,
                policy.RequiresConfirmation,
                [],
                [$"At least {policy.MinimumSamples} baseline and candidate operation-latency observations are required."]);
        }

        var diagnostics = new List<string>();
        var metrics = new List<RegressionMetric>
        {
            HierarchicalBootstrapMetric(
                "operation_latency_p50_ns", MetricDirection.LowerIsBetter, baselineRuns, candidateRuns,
                static samples => OperationLatencies(samples),
                0.50, policy.LatencyBudget, policy, seed),
            HierarchicalBootstrapMetric(
                "operation_latency_p95_ns", MetricDirection.LowerIsBetter, baselineRuns, candidateRuns,
                static samples => OperationLatencies(samples),
                0.95, policy.LatencyBudget, policy, seed + 1),
            HierarchicalBootstrapMetric(
                "throughput_ops_per_second", MetricDirection.HigherIsBetter,
                baselineRuns, candidateRuns,
                static samples => samples.Select(sample => sample.ThroughputOperationsPerSecond).ToArray(),
                0.50, policy.ThroughputBudget, policy, seed + 2)
        };
        if (baselineRuns.All(run => run.All(sample => sample.AllocatedBytesPerOperation > 0)))
        {
            metrics.Add(HierarchicalBootstrapMetric(
                "allocated_bytes_per_op", MetricDirection.LowerIsBetter,
                baselineRuns, candidateRuns,
                static samples => samples.Select(sample => sample.AllocatedBytesPerOperation).ToArray(),
                0.50,
                policy.AllocationBudget, policy, seed + 3));
        }
        else
        {
            diagnostics.Add("Allocation regression was not evaluated: baseline allocation contains a zero value.");
        }

        AddStorageMetric(baselineRuns, candidateRuns, policy, seed + 4, metrics, diagnostics);

        return new RegressionEvaluation(caseIdentity, true, policy.RequiresConfirmation, metrics, diagnostics);
    }

    private static RegressionMetric HierarchicalBootstrapMetric(
        string name,
        MetricDirection direction,
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> baselineRuns,
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> candidateRuns,
        Func<IReadOnlyList<BenchmarkSample>, double[]> observations,
        double percentile,
        double budget,
        RegressionPolicy policy,
        int seed)
    {
        var baselineValue = MedianProcessStatistic(baselineRuns, observations, percentile);
        var candidateValue = MedianProcessStatistic(candidateRuns, observations, percentile);
        var ratios = new double[policy.BootstrapIterations];
        var random = new Random(seed);
        for (var iteration = 0; iteration < ratios.Length; iteration++)
        {
            var baselineValueResampled = HierarchicalResampleStatistic(
                baselineRuns, observations, percentile, random);
            var candidateValueResampled = HierarchicalResampleStatistic(
                candidateRuns, observations, percentile, random);
            var denominator = baselineValueResampled;
            ratios[iteration] = denominator <= 0
                ? double.PositiveInfinity
                : candidateValueResampled / denominator;
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
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> baselineRuns,
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> candidateRuns,
        RegressionPolicy policy,
        int seed,
        ICollection<RegressionMetric> metrics,
        ICollection<string> diagnostics)
    {
        var baselineStorage = baselineRuns.Select(StorageObservations).ToArray();
        var candidateStorage = candidateRuns.Select(StorageObservations).ToArray();
        if (baselineStorage.All(run => run.Length == 0) && candidateStorage.All(run => run.Length == 0))
            return;
        if (baselineStorage.Any(run => run.Length < policy.MinimumSamples) ||
            candidateStorage.Any(run => run.Length < policy.MinimumSamples))
        {
            diagnostics.Add(
                $"Storage regression was not evaluated: at least {policy.MinimumSamples} storage observations are required.");
            return;
        }

        var baselineValue = BenchmarkStatistics.Percentile(
            baselineStorage.Select(AggregateStorageRatio).ToArray(), 0.50);
        if (baselineValue <= 0)
        {
            diagnostics.Add("Storage regression was not evaluated: the baseline has no positive storage growth.");
            return;
        }

        var candidateValue = BenchmarkStatistics.Percentile(
            candidateStorage.Select(AggregateStorageRatio).ToArray(), 0.50);
        var ratios = new double[policy.BootstrapIterations];
        var random = new Random(seed);
        var unstableBaseline = false;
        for (var iteration = 0; iteration < ratios.Length; iteration++)
        {
            var denominator = HierarchicalStorageStatistic(baselineStorage, random);
            if (denominator <= 0)
            {
                unstableBaseline = true;
                break;
            }
            ratios[iteration] = HierarchicalStorageStatistic(candidateStorage, random) / denominator;
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
        sample.OperationLatencyNanoseconds is not null &&
        sample.OperationLatencyNanoseconds.Count == sample.Operations &&
        sample.OperationLatencyNanoseconds.All(latency => latency > 0) &&
        sample.AllocatedBytes >= 0 &&
        (!sample.RoundTrips.HasValue || sample.RoundTrips.Value >= 0) &&
        sample.LogicalPayloadBytes >= 0 &&
        sample.LogicalMutations >= 0 &&
        ValidStorage(sample.StorageBefore) &&
        ValidStorage(sample.StorageAfter);

    private static double[] OperationLatencies(IEnumerable<BenchmarkSample> samples) =>
        samples
            .SelectMany(sample => sample.OperationLatencyNanoseconds ?? [])
            .Select(latency => (double)latency)
            .ToArray();

    private static bool ValidStorage(StorageSnapshot? snapshot) => snapshot is null ||
        snapshot.TotalBytes >= 0 &&
        snapshot.IndexBytes >= 0 &&
        snapshot.PrimaryRows >= 0 &&
        snapshot.LinkedRows >= 0;

    private static double AggregateStorageRatio(IReadOnlyList<StorageObservation> values) =>
        values.Sum(value => value.GrowthBytes) / (double)values.Sum(value => value.LogicalPayloadBytes);

    private static double MedianProcessStatistic(
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> runs,
        Func<IReadOnlyList<BenchmarkSample>, double[]> observations,
        double percentile) =>
        BenchmarkStatistics.Percentile(
            runs.Select(run => BenchmarkStatistics.Percentile(observations(run), percentile)).ToArray(),
            0.50);

    private static double HierarchicalResampleStatistic(
        IReadOnlyList<IReadOnlyList<BenchmarkSample>> runs,
        Func<IReadOnlyList<BenchmarkSample>, double[]> observations,
        double percentile,
        Random random)
    {
        var processStatistics = new double[runs.Count];
        for (var index = 0; index < processStatistics.Length; index++)
        {
            var selectedProcess = runs[random.Next(runs.Count)];
            processStatistics[index] = BenchmarkStatistics.Percentile(
                Resample(observations(selectedProcess), random),
                percentile);
        }
        return BenchmarkStatistics.Percentile(processStatistics, 0.50);
    }

    private static double HierarchicalStorageStatistic(
        IReadOnlyList<StorageObservation[]> runs,
        Random random)
    {
        var processStatistics = new double[runs.Count];
        for (var index = 0; index < processStatistics.Length; index++)
        {
            var selectedProcess = runs[random.Next(runs.Count)];
            processStatistics[index] = AggregateStorageRatio(Resample(selectedProcess, random));
        }
        return BenchmarkStatistics.Percentile(processStatistics, 0.50);
    }

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
