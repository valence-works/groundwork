using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkConsumerEvidenceResult(
    string WorkloadIdentity,
    string WorkloadVersion,
    string MeasurementProtocol,
    string WorkloadFingerprint,
    string ResultDigest,
    string MeasurementDigest,
    string ProviderIdentity,
    string ProviderVersion,
    string ProviderConfigurationDigest,
    PhysicalStorageForm StorageForm,
    BenchmarkDataShape DataShape,
    int IndependentRun,
    int RawSampleCount,
    int RawOperationLatencyCount,
    string RawSamplesDigest,
    IReadOnlyList<string> NativePlanArtifacts,
    string NativePlanDigest);

public sealed record BenchmarkConsumerEvidenceReport(
    string SchemaVersion,
    string RunId,
    bool Promotable,
    bool ExternalOracleJoinRequired,
    string GitCommit,
    bool GitDirty,
    string RawMeasurements,
    string RawMeasurementsDigest,
    IReadOnlyList<string> NonPromotableReasons,
    IReadOnlyList<BenchmarkConsumerEvidenceResult> Results)
{
    public const string ContractVersion = "groundwork.physical-storage.consumer-evidence/v1";
    public const string MeasurementProtocol = "direct-operation-latency/v1";

    public static BenchmarkConsumerEvidenceReport Create(
        BenchmarkRunReport report,
        BenchmarkRunConfiguration configuration,
        BenchmarkMachineMetadata machine,
        IReadOnlyList<BenchmarkProviderMetadata> providers,
        ArtifactLayout layout,
        int independentRun = 1)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(layout);
        var results = CreateResults(report, configuration, machine, providers, layout, independentRun);

        return new BenchmarkConsumerEvidenceReport(
            ContractVersion,
            report.RunId,
            Promotable: false,
            ExternalOracleJoinRequired: true,
            machine.GitCommit,
            machine.GitDirty,
            layout.RelativePath(layout.RawMeasurements),
            DigestFile(layout.RawMeasurements),
            report.BaselineEligibility.Diagnostics
                .Append("The external oracle join and accepted-shape verdict are required before promotion.")
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            results);
    }

    public static IReadOnlyList<BenchmarkConsumerEvidenceResult> CreateResults(
        BenchmarkRunReport report,
        BenchmarkRunConfiguration configuration,
        BenchmarkMachineMetadata machine,
        IReadOnlyList<BenchmarkProviderMetadata> providers,
        ArtifactLayout layout,
        int independentRun = 1)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(layout);
        var shape = report.DataShape ?? new BenchmarkDataShape(configuration.DatasetSize, 0, 5_000);
        shape.Validate();
        var results = report.Cases
            .OrderBy(result => result.Case.Identity, StringComparer.Ordinal)
            .Select(result =>
            {
                var provider = providers.Single(metadata => metadata.Provider == result.Case.Provider);
                var workloadIdentity = WorkloadIdentity(result.Case.Workload);
                var workloadVersion = "1.1";
                var fingerprint = Digest(new
                {
                    workloadIdentity,
                    workloadVersion,
                    MeasurementProtocol,
                    DataShape = shape,
                    configuration.Seed,
                    configuration.WarmupIterations,
                    configuration.MeasurementIterations,
                    configuration.MinimumMeasuredOperations,
                    configuration.MinimumSteadyStateDurationSeconds,
                    configuration.OperationsPerIteration,
                    configuration.Concurrency
                });
                var rawSamplesDigest = Digest(result.Samples);
                var observableResultDigest = RequireObservableResultDigest(result);
                var nativePlanArtifacts = result.PlanArtifacts
                    .SelectMany(artifact => new[] { artifact, $"{artifact}.assertions.json" })
                    .Select(artifact => RequireArtifact(layout, artifact))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var nativePlanDigest = DigestFiles(layout, nativePlanArtifacts);
                return new BenchmarkConsumerEvidenceResult(
                    workloadIdentity,
                    workloadVersion,
                    MeasurementProtocol,
                    fingerprint,
                    observableResultDigest,
                    Digest(new
                    {
                        result.Correctness,
                        result.Summary,
                        RawSamplesDigest = rawSamplesDigest
                    }),
                    ProviderIdentity(result.Case.Provider),
                    provider.Version,
                    Digest(new
                    {
                        Configuration = provider.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                        EffectiveSettings = provider.EffectiveSettings.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
                    }),
                    result.Case.StorageForm,
                    shape,
                    independentRun,
                    result.Samples.Count,
                    result.Samples.Sum(sample => sample.OperationLatencyNanoseconds.Count),
                    rawSamplesDigest,
                    nativePlanArtifacts,
                    nativePlanDigest);
            })
            .ToArray();

        return results;
    }

    /// <summary>
    /// Rebuilds every claimed digest from the immutable run inputs. Hashing the
    /// consumer-evidence file alone is insufficient: otherwise its result and plan
    /// claims could remain internally stale after another artifact was modified.
    /// </summary>
    public static void VerifyBoundClaims(
        BenchmarkRunReport report,
        BenchmarkRunConfiguration configuration,
        BenchmarkMachineMetadata machine,
        IReadOnlyList<BenchmarkProviderMetadata> providers,
        ArtifactLayout layout,
        BenchmarkConsumerEvidenceReport actual)
    {
        var independentRuns = actual.Results
            .Select(result => result.IndependentRun)
            .Distinct()
            .ToArray();
        if (independentRuns.Length > 1 ||
            independentRuns.Length == 1 && independentRuns[0] <= 0)
        {
            throw new InvalidOperationException(
                "Consumer evidence must bind exactly one positive independent-run identity.");
        }
        var expected = Create(
            report,
            configuration,
            machine,
            providers,
            layout,
            independentRuns.Length == 0 ? 1 : independentRuns[0]);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(expected, BenchmarkJson.CompactOptions)),
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(actual, BenchmarkJson.CompactOptions))))
        {
            throw new InvalidOperationException(
                "Consumer evidence digest claims do not match the bound run artifacts.");
        }
    }

    private static string RequireObservableResultDigest(
        BenchmarkCaseResult result)
    {
        if (result.ObservableResults is null || result.ObservableResults.Count == 0)
        {
            throw new InvalidOperationException(
                $"Consumer evidence for '{result.Case.Identity}' requires a non-empty observable result vector.");
        }
        try
        {
            return BenchmarkObservableResultVector.Create(result.ObservableResults).Digest;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Consumer evidence for '{result.Case.Identity}' has a non-canonical observable result vector.",
                exception);
        }
    }

    private static string DigestFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string DigestFiles(ArtifactLayout layout, IEnumerable<string> artifacts)
    {
        var digests = artifacts
            .Order(StringComparer.Ordinal)
            .Select(artifact => new
            {
                Path = artifact,
                Digest = DigestFile(ResolveArtifact(layout, artifact))
            })
            .ToArray();
        return Digest(digests);
    }

    private static string RequireArtifact(ArtifactLayout layout, string artifact)
    {
        var path = ResolveArtifact(layout, artifact);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Native-plan evidence artifact '{artifact}' was not found.", path);
        return layout.RelativePath(path);
    }

    private static string ResolveArtifact(ArtifactLayout layout, string artifact)
    {
        if (Path.IsPathRooted(artifact))
            throw new InvalidOperationException("Evidence artifact paths must be relative to the run root.");
        var root = Path.GetFullPath(layout.Root);
        var path = Path.GetFullPath(Path.Combine(
            root,
            artifact.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Evidence artifact '{artifact}' escapes the run root.");
        return path;
    }

    private static string Digest<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BenchmarkJson.CompactOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string Kebab(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    internal static string ProviderIdentity(BenchmarkProvider provider) =>
        $"groundwork.{Kebab(provider.ToString())}";

    internal static string WorkloadIdentity(BenchmarkWorkload workload) =>
        $"groundwork.physical-storage/{Kebab(workload.ToString())}";
}
