using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkWorkerResponse(
    string ProtocolVersion,
    string RunGroupId,
    int Ordinal,
    BenchmarkExecutionRole Role,
    bool Succeeded,
    string? RunDirectory,
    string? ConsumerEvidence,
    string? FailureType)
{
    public string RequestDigest { get; init; } = string.Empty;
    public string GitCommit { get; init; } = string.Empty;
    public string GitTreeDigest { get; init; } = string.Empty;
    public BenchmarkWorkerArtifactDigests? Artifacts { get; init; }
}

public sealed record BenchmarkWorkerArtifactDigests(
    string Manifest,
    string ManifestDigest,
    string ElsaMigrationEvidence,
    string ElsaMigrationEvidenceDigest,
    string? ConsumerEvidence,
    string? ConsumerEvidenceDigest);

public sealed record BenchmarkRunGroupEntry(
    int Ordinal,
    int IndependentRun,
    BenchmarkExecutionRole Role,
    string Request,
    string Response,
    string? ConsumerEvidence,
    string? ConsumerEvidenceDigest)
{
    public string RequestDigest { get; init; } = string.Empty;
    public string ResponseDigest { get; init; } = string.Empty;
    public string WorkerManifest { get; init; } = string.Empty;
    public string WorkerManifestDigest { get; init; } = string.Empty;
    public string ElsaMigrationEvidence { get; init; } = string.Empty;
    public string ElsaMigrationEvidenceDigest { get; init; } = string.Empty;
}

public sealed record BenchmarkRunGroupManifest(
    string ProtocolVersion,
    string RunGroupId,
    bool Promotable,
    bool ExternalOracleJoinRequired,
    string GitCommit,
    bool GitDirty,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<BenchmarkRunGroupEntry> Runs)
{
    public string GitTreeDigest { get; init; } = string.Empty;
    public string? BaselineRunGroup { get; init; }
    public string? RegressionReport { get; init; }
    public string? RegressionReportDigest { get; init; }
    public bool ConfirmedRegression { get; init; }
}

public sealed record BenchmarkRunGroupResult(
    string RunDirectory,
    int WorkerCount,
    bool ConfirmedRegression);

public sealed class BenchmarkSubprocessCoordinator
{
    private readonly Action<string> progress;
    private readonly Func<string, string, CancellationToken, Task<int>> workerLauncher;

    public BenchmarkSubprocessCoordinator(Action<string>? progress = null)
    {
        this.progress = progress ?? (_ => { });
        workerLauncher = RunWorkerProcessAsync;
    }

    internal BenchmarkSubprocessCoordinator(
        Action<string>? progress,
        Func<string, string, CancellationToken, Task<int>> workerLauncher)
    {
        this.progress = progress ?? (_ => { });
        this.workerLauncher = workerLauncher ?? throw new ArgumentNullException(nameof(workerLauncher));
    }

    public async Task<BenchmarkRunGroupResult> RunAsync(
        BenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var machine = BenchmarkMetadata.Capture(request.RepositoryRoot);
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var runGroupId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Short(machine.GitCommit)}-" +
                         $"{request.Configuration.Mode.ToString().ToLowerInvariant()}-{nonce}";
        var root = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            request.RepositoryRoot,
            "artifacts",
            "physical-storage",
            "groups",
            "v1",
            runGroupId));
        RequireEmpty(root);
        var protocolRoot = Path.Combine(root, "protocol");
        var requestsRoot = Path.Combine(protocolRoot, "requests");
        var responsesRoot = Path.Combine(protocolRoot, "responses");
        var runsRoot = Path.Combine(root, "runs");
        Directory.CreateDirectory(requestsRoot);
        Directory.CreateDirectory(responsesRoot);
        Directory.CreateDirectory(runsRoot);

        var expectedGit = new BenchmarkGitState(machine.GitCommit, machine.GitDirty, machine.GitTreeDigest);
        var invocations = BenchmarkRunProtocol.CreateInvocations(request, runGroupId, expectedGit);
        var entries = new List<BenchmarkRunGroupEntry>(invocations.Count);
        foreach (var original in invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = original.Ordinal.ToString("D6");
            var workerRun = Path.Combine(runsRoot, ordinal);
            var invocation = original with
            {
                Request = original.Request with
                {
                    OutputDirectory = workerRun,
                    IndependentRun = original.IndependentRun
                }
            };
            var requestPath = Path.Combine(requestsRoot, $"{ordinal}.json");
            var responsePath = Path.Combine(responsesRoot, $"{ordinal}.json");
            await WriteImmutableAsync(requestPath, invocation, cancellationToken);
            var requestDigest = DigestFile(requestPath);
            progress($"[{original.Ordinal}/{invocations.Count}] worker {original.Request.DataShape!.Identity} " +
                     $"{original.Request.Configuration.Providers[0]}/" +
                     $"{original.Request.Configuration.StorageForms[0]}/{original.Request.Workloads[0]} " +
                     $"({original.Role})");
            var exitCode = await workerLauncher(requestPath, responsePath, cancellationToken);
            if (!File.Exists(responsePath))
                throw new InvalidOperationException($"Benchmark worker {original.Ordinal} produced no response artifact.");
            var response = await ReadAsync<BenchmarkWorkerResponse>(responsePath, cancellationToken);
            ValidateResponse(invocation, response, requestDigest);
            EnsureWorkerSucceeded(original.Ordinal, exitCode, response);
            var responseRunDirectory = Path.GetFullPath(response.RunDirectory!);
            if (!responseRunDirectory.Equals(Path.GetFullPath(workerRun), StringComparison.Ordinal) ||
                !Directory.Exists(responseRunDirectory))
            {
                throw new InvalidOperationException(
                    $"Benchmark worker {original.Ordinal} returned an invalid run directory.");
            }
            string? relativeEvidence = null;
            string? evidenceDigest = null;
            if (original.Role == BenchmarkExecutionRole.Measured)
            {
                if (response.ConsumerEvidence is null)
                    throw new InvalidOperationException($"Measured worker {original.Ordinal} returned no consumer evidence.");
                var consumerEvidence = Path.GetFullPath(response.ConsumerEvidence);
                if (!consumerEvidence.StartsWith(Path.GetFullPath(workerRun) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !File.Exists(consumerEvidence))
                    throw new InvalidOperationException($"Benchmark worker {original.Ordinal} returned an invalid evidence path.");
                var digestedEvidence = ResolveWorkerArtifact(
                    workerRun,
                    response.Artifacts!.ConsumerEvidence ??
                    throw new InvalidOperationException(
                        $"Measured worker {original.Ordinal} returned no consumer evidence digest path."));
                if (!consumerEvidence.Equals(digestedEvidence, StringComparison.Ordinal) ||
                    response.Artifacts.ConsumerEvidenceDigest != DigestFile(consumerEvidence))
                {
                    throw new InvalidOperationException(
                        $"Measured worker {original.Ordinal} consumer evidence digest binding is invalid.");
                }
                relativeEvidence = Relative(root, consumerEvidence);
                evidenceDigest = DigestFile(consumerEvidence);
            }
            else if (response.ConsumerEvidence is not null)
            {
                throw new InvalidOperationException($"Untimed warm-up worker {original.Ordinal} returned measured evidence.");
            }
            entries.Add(new BenchmarkRunGroupEntry(
                original.Ordinal,
                original.IndependentRun,
                original.Role,
                Relative(root, requestPath),
                Relative(root, responsePath),
                relativeEvidence,
                evidenceDigest)
            {
                RequestDigest = requestDigest,
                ResponseDigest = DigestFile(responsePath),
                WorkerManifest = Relative(root, ResolveWorkerArtifact(workerRun, response.Artifacts!.Manifest)),
                WorkerManifestDigest = response.Artifacts.ManifestDigest,
                ElsaMigrationEvidence = Relative(
                    root,
                    ResolveWorkerArtifact(workerRun, response.Artifacts.ElsaMigrationEvidence)),
                ElsaMigrationEvidenceDigest = response.Artifacts.ElsaMigrationEvidenceDigest
            });
        }

        var manifest = new BenchmarkRunGroupManifest(
            BenchmarkRunProtocol.ProtocolVersion,
            runGroupId,
            Promotable: false,
            ExternalOracleJoinRequired: true,
            machine.GitCommit,
            machine.GitDirty,
            DateTimeOffset.UtcNow,
            entries)
        {
            GitTreeDigest = machine.GitTreeDigest
        };
        BenchmarkRunGroupRegressionReport? regression = null;
        if (request.BaselineRun is not null)
        {
            regression = await BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                root,
                manifest,
                request.BaselineRun,
                request.Configuration.Mode == BenchmarkRunMode.Scheduled
                    ? RegressionPolicy.Scheduled
                    : RegressionPolicy.Smoke,
                cancellationToken);
            var regressionPath = Path.Combine(root, "reports", "regression.json");
            await WriteImmutableAsync(regressionPath, regression, cancellationToken);
            manifest = manifest with
            {
                BaselineRunGroup = Path.GetFullPath(request.BaselineRun),
                RegressionReport = Relative(root, regressionPath),
                RegressionReportDigest = DigestFile(regressionPath),
                ConfirmedRegression = request.RegressionConfirmationRun && regression.Regressed
            };
        }
        await WriteImmutableAsync(
            Path.Combine(root, "run-group.json"),
            manifest,
            cancellationToken);
        await BenchmarkRunGroupVerifier.VerifyAsync(root, cancellationToken);
        return new BenchmarkRunGroupResult(root, entries.Count, manifest.ConfirmedRegression);
    }

    internal static void ValidateResponse(
        BenchmarkWorkerInvocation invocation,
        BenchmarkWorkerResponse response,
        string? expectedRequestDigest = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(response.ProtocolVersion, BenchmarkRunProtocol.ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(response.ProtocolVersion, invocation.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Worker response protocol '{response.ProtocolVersion}' does not match the invocation protocol.");
        }
        if (!string.Equals(response.RunGroupId, invocation.RunGroupId, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker response group does not match the invocation group.");
        if (response.Ordinal != invocation.Ordinal)
            throw new InvalidOperationException("Worker response ordinal does not match the invocation ordinal.");
        if (response.Role != invocation.Role)
            throw new InvalidOperationException("Worker response role does not match the invocation role.");
        if (expectedRequestDigest is not null &&
            !string.Equals(response.RequestDigest, expectedRequestDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker response request digest does not match the invocation artifact.");
        if (!string.Equals(response.GitCommit, invocation.ExpectedGitCommit, StringComparison.Ordinal) ||
            !string.Equals(response.GitTreeDigest, invocation.ExpectedGitTreeDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker response Git provenance does not match the invocation.");

        if (response.Succeeded)
        {
            if (string.IsNullOrWhiteSpace(response.RunDirectory) ||
                response.FailureType is not null ||
                response.Artifacts is null)
                throw new InvalidOperationException("Successful worker response semantics are inconsistent.");
            if (invocation.Role == BenchmarkExecutionRole.Measured &&
                string.IsNullOrWhiteSpace(response.ConsumerEvidence))
            {
                throw new InvalidOperationException("Measured worker response must identify consumer evidence.");
            }
            if (invocation.Role == BenchmarkExecutionRole.UntimedWarmup &&
                response.ConsumerEvidence is not null)
            {
                throw new InvalidOperationException("Untimed warm-up response cannot identify consumer evidence.");
            }
            if (invocation.Role == BenchmarkExecutionRole.Measured &&
                (response.Artifacts.ConsumerEvidence is null ||
                 response.Artifacts.ConsumerEvidenceDigest is null))
                throw new InvalidOperationException("Measured worker response must digest consumer evidence.");
            if (invocation.Role == BenchmarkExecutionRole.UntimedWarmup &&
                (response.Artifacts.ConsumerEvidence is not null ||
                 response.Artifacts.ConsumerEvidenceDigest is not null))
                throw new InvalidOperationException("Untimed warm-up response cannot digest consumer evidence.");
            return;
        }

        if (response.RunDirectory is not null ||
            response.ConsumerEvidence is not null ||
            response.Artifacts is not null ||
            string.IsNullOrWhiteSpace(response.FailureType))
        {
            throw new InvalidOperationException("Failed worker response semantics are inconsistent.");
        }
    }

    internal static void EnsureWorkerSucceeded(
        int ordinal,
        int exitCode,
        BenchmarkWorkerResponse response)
    {
        if (exitCode != 0 || !response.Succeeded)
        {
            throw new InvalidOperationException(
                $"Benchmark worker {ordinal} failed ({response.FailureType ?? $"exit code {exitCode}"}).");
        }
    }

    internal static async Task<int> RunWorkerAsync(
        BenchmarkWorkerInvocation invocation,
        string responsePath,
        CancellationToken cancellationToken,
        string requestDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
    {
        try
        {
            BenchmarkRunProtocol.ValidateInvocation(invocation);
            var git = BenchmarkMetadata.CaptureGit(invocation.Request.RepositoryRoot);
            if (!string.Equals(git.Commit, invocation.ExpectedGitCommit, StringComparison.Ordinal) ||
                !string.Equals(git.TreeDigest, invocation.ExpectedGitTreeDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Worker repository Git commit/tree state does not match the coordinator invocation.");
            }
            var result = await new BenchmarkRunner(Console.WriteLine).RunAsync(invocation.Request, cancellationToken);
            var manifestPath = Path.Combine(result.RunDirectory, "manifest.json");
            var elsaPath = Path.Combine(result.RunDirectory, "reports", "elsa-migration-evidence.json");
            var consumerPath = invocation.Role == BenchmarkExecutionRole.Measured
                ? Path.Combine(result.RunDirectory, "reports", "consumer-evidence.json")
                : null;
            await WriteImmutableAsync(
                responsePath,
                new BenchmarkWorkerResponse(
                    BenchmarkRunProtocol.ProtocolVersion,
                    invocation.RunGroupId,
                    invocation.Ordinal,
                    invocation.Role,
                    Succeeded: true,
                    result.RunDirectory,
                    invocation.Role == BenchmarkExecutionRole.Measured
                        ? Path.Combine(result.RunDirectory, "reports", "consumer-evidence.json")
                        : null,
                    FailureType: null)
                {
                    RequestDigest = requestDigest,
                    GitCommit = git.Commit,
                    GitTreeDigest = git.TreeDigest,
                    Artifacts = new BenchmarkWorkerArtifactDigests(
                        "manifest.json",
                        DigestFile(manifestPath),
                        "reports/elsa-migration-evidence.json",
                        DigestFile(elsaPath),
                        consumerPath is null ? null : "reports/consumer-evidence.json",
                        consumerPath is null ? null : DigestFile(consumerPath))
                },
                cancellationToken);
            return result.ConfirmedRegression ? 2 : 0;
        }
        catch (Exception exception)
        {
            await WriteImmutableAsync(
                responsePath,
                new BenchmarkWorkerResponse(
                    BenchmarkRunProtocol.ProtocolVersion,
                    invocation.RunGroupId,
                    invocation.Ordinal,
                    invocation.Role,
                    Succeeded: false,
                    RunDirectory: null,
                    ConsumerEvidence: null,
                    exception.GetType().FullName ?? exception.GetType().Name)
                {
                    RequestDigest = requestDigest,
                    GitCommit = invocation.ExpectedGitCommit,
                    GitTreeDigest = invocation.ExpectedGitTreeDigest
                },
                CancellationToken.None);
            Console.Error.WriteLine(exception);
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    internal static Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken) =>
        ReadCoreAsync<T>(path, cancellationToken);

    private async Task<int> RunWorkerProcessAsync(
        string requestPath,
        string responsePath,
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--response");
        startInfo.ArgumentList.Add(responsePath);
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Unable to start benchmark worker process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForExitOrTerminateAsync(process, cancellationToken);
        }
        catch
        {
            await Task.WhenAll(output, error);
            throw;
        }
        var standardOutput = await output;
        var standardError = await error;
        if (!string.IsNullOrWhiteSpace(standardOutput))
            progressSafe(standardOutput);
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(standardError))
            Console.Error.WriteLine(standardError);
        return process.ExitCode;

        void progressSafe(string value) => progress(value.TrimEnd());
    }

    internal static async Task WaitForExitOrTerminateAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<T> ReadCoreAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, BenchmarkJson.Options, cancellationToken)
               ?? throw new InvalidOperationException($"Protocol artifact '{path}' is null.");
    }

    private static async Task WriteImmutableAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, BenchmarkJson.Options, cancellationToken);
    }

    internal static string DigestFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private static string ResolveWorkerArtifact(string workerRoot, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException("Worker artifact path must be relative.");
        var root = Path.GetFullPath(workerRoot);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            throw new InvalidOperationException($"Worker artifact '{relative}' is missing or escapes its run root.");
        return path;
    }

    private static void RequireEmpty(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException($"Benchmark output directory '{root}' is not empty.");
    }
}
