using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkRunGroupRegressionReport(
    string SchemaVersion,
    string CandidateRunGroupId,
    string BaselineRunGroupId,
    int MinimumIndependentRuns,
    IReadOnlyList<RegressionEvaluation> Evaluations)
{
    public const string ContractVersion = "groundwork.physical-storage.run-group-regression/v1";
    public bool Regressed => Evaluations.Any(evaluation =>
        evaluation.IsComparable && evaluation.Regressed && evaluation.RequiresConfirmation);
}

internal sealed record BenchmarkRunTuple(
    BenchmarkProvider Provider,
    PhysicalStorageForm StorageForm,
    BenchmarkWorkload Workload,
    BenchmarkDataShape DataShape)
{
    public string Identity =>
        $"{Provider}/{StorageForm}/{Workload}/{DataShape.Identity}";

    public static BenchmarkRunTuple From(BenchmarkWorkerInvocation invocation) => new(
        invocation.Request.Configuration.Providers.Single(),
        invocation.Request.Configuration.StorageForms.Single(),
        invocation.Request.Workloads.Single(),
        invocation.Request.DataShape ??
        throw new InvalidOperationException("Worker request has no data shape."));
}

public static class BenchmarkRunGroupVerifier
{
    public static async Task<BenchmarkRunGroupManifest> VerifyAsync(
        string runGroupRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(runGroupRoot);
        var manifest = await ReadAsync<BenchmarkRunGroupManifest>(
            Path.Combine(root, "run-group.json"),
            cancellationToken);
        await VerifyAsync(root, manifest, cancellationToken);
        return manifest;
    }

    internal static async Task VerifyAsync(
        string root,
        BenchmarkRunGroupManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.ProtocolVersion != BenchmarkRunProtocol.ProtocolVersion ||
            string.IsNullOrWhiteSpace(manifest.RunGroupId) ||
            manifest.Promotable ||
            !manifest.ExternalOracleJoinRequired ||
            manifest.Runs.Count == 0)
            throw new InvalidOperationException("Run-group manifest semantics are invalid.");
        if (manifest.Runs.Select(entry => entry.Ordinal).Distinct().Count() != manifest.Runs.Count)
            throw new InvalidOperationException("Run-group worker ordinals must be unique.");

        var semanticWorkers = new HashSet<(BenchmarkRunTuple Tuple, BenchmarkExecutionRole Role, int IndependentRun)>();
        foreach (var entry in manifest.Runs)
        {
            var requestPath = Resolve(root, entry.Request);
            var responsePath = Resolve(root, entry.Response);
            VerifyDigest(requestPath, entry.RequestDigest, "worker request");
            VerifyDigest(responsePath, entry.ResponseDigest, "worker response");
            var invocation = await ReadAsync<BenchmarkWorkerInvocation>(requestPath, cancellationToken);
            var response = await ReadAsync<BenchmarkWorkerResponse>(responsePath, cancellationToken);
            BenchmarkRunProtocol.ValidateInvocation(invocation);
            if (!semanticWorkers.Add((
                    BenchmarkRunTuple.From(invocation),
                    invocation.Role,
                    invocation.IndependentRun)))
            {
                throw new InvalidOperationException(
                    "Run group contains a duplicate semantic worker tuple/run identity.");
            }
            BenchmarkSubprocessCoordinator.ValidateResponse(invocation, response, entry.RequestDigest);
            if (invocation.RunGroupId != manifest.RunGroupId ||
                invocation.Ordinal != entry.Ordinal ||
                invocation.IndependentRun != entry.IndependentRun ||
                invocation.Role != entry.Role)
                throw new InvalidOperationException("Run-group entry does not match its worker protocol artifacts.");
            if (response.GitCommit != manifest.GitCommit)
                throw new InvalidOperationException("Worker and run-group Git commits differ.");
            if (response.GitTreeDigest != manifest.GitTreeDigest)
                throw new InvalidOperationException("Worker and run-group Git tree digests differ.");

            var workerRoot = Path.Combine(root, "runs", entry.Ordinal.ToString("D6"));
            if (!Path.GetFullPath(response.RunDirectory!).Equals(
                    Path.GetFullPath(workerRoot),
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Worker response run directory does not match its run-group slot.");
            var workerManifestPath = Resolve(root, entry.WorkerManifest);
            var responseManifestPath = ResolveWorker(
                root,
                entry.Ordinal,
                response.Artifacts!.Manifest);
            if (workerManifestPath != responseManifestPath)
                throw new InvalidOperationException("Worker manifest paths are inconsistent.");
            VerifyDigest(workerManifestPath, entry.WorkerManifestDigest, "worker manifest");
            var workerManifest = await ReadAsync<BenchmarkRunManifest>(
                workerManifestPath,
                cancellationToken);
            if (workerManifest.Status != "completed" ||
                workerManifest.GitCommit != response.GitCommit ||
                workerManifest.Mode != invocation.Request.Configuration.Mode)
                throw new InvalidOperationException("Worker manifest provenance is inconsistent.");
            var elsaEvidencePath = Resolve(root, entry.ElsaMigrationEvidence);
            if (elsaEvidencePath != ResolveWorker(
                    root,
                    entry.Ordinal,
                    response.Artifacts.ElsaMigrationEvidence))
                throw new InvalidOperationException("Elsa evidence paths are inconsistent.");
            VerifyDigest(
                elsaEvidencePath,
                entry.ElsaMigrationEvidenceDigest,
                "Elsa migration evidence");
            if (entry.Role == BenchmarkExecutionRole.Measured)
            {
                if (entry.ConsumerEvidence is null || entry.ConsumerEvidenceDigest is null)
                    throw new InvalidOperationException("Measured run-group entry has no consumer evidence digest.");
                var consumerEvidencePath = Resolve(root, entry.ConsumerEvidence);
                var responseConsumerPath = Path.GetFullPath(response.ConsumerEvidence!);
                var artifactConsumerPath = ResolveWorker(
                    root,
                    entry.Ordinal,
                    response.Artifacts.ConsumerEvidence!);
                if (consumerEvidencePath != responseConsumerPath ||
                    consumerEvidencePath != artifactConsumerPath)
                    throw new InvalidOperationException("Consumer evidence paths are inconsistent.");
                VerifyDigest(
                    consumerEvidencePath,
                    entry.ConsumerEvidenceDigest,
                    "consumer evidence");
                var consumerEvidence = await ReadAsync<BenchmarkConsumerEvidenceReport>(
                    consumerEvidencePath,
                    cancellationToken);
                if (consumerEvidence.RunId != workerManifest.RunId ||
                    consumerEvidence.GitCommit != response.GitCommit ||
                    consumerEvidence.RawMeasurements != workerManifest.RawMeasurements)
                    throw new InvalidOperationException("Consumer evidence provenance is inconsistent.");
                VerifyDigest(
                    ResolveWorker(root, entry.Ordinal, consumerEvidence.RawMeasurements),
                    consumerEvidence.RawMeasurementsDigest,
                    "raw measurements");
            }
            else if (entry.ConsumerEvidence is not null || entry.ConsumerEvidenceDigest is not null)
            {
                throw new InvalidOperationException("Warm-up run-group entry contains measured evidence.");
            }

            var artifacts = response.Artifacts ??
                            throw new InvalidOperationException("Successful worker response has no artifact digests.");
            VerifyDigest(
                ResolveWorker(root, entry.Ordinal, artifacts.Manifest),
                artifacts.ManifestDigest,
                "response worker manifest");
            VerifyDigest(
                ResolveWorker(root, entry.Ordinal, artifacts.ElsaMigrationEvidence),
                artifacts.ElsaMigrationEvidenceDigest,
                "response Elsa migration evidence");
            if (artifacts.ConsumerEvidence is not null && artifacts.ConsumerEvidenceDigest is not null)
            {
                VerifyDigest(
                    ResolveWorker(root, entry.Ordinal, artifacts.ConsumerEvidence),
                    artifacts.ConsumerEvidenceDigest,
                    "response consumer evidence");
            }
        }

        if (manifest.RegressionReport is not null)
        {
            if (manifest.RegressionReportDigest is null)
                throw new InvalidOperationException("Run-group regression report digest is missing.");
            VerifyDigest(
                Resolve(root, manifest.RegressionReport),
                manifest.RegressionReportDigest,
                "run-group regression report");
        }
        else if (manifest.RegressionReportDigest is not null ||
                 manifest.BaselineRunGroup is not null ||
                 manifest.ConfirmedRegression)
        {
            throw new InvalidOperationException("Run-group regression manifest fields are inconsistent.");
        }
    }

    internal static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required run-group artifact was not found.", path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, BenchmarkJson.Options, cancellationToken)
               ?? throw new InvalidOperationException($"Run-group artifact '{path}' is null.");
    }

    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Run-group artifact paths must be non-empty and relative.");
        var canonicalRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Run-group artifact '{relative}' escapes the group root.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Run-group artifact '{relative}' was not found.", path);
        return path;
    }

    private static string ResolveWorker(string root, int ordinal, string relative)
    {
        var workerRoot = Path.Combine(root, "runs", ordinal.ToString("D6"));
        var path = Resolve(workerRoot, relative);
        if (!path.StartsWith(Path.GetFullPath(workerRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker response artifact escapes its run root.");
        return path;
    }

    private static void VerifyDigest(string path, string expected, string description)
    {
        if (string.IsNullOrWhiteSpace(expected) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidOperationException($"{description} digest verification failed.");
    }
}

public static class BenchmarkRunGroupRegressionEvaluator
{
    public static async Task<BenchmarkRunGroupRegressionReport> CompareAsync(
        string candidateRoot,
        BenchmarkRunGroupManifest candidate,
        string baselineRoot,
        RegressionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        var canonicalCandidateRoot = Path.GetFullPath(candidateRoot);
        var canonicalBaselineRoot = Path.GetFullPath(baselineRoot);
        await BenchmarkRunGroupVerifier.VerifyAsync(canonicalCandidateRoot, candidate, cancellationToken);
        var baseline = await BenchmarkRunGroupVerifier.VerifyAsync(canonicalBaselineRoot, cancellationToken);
        var candidateProcesses = await ReadProcessesAsync(
            canonicalCandidateRoot, candidate, cancellationToken);
        var baselineProcesses = await ReadProcessesAsync(
            canonicalBaselineRoot, baseline, cancellationToken);
        var candidateByTuple = candidateProcesses
            .GroupBy(process => process.Tuple)
            .ToDictionary(group => group.Key, group => group.OrderBy(process => process.IndependentRun).ToArray());
        var baselineByTuple = baselineProcesses
            .GroupBy(process => process.Tuple)
            .ToDictionary(group => group.Key, group => group.OrderBy(process => process.IndependentRun).ToArray());
        RequireEqualTupleSets(candidateByTuple.Keys, baselineByTuple.Keys);

        var evaluations = new List<RegressionEvaluation>();
        foreach (var (tuple, candidateRuns) in candidateByTuple
                     .OrderBy(pair => pair.Key.Identity, StringComparer.Ordinal))
        {
            var baselineTuple = baselineByTuple[tuple];
            var diagnostics = ValidateProcessSet(candidateRuns, baselineTuple, policy);
            if (diagnostics.Count > 0)
            {
                evaluations.Add(new RegressionEvaluation(
                    tuple.Identity,
                    false,
                    policy.RequiresConfirmation,
                    [],
                    diagnostics));
                continue;
            }

            evaluations.Add(RegressionEvaluator.CompareIndependentRuns(
                tuple.Identity,
                baselineTuple.Select(process => process.Samples).ToArray(),
                candidateRuns.Select(process => process.Samples).ToArray(),
                policy));
        }

        return new BenchmarkRunGroupRegressionReport(
            BenchmarkRunGroupRegressionReport.ContractVersion,
            candidate.RunGroupId,
            baseline.RunGroupId,
            policy.MinimumIndependentRuns,
            evaluations);
    }

    private static void RequireEqualTupleSets(
        IEnumerable<BenchmarkRunTuple> candidate,
        IEnumerable<BenchmarkRunTuple> baseline)
    {
        var candidateSet = candidate.ToHashSet();
        var baselineSet = baseline.ToHashSet();
        var missing = baselineSet.Except(candidateSet)
            .Select(tuple => tuple.Identity)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var extra = candidateSet.Except(baselineSet)
            .Select(tuple => tuple.Identity)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0 && extra.Length == 0)
            return;

        var diagnostics = new List<string>();
        if (missing.Length > 0)
            diagnostics.Add($"Candidate run group is missing baseline tuples: {string.Join(", ", missing)}.");
        if (extra.Length > 0)
            diagnostics.Add($"Candidate run group contains extra tuples: {string.Join(", ", extra)}.");
        throw new InvalidOperationException(string.Join(" ", diagnostics));
    }

    private static async Task<BenchmarkProcessEvidence[]> ReadProcessesAsync(
        string root,
        BenchmarkRunGroupManifest manifest,
        CancellationToken cancellationToken)
    {
        var result = new List<BenchmarkProcessEvidence>();
        foreach (var entry in manifest.Runs.Where(run => run.Role == BenchmarkExecutionRole.Measured))
        {
            var invocation = await BenchmarkRunGroupVerifier.ReadAsync<BenchmarkWorkerInvocation>(
                BenchmarkRunGroupVerifier.Resolve(root, entry.Request),
                cancellationToken);
            var tuple = BenchmarkRunTuple.From(invocation);
            var raw = await BenchmarkArtifactWriter.ReadRawAsync(
                Path.Combine(root, "runs", entry.Ordinal.ToString("D6")),
                cancellationToken);
            var workerRoot = Path.Combine(root, "runs", entry.Ordinal.ToString("D6"));
            var configuration = await BenchmarkRunGroupVerifier.ReadAsync<BenchmarkRunConfiguration>(
                Path.Combine(workerRoot, "metadata", "configuration.json"),
                cancellationToken);
            var machine = await BenchmarkRunGroupVerifier.ReadAsync<BenchmarkMachineMetadata>(
                Path.Combine(workerRoot, "metadata", "machine.json"),
                cancellationToken);
            var providers = await BenchmarkRunGroupVerifier.ReadAsync<IReadOnlyList<BenchmarkProviderMetadata>>(
                Path.Combine(workerRoot, "metadata", "providers.json"),
                cancellationToken);
            var matching = raw
                .Where(record => record.Case.Provider == tuple.Provider &&
                                 record.Case.StorageForm == tuple.StorageForm &&
                                 record.Case.Workload == tuple.Workload)
                .Select(record => record.Sample)
                .ToArray();
            if (matching.Length != raw.Count)
                throw new InvalidOperationException("Measured worker raw data contains a case outside its request tuple.");
            if (matching.Length < configuration.MeasurementIterations ||
                matching.Sum(sample => (long)sample.Operations) < configuration.MinimumMeasuredOperations ||
                matching.Sum(sample => sample.ElapsedNanoseconds) <
                configuration.MinimumSteadyStateDurationSeconds * 1_000_000_000L)
            {
                throw new InvalidOperationException(
                    "Measured worker raw data does not satisfy its iteration, operation, and duration floors.");
            }
            result.Add(new BenchmarkProcessEvidence(
                tuple,
                entry.IndependentRun,
                matching,
                ComparabilityDigest(configuration, machine, providers)));
        }
        return result.ToArray();
    }

    private static List<string> ValidateProcessSet(
        IReadOnlyList<BenchmarkProcessEvidence> candidate,
        IReadOnlyList<BenchmarkProcessEvidence> baseline,
        RegressionPolicy policy)
    {
        var diagnostics = new List<string>();
        if (candidate.Count < policy.MinimumIndependentRuns || baseline.Count < policy.MinimumIndependentRuns)
        {
            diagnostics.Add(
                $"At least {policy.MinimumIndependentRuns} independent measured processes are required for each tuple.");
            return diagnostics;
        }
        if (candidate.Select(process => process.IndependentRun).Distinct().Count() != candidate.Count ||
            baseline.Select(process => process.IndependentRun).Distinct().Count() != baseline.Count)
            diagnostics.Add("Independent measured process numbers must be unique within each tuple.");
        if (!candidate.Select(process => process.IndependentRun)
                .SequenceEqual(baseline.Select(process => process.IndependentRun)))
            diagnostics.Add("Candidate and baseline independent-run identities do not match.");
        foreach (var (candidateProcess, baselineProcess) in candidate.Zip(baseline))
        {
            if (candidateProcess.IndependentRun == baselineProcess.IndependentRun &&
                candidateProcess.ComparabilityDigest != baselineProcess.ComparabilityDigest)
            {
                diagnostics.Add(
                    $"Candidate and baseline comparability metadata differ for independent run {candidateProcess.IndependentRun}.");
            }
        }
        return diagnostics;
    }

    private static string ComparabilityDigest(
        BenchmarkRunConfiguration configuration,
        BenchmarkMachineMetadata machine,
        IReadOnlyList<BenchmarkProviderMetadata> providers)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Configuration = configuration,
            Machine = new
            {
                machine.OperatingSystem,
                machine.MachineName,
                machine.Architecture,
                machine.Framework,
                machine.BuildConfiguration,
                machine.ProcessorCount,
                machine.ServerGc,
                machine.StopwatchFrequency,
                machine.HarnessVersion,
                machine.CpuModel,
                machine.Memory,
                machine.Storage,
                machine.PowerManagement
            },
            Providers = providers.OrderBy(provider => provider.Provider).ToArray()
        }, BenchmarkJson.CompactOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record BenchmarkProcessEvidence(
        BenchmarkRunTuple Tuple,
        int IndependentRun,
        IReadOnlyList<BenchmarkSample> Samples,
        string ComparabilityDigest);

}
