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
        var shape = report.DataShape ??
                    new BenchmarkDataShape(configuration.DatasetSize, 0, 5_000);
        shape.Validate();
        var results = report.Cases
            .OrderBy(result => result.Case.Identity, StringComparer.Ordinal)
            .Select(result =>
            {
                var provider = providers.Single(metadata => metadata.Provider == result.Case.Provider);
                var workloadIdentity = $"groundwork.physical-storage/{Kebab(result.Case.Workload.ToString())}";
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
                    Digest(new
                    {
                        Contract = "groundwork.physical-storage.observable-result/v1",
                        workloadIdentity,
                        workloadVersion,
                        DataShape = shape,
                        result.Correctness
                    }),
                    Digest(new
                    {
                        result.Correctness,
                        result.Summary,
                        RawSamplesDigest = rawSamplesDigest
                    }),
                    $"groundwork.{Kebab(result.Case.Provider.ToString())}",
                    provider.Version,
                    Digest(provider.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()),
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
}
