namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BaselineEligibility(bool Eligible, IReadOnlyList<string> Diagnostics);

public static class BaselineEligibilityEvaluator
{
    public static BaselineEligibility Evaluate(
        BenchmarkRunConfiguration configuration,
        IReadOnlyList<BenchmarkWorkload> workloads,
        BenchmarkMachineMetadata machine,
        IReadOnlyList<BenchmarkCaseResult> cases)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(cases);

        var diagnostics = new List<string>();
        if (!UsesScheduledControls(configuration))
            diagnostics.Add("Baseline promotion requires the exact fixed scheduled profile controls.");
        if (!SameSet(configuration.Providers, Enum.GetValues<BenchmarkProvider>()))
            diagnostics.Add("Baseline promotion requires every provider.");
        if (!SameSet(configuration.StorageForms, Enum.GetValues<Groundwork.Core.PhysicalStorage.PhysicalStorageForm>()))
            diagnostics.Add("Baseline promotion requires every physical storage form.");
        if (!SameSet(workloads, Enum.GetValues<BenchmarkWorkload>()))
            diagnostics.Add("Baseline promotion requires every workload.");
        if (machine.GitDirty || machine.GitCommit.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add("Baseline promotion requires a known, clean Git commit.");

        var expected = BenchmarkMatrix.Create(BenchmarkProfiles.Scheduled)
            .Select(benchmarkCase => benchmarkCase.Identity)
            .ToHashSet(StringComparer.Ordinal);
        var actual = cases.Select(result => result.Case.Identity).ToArray();
        if (actual.Length != expected.Count ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !expected.SetEquals(actual))
            diagnostics.Add("Baseline promotion requires one result for every scheduled matrix case.");
        if (cases.Any(result => result.Samples.Count != BenchmarkProfiles.Scheduled.MeasurementIterations ||
                                result.Summary.SampleCount != BenchmarkProfiles.Scheduled.MeasurementIterations))
            diagnostics.Add($"Every baseline case requires exactly {BenchmarkProfiles.Scheduled.MeasurementIterations} measured samples.");
        if (cases.Any(result => !AllPassed(result.Correctness)))
            diagnostics.Add("Every baseline case requires all correctness gates to pass.");
        if (cases.Any(result => string.IsNullOrWhiteSpace(result.PlanArtifact)))
            diagnostics.Add("Every baseline case requires native plan evidence.");

        return new BaselineEligibility(diagnostics.Count == 0, diagnostics);
    }

    private static bool UsesScheduledControls(BenchmarkRunConfiguration configuration)
    {
        var scheduled = BenchmarkProfiles.Scheduled;
        return configuration.SchemaVersion == scheduled.SchemaVersion &&
               configuration.Mode == scheduled.Mode &&
               configuration.Seed == scheduled.Seed &&
               configuration.DatasetSize == scheduled.DatasetSize &&
               configuration.MigrationDatasetSize == scheduled.MigrationDatasetSize &&
               configuration.WarmupIterations == scheduled.WarmupIterations &&
               configuration.MeasurementIterations == scheduled.MeasurementIterations &&
               configuration.OperationsPerIteration == scheduled.OperationsPerIteration &&
               configuration.Concurrency == scheduled.Concurrency;
    }

    private static bool SameSet<T>(IEnumerable<T> actual, IEnumerable<T> expected) where T : notnull =>
        actual.ToHashSet().SetEquals(expected);

    private static bool AllPassed(CorrectnessGateResult result) =>
        result.ScopeIsolation &&
        result.OptimisticConcurrency &&
        result.UnitOfWorkRollback &&
        result.BoundedQuery &&
        result.MixedOrdering;
}
