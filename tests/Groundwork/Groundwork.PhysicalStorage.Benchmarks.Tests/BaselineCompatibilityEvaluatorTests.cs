using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BaselineCompatibilityEvaluatorTests
{
    private readonly BenchmarkRunConfiguration candidateConfiguration = BenchmarkProfiles.Scheduled with
    {
        Providers = [BenchmarkProvider.Sqlite]
    };
    private readonly BenchmarkMachineMetadata machine = new(
        "test-os", "benchmark-host", "Arm64", ".NET 10.0.8", "Release", 8, true, 1_000_000_000,
        "1.0.0", "0123456789abcdef", false, DateTimeOffset.UnixEpoch);
    private readonly BenchmarkProviderMetadata sqlite = new(
        BenchmarkProvider.Sqlite,
        "3.50.4",
        new Dictionary<string, string> { ["journal_mode"] = "wal" });

    [Fact]
    public void Scheduled_comparison_accepts_matching_controlled_provenance()
    {
        var baseline = Baseline(machine, sqlite, withMeasurements: true);

        var result = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            machine,
            [sqlite],
            baseline);

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Scheduled_comparison_rejects_raw_jsonl_without_provenance()
    {
        var baseline = new BenchmarkBaseline([], null, null, null, null, null);

        var result = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            machine,
            [sqlite],
            baseline);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("run directory", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduled_comparison_rejects_machine_or_provider_drift()
    {
        var baseline = Baseline(
            machine with { Architecture = "X64" },
            sqlite with { Version = "3.49.0" },
            withMeasurements: true);

        var result = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            machine,
            [sqlite],
            baseline);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("machine architecture", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("provider version", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduled_comparison_rejects_build_configuration_drift()
    {
        var baseline = Baseline(machine with { BuildConfiguration = "Debug" }, sqlite, withMeasurements: true);

        var result = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            machine,
            [sqlite],
            baseline);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("build configuration", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduled_comparison_rejects_missing_configured_provider_metadata()
    {
        var configuration = candidateConfiguration with { Providers = [BenchmarkProvider.PostgreSql] };

        var result = BaselineCompatibilityEvaluator.Evaluate(
            configuration,
            machine,
            [sqlite],
            Baseline(machine, sqlite, withMeasurements: true));

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Candidate provider metadata is missing PostgreSql", StringComparison.Ordinal));
    }

    [Fact]
    public void Smoke_comparison_allows_raw_jsonl_but_discloses_missing_provenance()
    {
        var result = BaselineCompatibilityEvaluator.Evaluate(
            BenchmarkProfiles.Smoke,
            machine,
            [sqlite],
            new BenchmarkBaseline([], null, null, null, null, null));

        Assert.True(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("diagnostic-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduled_comparison_rejects_inconsistent_raw_and_git_provenance()
    {
        var baseline = Baseline(machine, sqlite);
        baseline = baseline with { Manifest = baseline.Manifest! with { GitCommit = "different" } };

        var result = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            machine,
            [sqlite],
            baseline);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Git provenance", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("sample count", StringComparison.Ordinal));
    }

    private BenchmarkBaseline Baseline(
        BenchmarkMachineMetadata baselineMachine,
        BenchmarkProviderMetadata provider,
        bool withMeasurements = false)
    {
        var manifest = new BenchmarkRunManifest(
            BenchmarkProfiles.SchemaVersion,
            "baseline",
            "completed",
            BenchmarkRunMode.Scheduled,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            baselineMachine.GitCommit,
            baselineMachine.GitDirty,
            "raw/measurements.jsonl",
            "reports/summary.json",
            "reports/elsa-migration-decision.json",
            "metadata/machine.json",
            "metadata/providers.json",
            "metadata/configuration.json",
            [],
            null,
            false,
            null);
        var benchmarkCases = BenchmarkMatrix.Create(BenchmarkProfiles.Scheduled);
        var records = withMeasurements
            ? benchmarkCases.SelectMany(benchmarkCase =>
                Enumerable.Range(0, BenchmarkProfiles.Scheduled.MeasurementIterations)
                    .Select(iteration => new RawBenchmarkRecord(
                        benchmarkCase,
                        new BenchmarkSample(
                            iteration, 1, 1_000, 100, 1, 0, 0, null, null,
                            new Dictionary<string, long>()))))
                .ToArray()
            : [];
        var decision = new ElsaMigrationDecisionReport(
            BenchmarkProfiles.SchemaVersion,
            "baseline",
            BenchmarkRunMode.Scheduled,
            new BaselineEligibility(true, []),
            false,
            false,
            withMeasurements
                ? benchmarkCases.Select(benchmarkCase => new ElsaMigrationDecisionCase(
                        benchmarkCase.Identity,
                        benchmarkCase.Provider,
                        benchmarkCase.StorageForm,
                        benchmarkCase.Workload,
                        1_000,
                        1_000,
                        1_000,
                        1_000_000,
                        100,
                        1,
                        null,
                        null,
                        null,
                        $"plans/{benchmarkCase.Provider}/{benchmarkCase.StorageForm}/indexed-query.txt"))
                    .ToArray()
                : [],
            []);
        var providers = Enum.GetValues<BenchmarkProvider>()
            .Select(candidate => candidate == provider.Provider
                ? provider
                : new BenchmarkProviderMetadata(candidate, "test-version", new Dictionary<string, string>()))
            .ToArray();
        return new BenchmarkBaseline(
            records, manifest, BenchmarkProfiles.Scheduled, baselineMachine, providers, decision);
    }
}
