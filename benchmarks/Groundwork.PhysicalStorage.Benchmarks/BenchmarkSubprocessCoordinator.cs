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
    string? FailureType);

public sealed record BenchmarkRunGroupEntry(
    int Ordinal,
    int IndependentRun,
    BenchmarkExecutionRole Role,
    string Request,
    string Response,
    string? ConsumerEvidence,
    string? ConsumerEvidenceDigest);

public sealed record BenchmarkRunGroupManifest(
    string ProtocolVersion,
    string RunGroupId,
    bool Promotable,
    bool ExternalOracleJoinRequired,
    string GitCommit,
    bool GitDirty,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<BenchmarkRunGroupEntry> Runs);

public sealed record BenchmarkRunGroupResult(string RunDirectory, int WorkerCount);

public sealed class BenchmarkSubprocessCoordinator
{
    private readonly Action<string> progress;

    public BenchmarkSubprocessCoordinator(Action<string>? progress = null) =>
        this.progress = progress ?? (_ => { });

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

        var invocations = BenchmarkRunProtocol.CreateInvocations(request, runGroupId);
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
            progress($"[{original.Ordinal}/{invocations.Count}] worker {original.Request.DataShape!.Identity} " +
                     $"{original.Request.Configuration.Providers[0]}/" +
                     $"{original.Request.Configuration.StorageForms[0]}/{original.Request.Workloads[0]} " +
                     $"({original.Role})");
            var exitCode = await RunWorkerAsync(requestPath, responsePath, cancellationToken);
            if (!File.Exists(responsePath))
                throw new InvalidOperationException($"Benchmark worker {original.Ordinal} produced no response artifact.");
            var response = await ReadAsync<BenchmarkWorkerResponse>(responsePath, cancellationToken);
            ValidateResponse(invocation, response);
            if (exitCode is not (0 or 2) || !response.Succeeded)
                throw new InvalidOperationException($"Benchmark worker {original.Ordinal} failed ({response.FailureType ?? "unknown"}).");
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
                evidenceDigest));
        }

        await WriteImmutableAsync(
            Path.Combine(root, "run-group.json"),
            new BenchmarkRunGroupManifest(
                BenchmarkRunProtocol.ProtocolVersion,
                runGroupId,
                Promotable: false,
                ExternalOracleJoinRequired: true,
                machine.GitCommit,
                machine.GitDirty,
                DateTimeOffset.UtcNow,
                entries),
            cancellationToken);
        return new BenchmarkRunGroupResult(root, entries.Count);
    }

    internal static void ValidateResponse(
        BenchmarkWorkerInvocation invocation,
        BenchmarkWorkerResponse response)
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

        if (response.Succeeded)
        {
            if (string.IsNullOrWhiteSpace(response.RunDirectory) || response.FailureType is not null)
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
            return;
        }

        if (response.RunDirectory is not null ||
            response.ConsumerEvidence is not null ||
            string.IsNullOrWhiteSpace(response.FailureType))
        {
            throw new InvalidOperationException("Failed worker response semantics are inconsistent.");
        }
    }

    internal static async Task<int> RunWorkerAsync(
        BenchmarkWorkerInvocation invocation,
        string responsePath,
        CancellationToken cancellationToken)
    {
        try
        {
            BenchmarkRunProtocol.ValidateInvocation(invocation);
            var result = await new BenchmarkRunner(Console.WriteLine).RunAsync(invocation.Request, cancellationToken);
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
                    FailureType: null),
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
                    exception.GetType().FullName ?? exception.GetType().Name),
                CancellationToken.None);
            Console.Error.WriteLine(exception);
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    internal static Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken) =>
        ReadCoreAsync<T>(path, cancellationToken);

    private async Task<int> RunWorkerAsync(
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

    private static string DigestFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private static void RequireEmpty(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException($"Benchmark output directory '{root}' is not empty.");
    }
}
