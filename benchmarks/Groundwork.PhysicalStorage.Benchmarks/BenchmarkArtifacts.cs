using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkMachineMetadata(
    string OperatingSystem,
    string MachineName,
    string Architecture,
    string Framework,
    string BuildConfiguration,
    int ProcessorCount,
    bool ServerGc,
    long StopwatchFrequency,
    string HarnessVersion,
    string GitCommit,
    bool GitDirty,
    DateTimeOffset CapturedAtUtc);

public sealed record BenchmarkProviderMetadata(
    BenchmarkProvider Provider,
    string Version,
    IReadOnlyDictionary<string, string> Configuration);

public sealed record BenchmarkRunFailure(string Type, string Message);

public sealed record BenchmarkRunManifest(
    string SchemaVersion,
    string RunId,
    string Status,
    BenchmarkRunMode Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string GitCommit,
    bool GitDirty,
    string RawMeasurements,
    string Summary,
    string ElsaMigrationEvidence,
    string MachineMetadata,
    string ProviderMetadata,
    string Configuration,
    IReadOnlyList<string> PlanArtifacts,
    string? BaselineRun,
    bool RegressionConfirmationRun,
    BenchmarkRunFailure? Failure);

public sealed record RawBenchmarkRecord(BenchmarkCase Case, BenchmarkSample Sample);

public sealed record BenchmarkCaseResult(
    BenchmarkCase Case,
    CorrectnessGateResult Correctness,
    string PlanArtifact,
    BenchmarkCaseSummary Summary,
    IReadOnlyList<BenchmarkSample> Samples);

public sealed record BenchmarkRunReport(
    string SchemaVersion,
    string RunId,
    BenchmarkRunMode Mode,
    IReadOnlyList<BenchmarkCaseResult> Cases,
    IReadOnlyList<RegressionEvaluation> Regressions,
    BaselineEligibility BaselineEligibility);

public sealed record ElsaMigrationEvidenceCase(
    string CaseIdentity,
    BenchmarkProvider Provider,
    Groundwork.Core.PhysicalStorage.PhysicalStorageForm StorageForm,
    BenchmarkWorkload Workload,
    double NormalizedBatchLatencyP50NanosecondsPerOperation,
    double NormalizedBatchLatencyP95NanosecondsPerOperation,
    double NormalizedBatchLatencyP99NanosecondsPerOperation,
    double ThroughputOperationsPerSecond,
    double AllocatedBytesPerOperation,
    double? RoundTripsPerOperation,
    long? StorageGrowthBytes,
    double? WriteAmplificationBytesPerLogicalByte,
    double? PhysicalRowsPerLogicalMutation,
    string PlanArtifact);

public enum BenchmarkEvidenceReadiness
{
    Insufficient
}

public sealed record ElsaMigrationEvidenceReport(
    string SchemaVersion,
    string RunId,
    BenchmarkRunMode Mode,
    BenchmarkEvidenceReadiness Readiness,
    bool ElsaEfOracleRequired,
    BaselineEligibility BaselineEligibility,
    bool RegressionSignalDetected,
    bool ConfirmationRunSuggested,
    IReadOnlyList<string> RemainingAcceptanceWork,
    IReadOnlyList<ElsaMigrationEvidenceCase> Cases,
    IReadOnlyList<RegressionEvaluation> Regressions)
{
    public static ElsaMigrationEvidenceReport From(BenchmarkRunReport report) => new(
        report.SchemaVersion,
        report.RunId,
        report.Mode,
        BenchmarkEvidenceReadiness.Insufficient,
        true,
        report.BaselineEligibility,
        report.Regressions.Any(regression => regression.Regressed),
        report.Regressions.Any(regression => regression.Regressed && regression.RequiresConfirmation),
        Issue50EvidenceRequirements.Remaining,
        report.Cases.Select(result => new ElsaMigrationEvidenceCase(
                result.Case.Identity,
                result.Case.Provider,
                result.Case.StorageForm,
                result.Case.Workload,
                result.Summary.NormalizedBatchLatencyP50NanosecondsPerOperation,
                result.Summary.NormalizedBatchLatencyP95NanosecondsPerOperation,
                result.Summary.NormalizedBatchLatencyP99NanosecondsPerOperation,
                result.Summary.ThroughputOperationsPerSecond,
                result.Summary.AllocatedBytesPerOperation,
                result.Summary.RoundTripsPerOperation,
                result.Summary.StorageGrowthBytes,
                result.Summary.WriteAmplificationBytesPerLogicalByte,
                result.Summary.PhysicalRowsPerLogicalMutation,
                result.PlanArtifact))
            .OrderBy(result => result.CaseIdentity, StringComparer.Ordinal)
            .ToArray(),
        report.Regressions);
}

public static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions CompactOptions { get; } = new(Options) { WriteIndented = false };

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class BenchmarkArtifactWriter : IAsyncDisposable
{
    private readonly StreamWriter rawWriter;

    public BenchmarkArtifactWriter(ArtifactLayout layout)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Layout.CreateDirectories();
        rawWriter = new StreamWriter(
            new FileStream(Layout.RawMeasurements, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public ArtifactLayout Layout { get; }

    public Task WriteManifestAsync(BenchmarkRunManifest manifest, CancellationToken cancellationToken) =>
        WriteJsonAsync(Layout.Manifest, manifest, cancellationToken);

    public Task WriteMachineAsync(BenchmarkMachineMetadata metadata, CancellationToken cancellationToken) =>
        WriteJsonAsync(Layout.MachineMetadata, metadata, cancellationToken);

    public Task WriteProvidersAsync(IReadOnlyList<BenchmarkProviderMetadata> providers, CancellationToken cancellationToken) =>
        WriteJsonAsync(Layout.ProviderMetadata, providers, cancellationToken);

    public Task WriteConfigurationAsync(BenchmarkRunConfiguration configuration, CancellationToken cancellationToken) =>
        WriteJsonAsync(Layout.Configuration, configuration, cancellationToken);

    public async Task AppendSampleAsync(RawBenchmarkRecord record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, BenchmarkJson.CompactOptions);
        await rawWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    public async Task<string> WritePlanAsync(
        BenchmarkCase benchmarkCase,
        NativePlanEvidence evidence,
        CancellationToken cancellationToken)
    {
        var extension = benchmarkCase.Provider switch
        {
            BenchmarkProvider.SqlServer => "xml",
            BenchmarkProvider.PostgreSql or BenchmarkProvider.MongoDb => "json",
            _ => "txt"
        };
        var path = Layout.Plan(benchmarkCase, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, evidence.NativePlan, cancellationToken);
        await WriteJsonAsync(path + ".assertions.json", evidence, cancellationToken);
        return Path.GetRelativePath(Layout.Root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    public async Task WriteReportAsync(BenchmarkRunReport report, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(Layout.SummaryJson, report, cancellationToken);
        await WriteJsonAsync(Layout.RegressionJson, report.Regressions, cancellationToken);
        await WriteJsonAsync(
            Layout.ElsaMigrationEvidenceJson,
            ElsaMigrationEvidenceReport.From(report),
            cancellationToken);
        await WriteTextAsync(Layout.SummaryMarkdown, Markdown(report), cancellationToken);
    }

    public async ValueTask DisposeAsync() => await rawWriter.DisposeAsync();

    public static async Task<IReadOnlyList<RawBenchmarkRecord>> ReadRawAsync(
        string runOrRawPath,
        CancellationToken cancellationToken)
    {
        var path = Directory.Exists(runOrRawPath)
            ? Path.Combine(runOrRawPath, "raw", "measurements.jsonl")
            : runOrRawPath;
        if (!File.Exists(path))
            throw new FileNotFoundException("Baseline raw measurements were not found.", path);
        var records = new List<RawBenchmarkRecord>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            records.Add(JsonSerializer.Deserialize<RawBenchmarkRecord>(line, BenchmarkJson.CompactOptions)
                        ?? throw new InvalidOperationException($"Baseline record in '{path}' is null."));
        }
        return records;
    }

    public static async Task<BenchmarkBaseline> ReadBaselineAsync(
        string runOrRawPath,
        CancellationToken cancellationToken)
    {
        var records = await ReadRawAsync(runOrRawPath, cancellationToken);
        if (!Directory.Exists(runOrRawPath))
            return new BenchmarkBaseline(records, null, null, null, null, null);

        var root = Path.GetFullPath(runOrRawPath);
        return new BenchmarkBaseline(
            records,
            await ReadJsonAsync<BenchmarkRunManifest>(Path.Combine(root, "manifest.json"), cancellationToken),
            await ReadJsonAsync<BenchmarkRunConfiguration>(Path.Combine(root, "metadata", "configuration.json"), cancellationToken),
            await ReadJsonAsync<BenchmarkMachineMetadata>(Path.Combine(root, "metadata", "machine.json"), cancellationToken),
            await ReadJsonAsync<IReadOnlyList<BenchmarkProviderMetadata>>(Path.Combine(root, "metadata", "providers.json"), cancellationToken),
            await ReadJsonAsync<ElsaMigrationEvidenceReport>(Path.Combine(root, "reports", "elsa-migration-evidence.json"), cancellationToken));
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, BenchmarkJson.Options, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task WriteTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Baseline provenance artifact was not found.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, BenchmarkJson.Options, cancellationToken)
               ?? throw new InvalidOperationException($"Baseline provenance artifact '{path}' is null.");
    }

    private static string Markdown(BenchmarkRunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Groundwork physical-storage benchmark report");
        builder.AppendLine();
        builder.AppendLine($"Schema: `{report.SchemaVersion}`  ");
        builder.AppendLine($"Run: `{report.RunId}`  ");
        builder.AppendLine($"Mode: `{report.Mode}`");
        builder.AppendLine($"Baseline eligible: `{report.BaselineEligibility.Eligible}`");
        builder.AppendLine();
        builder.AppendLine("| Provider | Form | Workload | normalized batch p50 (ms/op) | normalized batch p95 (ms/op) | normalized batch p99 (ms/op) | ops/s | B/op | round trips/op | storage delta | write amp | plan |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Cases.OrderBy(result => result.Case.Identity, StringComparer.Ordinal))
        {
            var summary = result.Summary;
            builder.AppendLine(
                $"| {result.Case.Provider} | {result.Case.StorageForm} | {result.Case.Workload} | " +
                $"{summary.NormalizedBatchLatencyP50NanosecondsPerOperation / 1_000_000:F3} | {summary.NormalizedBatchLatencyP95NanosecondsPerOperation / 1_000_000:F3} | " +
                $"{summary.NormalizedBatchLatencyP99NanosecondsPerOperation / 1_000_000:F3} | {summary.ThroughputOperationsPerSecond:F1} | " +
                $"{summary.AllocatedBytesPerOperation:F1} | {Format(summary.RoundTripsPerOperation)} | " +
                $"{Format(summary.StorageGrowthBytes)} | {Format(summary.WriteAmplificationBytesPerLogicalByte)} | " +
                $"[{Path.GetFileName(result.PlanArtifact)}](../{result.PlanArtifact}) |");
        }
        builder.AppendLine();
        builder.AppendLine("`null`/blank round-trip values mean no explicit, diagnostic-command, or database-client-activity signal was observable; raw provider-work flags identify the signal source when one was available.");
        if (!report.BaselineEligibility.Eligible)
        {
            builder.AppendLine();
            builder.AppendLine("## Baseline eligibility");
            builder.AppendLine();
            foreach (var diagnostic in report.BaselineEligibility.Diagnostics)
                builder.AppendLine($"- {diagnostic}");
        }
        if (report.Regressions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Regression evaluation");
            builder.AppendLine();
            foreach (var evaluation in report.Regressions)
            {
                builder.AppendLine($"- `{evaluation.CaseIdentity}`: " +
                                   (evaluation.IsComparable
                                       ? evaluation.Regressed ? "diagnostic regression signal" : "within diagnostic budget"
                                       : string.Join(" ", evaluation.Diagnostics)));
            }
        }
        return builder.ToString();
    }

    private static string Format(double? value) => value?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Format(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}

public static class BenchmarkMetadata
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    public static BenchmarkMachineMetadata Capture(string repositoryRoot)
    {
        var git = ReadGit(repositoryRoot);
        return new BenchmarkMachineMetadata(
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            BuildConfiguration,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            Stopwatch.Frequency,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            git.Commit,
            git.Dirty,
            DateTimeOffset.UtcNow);
    }

    private static (string Commit, bool Dirty) ReadGit(string repositoryRoot)
    {
        try
        {
            var commit = RunGit(repositoryRoot, "rev-parse HEAD").Trim();
            var dirty = !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status --porcelain"));
            return (commit, dirty);
        }
        catch
        {
            return ("unknown", true);
        }
    }

    private static string RunGit(string repositoryRoot, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Unable to start git.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        return output;
    }
}
