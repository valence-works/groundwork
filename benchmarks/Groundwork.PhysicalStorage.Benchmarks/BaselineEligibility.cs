namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BaselineEligibility(bool Eligible, IReadOnlyList<string> Diagnostics);

public static class Issue50EvidenceRequirements
{
    public static IReadOnlyList<string> Remaining { get; } =
    [
        "The ratified 1K/100K/1M dataset matrix across payload sizes and query selectivity values has not been executed.",
        "exact-HEAD live evidence from all four providers (SQLite, SQL Server, PostgreSQL, and MongoDB) is incomplete.",
        "The Elsa-owned EF Core oracle and entity-form benefit classification have not been joined to this Groundwork evidence.",
        "Provider database-work signals, concurrent-load evidence, and an approved immutable baseline are incomplete."
    ];
}

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

        // The current profiles are harness-scaffolding controls. Even a complete run is not
        // evidence for baseline promotion or an Elsa migration decision until #50's ratified
        // matrix and external EF oracle are supplied.
        var diagnostics = new List<string>(Issue50EvidenceRequirements.Remaining);
        if (!UsesScheduledControls(configuration))
            diagnostics.Add("Future baseline activation also requires the exact fixed scheduled profile controls.");
        if (!SameSet(configuration.Providers, Enum.GetValues<BenchmarkProvider>()))
            diagnostics.Add("Future baseline activation also requires every provider.");
        if (!SameSet(configuration.StorageForms, Enum.GetValues<Groundwork.Core.PhysicalStorage.PhysicalStorageForm>()))
            diagnostics.Add("Future baseline activation also requires every physical storage form.");
        if (!SameSet(workloads, Enum.GetValues<BenchmarkWorkload>()))
            diagnostics.Add("Future baseline activation also requires every workload.");
        if (machine.GitDirty || machine.GitCommit.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add("Future baseline activation also requires a known, clean Git commit.");

        var expected = BenchmarkMatrix.Create(BenchmarkProfiles.Scheduled)
            .Select(benchmarkCase => benchmarkCase.Identity)
            .ToHashSet(StringComparer.Ordinal);
        var actual = cases.Select(result => result.Case.Identity).ToArray();
        if (actual.Length != expected.Count ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !expected.SetEquals(actual))
            diagnostics.Add("Future baseline activation also requires one result for every scheduled-scaffold case.");
        if (cases.Any(result => !MeetsMeasurementFloors(result, BenchmarkProfiles.Scheduled)))
        {
            diagnostics.Add(
                "Future baseline activation also requires every case to satisfy the scheduled " +
                "sample-count, operation-count, and steady-state duration floors.");
        }
        if (cases.Any(result => !AllPassed(result.Correctness)))
            diagnostics.Add("Future baseline activation also requires all correctness gates to pass.");
        if (cases.Any(result =>
                result.PlanArtifacts.Count != BenchmarkPlanRequests.ForWorkloads([result.Case.Workload]).Count ||
                result.PlanArtifacts.Any(string.IsNullOrWhiteSpace) ||
                result.PlanArtifacts.Distinct(StringComparer.Ordinal).Count() != result.PlanArtifacts.Count))
            diagnostics.Add("Future baseline activation also requires every applicable native query-plan shape and no plans on non-query cases.");

        return new BaselineEligibility(false, diagnostics);
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
               configuration.MinimumMeasuredOperations == scheduled.MinimumMeasuredOperations &&
               configuration.MinimumSteadyStateDurationSeconds == scheduled.MinimumSteadyStateDurationSeconds &&
               configuration.OperationsPerIteration == scheduled.OperationsPerIteration &&
               configuration.Concurrency == scheduled.Concurrency;
    }

    private static bool MeetsMeasurementFloors(
        BenchmarkCaseResult result,
        BenchmarkRunConfiguration configuration) =>
        result.Samples.Count >= configuration.MeasurementIterations &&
        result.Summary.SampleCount == result.Samples.Count &&
        result.Summary.OperationLatencyObservationCount ==
        result.Samples.Sum(sample => sample.OperationLatencyNanoseconds?.Count ?? 0) &&
        result.Samples.Sum(sample => (long)sample.Operations) >= configuration.MinimumMeasuredOperations &&
        result.Samples.All(sample =>
            sample.OperationLatencyNanoseconds is not null &&
            sample.OperationLatencyNanoseconds.Count == sample.Operations &&
            sample.OperationLatencyNanoseconds.All(latency => latency > 0)) &&
        result.Samples.Sum(sample => sample.ElapsedNanoseconds) >=
        configuration.MinimumSteadyStateDurationSeconds * 1_000_000_000L;

    private static bool SameSet<T>(IEnumerable<T> actual, IEnumerable<T> expected) where T : notnull =>
        actual.ToHashSet().SetEquals(expected);

    private static bool AllPassed(CorrectnessGateResult result) =>
        result.ScopeIsolation &&
        result.OptimisticConcurrency &&
        result.UnitOfWorkRollback &&
        result.BoundedQuery &&
        result.MixedOrdering;
}
