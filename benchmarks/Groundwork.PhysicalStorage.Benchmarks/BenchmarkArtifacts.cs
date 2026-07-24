using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    DateTimeOffset CapturedAtUtc)
{
    public string GitTreeDigest { get; init; } = "unavailable";
    public string CpuModel { get; init; } = "unavailable";
    public string Memory { get; init; } = "unavailable";
    public string Storage { get; init; } = "unavailable";
    public string PowerManagement { get; init; } = "unavailable";
}

public sealed record BenchmarkGitState(string Commit, bool Dirty, string TreeDigest);

public sealed record BenchmarkProviderMetadata(
    BenchmarkProvider Provider,
    string Version,
    IReadOnlyDictionary<string, string> Configuration)
{
    public IReadOnlyDictionary<string, string> EffectiveSettings { get; init; } =
        new Dictionary<string, string>
        {
            ["captureStatus"] = "unavailable:not-exposed-by-provider-target"
        };
}

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
    BenchmarkRunFailure? Failure,
    string? ConsumerEvidence = null);

public sealed record RawBenchmarkRecord(BenchmarkCase Case, BenchmarkSample Sample);

public sealed record BenchmarkCaseResult(
    BenchmarkCase Case,
    CorrectnessGateResult Correctness,
    IReadOnlyList<string> PlanArtifacts,
    BenchmarkCaseSummary Summary,
    IReadOnlyList<BenchmarkSample> Samples,
    IReadOnlyList<BenchmarkObservableResult>? ObservableResults = null);

public sealed record BenchmarkRunReport(
    string SchemaVersion,
    string RunId,
    BenchmarkRunMode Mode,
    IReadOnlyList<BenchmarkCaseResult> Cases,
    IReadOnlyList<RegressionEvaluation> Regressions,
    BaselineEligibility BaselineEligibility,
    BenchmarkDataShape? DataShape = null);

public sealed record ElsaMigrationEvidenceCase(
    string CaseIdentity,
    BenchmarkProvider Provider,
    Groundwork.Core.PhysicalStorage.PhysicalStorageForm StorageForm,
    BenchmarkWorkload Workload,
    int OperationLatencyObservationCount,
    double OperationLatencyP50Nanoseconds,
    double OperationLatencyP95Nanoseconds,
    double OperationLatencyP99Nanoseconds,
    double ThroughputOperationsPerSecond,
    double AllocatedBytesPerOperation,
    double? RoundTripsPerOperation,
    long? StorageGrowthBytes,
    double? NetStorageGrowthBytesPerLogicalPayloadByte,
    double? NetPhysicalRowGrowthPerLogicalMutation,
    IReadOnlyList<string> PlanArtifacts);

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
                result.Summary.OperationLatencyObservationCount,
                result.Summary.OperationLatencyP50Nanoseconds,
                result.Summary.OperationLatencyP95Nanoseconds,
                result.Summary.OperationLatencyP99Nanoseconds,
                result.Summary.ThroughputOperationsPerSecond,
                result.Summary.AllocatedBytesPerOperation,
                result.Summary.RoundTripsPerOperation,
                result.Summary.StorageGrowthBytes,
                result.Summary.NetStorageGrowthBytesPerLogicalPayloadByte,
                result.Summary.NetPhysicalRowGrowthPerLogicalMutation,
                result.PlanArtifacts))
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

    public Task WriteConsumerEvidenceAsync(
        BenchmarkConsumerEvidenceReport report,
        CancellationToken cancellationToken) =>
        WriteImmutableJsonAsync(Layout.ConsumerEvidenceJson, report, cancellationToken);

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
        ArgumentNullException.ThrowIfNull(benchmarkCase);
        ArgumentNullException.ThrowIfNull(evidence);
        if (benchmarkCase.Workload != evidence.Request.Workload)
        {
            throw new InvalidOperationException(
                $"Plan evidence for '{evidence.Request.Workload}' cannot be written for case '{benchmarkCase.Identity}'.");
        }
        var extension = benchmarkCase.Provider switch
        {
            BenchmarkProvider.SqlServer => "xml",
            BenchmarkProvider.PostgreSql or BenchmarkProvider.MongoDb => "json",
            _ => "txt"
        };
        var path = Layout.Plan(benchmarkCase, evidence.Request.Operation, extension);
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

    private static async Task WriteImmutableJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, BenchmarkJson.Options, cancellationToken);
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
        builder.AppendLine("| Provider | Form | Workload | raw operation p50 (ms) | raw operation p95 (ms) | raw operation p99 (ms) | ops/s | B/op | round trips/op | storage delta | net storage/logical payload | plan |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Cases.OrderBy(result => result.Case.Identity, StringComparer.Ordinal))
        {
            var summary = result.Summary;
            builder.AppendLine(
                $"| {result.Case.Provider} | {result.Case.StorageForm} | {result.Case.Workload} | " +
                $"{summary.OperationLatencyP50Nanoseconds / 1_000_000:F3} | {summary.OperationLatencyP95Nanoseconds / 1_000_000:F3} | " +
                $"{summary.OperationLatencyP99Nanoseconds / 1_000_000:F3} | {summary.ThroughputOperationsPerSecond:F1} | " +
                $"{summary.AllocatedBytesPerOperation:F1} | {Format(summary.RoundTripsPerOperation)} | " +
                $"{Format(summary.StorageGrowthBytes)} | {Format(summary.NetStorageGrowthBytesPerLogicalPayloadByte)} | " +
                $"{FormatPlans(result.PlanArtifacts)} |");
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
    private static string FormatPlans(IReadOnlyList<string> plans) => plans.Count == 0
        ? string.Empty
        : string.Join("<br>", plans.Select(plan => $"[{Path.GetFileName(plan)}](../{plan})"));
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
        var git = CaptureGit(repositoryRoot);
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
            DateTimeOffset.UtcNow)
        {
            GitTreeDigest = git.TreeDigest,
            CpuModel = CaptureCpuModel(),
            Memory = CaptureMemory(),
            Storage = CaptureStorage(repositoryRoot),
            PowerManagement = CapturePowerManagement()
        };
    }

    public static BenchmarkGitState CaptureGit(string repositoryRoot)
    {
        try
        {
            var commit = RunGit(repositoryRoot, "rev-parse HEAD").Trim();
            var dirty = !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status --porcelain"));
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(Encoding.UTF8.GetBytes(commit));
            digest.AppendData(RunGitBytes(repositoryRoot, "diff --binary --no-ext-diff HEAD --"));
            var untracked = RunGitBytes(repositoryRoot, "ls-files --others --exclude-standard -z");
            digest.AppendData(untracked);
            foreach (var relative in Encoding.UTF8.GetString(untracked)
                         .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                         .Order(StringComparer.Ordinal))
            {
                var path = Path.GetFullPath(Path.Combine(repositoryRoot, relative));
                if (File.Exists(path))
                    digest.AppendData(File.ReadAllBytes(path));
            }
            return new BenchmarkGitState(commit, dirty, Convert.ToHexStringLower(digest.GetHashAndReset()));
        }
        catch
        {
            return new BenchmarkGitState(
                "unknown",
                true,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("unavailable"))));
        }
    }

    private static string CaptureCpuModel() =>
        FirstAvailable(
            () => OperatingSystem.IsMacOS() ? Run("sysctl", "-n machdep.cpu.brand_string").Trim() : null,
            () => OperatingSystem.IsMacOS() ? Run("sysctl", "-n hw.model").Trim() : null,
            () => File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim());

    private static string CaptureMemory() =>
        FirstAvailable(
            () => OperatingSystem.IsMacOS() ? $"{Run("sysctl", "-n hw.memsize").Trim()} bytes" : null,
            () => File.ReadLines("/proc/meminfo")
                .FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                ?.Trim());

    private static string CaptureStorage(string repositoryRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(repositoryRoot))!;
            var drive = new DriveInfo(root);
            var fileSystem = OperatingSystem.IsMacOS()
                ? Run("stat", $"-f %T {Quote(repositoryRoot)}").Trim()
                : Run("stat", $"-f -c %T {Quote(repositoryRoot)}").Trim();
            return $"filesystem={fileSystem};totalBytes={drive.TotalSize};availableBytes={drive.AvailableFreeSpace}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string CapturePowerManagement() =>
        FirstAvailable(
            () => File.Exists("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor")
                ? $"governor={File.ReadAllText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor").Trim()}"
                : null,
            () => OperatingSystem.IsMacOS()
                ? $"lowPowerMode={Run("pmset", "-g custom")
                    .Split('\n')
                    .FirstOrDefault(line => line.Contains("lowpowermode", StringComparison.OrdinalIgnoreCase))
                    ?.Trim() ?? "unavailable"}"
                : null);

    private static string FirstAvailable(params Func<string?>[] readers)
    {
        foreach (var reader in readers)
        {
            try
            {
                var value = reader();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
            }
        }
        return "unavailable";
    }

    private static string RunGit(string repositoryRoot, string arguments)
        => Encoding.UTF8.GetString(RunGitBytes(repositoryRoot, arguments));

    private static byte[] RunGitBytes(string repositoryRoot, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Unable to start git.");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        return output.ToArray();
    }

    private static string Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException($"Unable to start '{fileName}'.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        return output;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
