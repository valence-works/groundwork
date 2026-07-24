namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkBaseline(
    IReadOnlyList<RawBenchmarkRecord> Records,
    BenchmarkRunManifest? Manifest,
    BenchmarkRunConfiguration? Configuration,
    BenchmarkMachineMetadata? Machine,
    IReadOnlyList<BenchmarkProviderMetadata>? Providers,
    ElsaMigrationEvidenceReport? EvidenceReport)
{
    public bool HasProvenance =>
        Manifest is not null &&
        Configuration is not null &&
        Machine is not null &&
        Providers is not null &&
        EvidenceReport is not null;
}

public sealed record BaselineCompatibility(bool IsCompatible, IReadOnlyList<string> Diagnostics);

public static class BaselineCompatibilityEvaluator
{
    public static BaselineCompatibility Evaluate(
        BenchmarkRunConfiguration candidateConfiguration,
        BenchmarkMachineMetadata candidateMachine,
        IReadOnlyList<BenchmarkProviderMetadata> candidateProviders,
        BenchmarkBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(candidateConfiguration);
        ArgumentNullException.ThrowIfNull(candidateMachine);
        ArgumentNullException.ThrowIfNull(candidateProviders);
        ArgumentNullException.ThrowIfNull(baseline);

        if (!baseline.HasProvenance)
        {
            return candidateConfiguration.Mode == BenchmarkRunMode.Scheduled
                ? new BaselineCompatibility(
                    false,
                    ["Scheduled comparison requires a complete v1 benchmark run directory; raw JSONL has no reproducibility provenance."])
                : new BaselineCompatibility(
                    true,
                    ["Raw JSONL comparison is diagnostic-only because machine, provider, and profile provenance is unavailable."]);
        }

        var diagnostics = new List<string>();
        var manifest = baseline.Manifest!;
        var baselineConfiguration = baseline.Configuration!;
        var baselineMachine = baseline.Machine!;
        var baselineProviders = baseline.Providers!;
        var evidence = baseline.EvidenceReport!;

        if (manifest.SchemaVersion != BenchmarkProfiles.SchemaVersion ||
            baselineConfiguration.SchemaVersion != BenchmarkProfiles.SchemaVersion ||
            evidence.SchemaVersion != BenchmarkProfiles.SchemaVersion)
            diagnostics.Add($"Baseline schema must be '{BenchmarkProfiles.SchemaVersion}'.");
        if (!manifest.Status.Equals("completed", StringComparison.Ordinal))
            diagnostics.Add("Baseline run manifest must have completed status.");
        if (manifest.Mode != baselineConfiguration.Mode || evidence.Mode != baselineConfiguration.Mode ||
            !manifest.RunId.Equals(evidence.RunId, StringComparison.Ordinal))
            diagnostics.Add("Baseline manifest, configuration, and evidence report are internally inconsistent.");
        if (!manifest.GitCommit.Equals(baselineMachine.GitCommit, StringComparison.Ordinal) ||
            manifest.GitDirty != baselineMachine.GitDirty)
            diagnostics.Add("Baseline manifest and machine Git provenance are internally inconsistent.");
        if (candidateConfiguration.Mode == BenchmarkRunMode.Scheduled &&
            (evidence.Readiness == BenchmarkEvidenceReadiness.Insufficient || !evidence.BaselineEligibility.Eligible))
            diagnostics.Add("Scheduled comparison cannot gate while the evidence report is insufficient and non-promotable.");

        CompareControls(candidateConfiguration, baselineConfiguration, diagnostics);
        CompareMachine(candidateMachine, baselineMachine, diagnostics);
        RequireProviderMetadata(candidateConfiguration.Providers, candidateProviders, "Candidate", diagnostics);
        RequireProviderMetadata(baselineConfiguration.Providers, baselineProviders, "Baseline", diagnostics);
        CompareProviders(candidateProviders, baselineProviders, diagnostics);
        ValidateRawRecords(baseline, baselineConfiguration, evidence, diagnostics);

        return new BaselineCompatibility(diagnostics.Count == 0, diagnostics);
    }

    private static void CompareControls(
        BenchmarkRunConfiguration candidate,
        BenchmarkRunConfiguration baseline,
        ICollection<string> diagnostics)
    {
        if (candidate.Mode != baseline.Mode ||
            candidate.Seed != baseline.Seed ||
            candidate.DatasetSize != baseline.DatasetSize ||
            candidate.DataShape != baseline.DataShape ||
            candidate.MigrationDatasetSize != baseline.MigrationDatasetSize ||
            candidate.WarmupIterations != baseline.WarmupIterations ||
            candidate.MeasurementIterations != baseline.MeasurementIterations ||
            candidate.MinimumMeasuredOperations != baseline.MinimumMeasuredOperations ||
            candidate.MinimumSteadyStateDurationSeconds != baseline.MinimumSteadyStateDurationSeconds ||
            candidate.OperationsPerIteration != baseline.OperationsPerIteration ||
            candidate.Concurrency != baseline.Concurrency)
            diagnostics.Add("Candidate and baseline fixed profile controls differ.");
        if (!candidate.Providers.All(baseline.Providers.Contains))
            diagnostics.Add("Baseline does not contain every selected candidate provider.");
        if (!candidate.StorageForms.All(baseline.StorageForms.Contains))
            diagnostics.Add("Baseline does not contain every selected candidate storage form.");
    }

    private static void CompareMachine(
        BenchmarkMachineMetadata candidate,
        BenchmarkMachineMetadata baseline,
        ICollection<string> diagnostics)
    {
        if (!candidate.OperatingSystem.Equals(baseline.OperatingSystem, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline operating system differ.");
        if (!candidate.MachineName.Equals(baseline.MachineName, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline machine identity differ.");
        if (!candidate.Architecture.Equals(baseline.Architecture, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline machine architecture differ.");
        if (!candidate.Framework.Equals(baseline.Framework, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline .NET runtime differ.");
        if (!candidate.BuildConfiguration.Equals(baseline.BuildConfiguration, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline build configuration differ.");
        if (candidate.ProcessorCount != baseline.ProcessorCount)
            diagnostics.Add("Candidate and baseline processor count differ.");
        if (candidate.ServerGc != baseline.ServerGc)
            diagnostics.Add("Candidate and baseline GC mode differ.");
        if (candidate.StopwatchFrequency != baseline.StopwatchFrequency)
            diagnostics.Add("Candidate and baseline stopwatch frequency differ.");
        if (!candidate.HarnessVersion.Equals(baseline.HarnessVersion, StringComparison.Ordinal))
            diagnostics.Add("Candidate and baseline harness version differ.");
    }

    private static void CompareProviders(
        IReadOnlyList<BenchmarkProviderMetadata> candidate,
        IReadOnlyList<BenchmarkProviderMetadata> baseline,
        ICollection<string> diagnostics)
    {
        var baselineByProvider = baseline
            .GroupBy(metadata => metadata.Provider)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var candidateProvider in candidate.GroupBy(metadata => metadata.Provider).Select(group => group.First()))
        {
            if (!baselineByProvider.TryGetValue(candidateProvider.Provider, out var baselineProvider))
            {
                diagnostics.Add($"Baseline provider metadata is missing {candidateProvider.Provider}.");
                continue;
            }
            if (!candidateProvider.Version.Equals(baselineProvider.Version, StringComparison.Ordinal))
                diagnostics.Add($"Candidate and baseline provider version differ for {candidateProvider.Provider}.");
            if (!SameConfiguration(candidateProvider.Configuration, baselineProvider.Configuration))
                diagnostics.Add($"Candidate and baseline provider configuration differ for {candidateProvider.Provider}.");
        }
    }

    private static void RequireProviderMetadata(
        IReadOnlyList<BenchmarkProvider> configured,
        IReadOnlyList<BenchmarkProviderMetadata> metadata,
        string source,
        ICollection<string> diagnostics)
    {
        var represented = metadata.Select(item => item.Provider).ToHashSet();
        foreach (var provider in configured.Where(provider => !represented.Contains(provider)))
            diagnostics.Add($"{source} provider metadata is missing {provider}.");
    }

    private static bool SameConfiguration(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count &&
        first.All(pair => second.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value, StringComparison.Ordinal));

    private static void ValidateRawRecords(
        BenchmarkBaseline baseline,
        BenchmarkRunConfiguration configuration,
        ElsaMigrationEvidenceReport evidence,
        ICollection<string> diagnostics)
    {
        var groups = baseline.Records
            .GroupBy(record => record.Case.Identity, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length == 0 || groups.Any(group =>
                group.Count() < configuration.MeasurementIterations ||
                group.Sum(record => (long)record.Sample.Operations) < configuration.MinimumMeasuredOperations ||
                group.Sum(record => record.Sample.ElapsedNanoseconds) <
                configuration.MinimumSteadyStateDurationSeconds * 1_000_000_000L))
        {
            diagnostics.Add(
                "Baseline raw measurements must satisfy the configured sample count, operation count, " +
                "and steady-state duration floors per case.");
        }
        if (baseline.Records.Any(record =>
                !configuration.Providers.Contains(record.Case.Provider) ||
                !configuration.StorageForms.Contains(record.Case.StorageForm)))
            diagnostics.Add("Baseline raw measurements contain a case outside the configured provider/form matrix.");
        if (baseline.Records.Any(record =>
                record.Sample.OperationLatencyNanoseconds is null ||
                record.Sample.OperationLatencyNanoseconds.Count != record.Sample.Operations ||
                record.Sample.OperationLatencyNanoseconds.Any(latency => latency <= 0)))
        {
            diagnostics.Add(
                "Baseline raw measurements must contain one positive raw latency observation per operation.");
        }

        var rawCases = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var evidenceCases = evidence.Cases.Select(item => item.CaseIdentity).ToArray();
        if (evidenceCases.Length != evidenceCases.Distinct(StringComparer.Ordinal).Count() ||
            !rawCases.SetEquals(evidenceCases))
            diagnostics.Add("Baseline raw measurements and evidence-report cases are internally inconsistent.");
        if (evidence.BaselineEligibility.Eligible)
        {
            var expectedCases = BenchmarkMatrix.Create(configuration)
                .Select(benchmarkCase => benchmarkCase.Identity)
                .ToHashSet(StringComparer.Ordinal);
            if (!rawCases.SetEquals(expectedCases))
                diagnostics.Add("Baseline-eligible provenance must contain the complete configured benchmark matrix.");
        }
    }
}
