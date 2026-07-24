using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkRunGroupTests : IDisposable
{
    private readonly string scratch = Path.Combine(
        Path.GetTempPath(),
        $"groundwork-run-group-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Coordinator_group_baseline_matches_measured_runs_and_detects_process_median_regression()
    {
        var baselineRoot = Path.Combine(scratch, "baseline");
        var candidateRoot = Path.Combine(scratch, "candidate");
        var baseline = await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var candidate = await WriteGroupAsync(candidateRoot, "candidate", 1_300, writeManifest: false);

        var report = await BenchmarkRunGroupRegressionEvaluator.CompareAsync(
            candidateRoot,
            candidate,
            baselineRoot,
            RegressionPolicy.Scheduled,
            CancellationToken.None);

        Assert.True(report.Regressed);
        var evaluation = Assert.Single(report.Evaluations);
        Assert.True(evaluation.IsComparable);
        Assert.Equal(
            "Sqlite/PhysicalEntityTable/IndexedQuery/n1000-payload0-selectivity5000bp",
            evaluation.CaseIdentity);
        Assert.Equal(3, report.MinimumIndependentRuns);
    }

    [Fact]
    public async Task Coordinator_baseline_option_consumes_a_group_root_and_propagates_confirmation()
    {
        var baselineRoot = Path.Combine(scratch, "coordinator-baseline");
        var candidateRoot = Path.Combine(scratch, "coordinator-candidate");
        await WriteGroupAsync(baselineRoot, "baseline", 1_000);
        var configuration = BenchmarkProfiles.Scheduled with
        {
            DatasetSize = 1_000,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
        };
        var request = new BenchmarkRunRequest(
            FindRepositoryRoot(),
            configuration,
            [BenchmarkWorkload.IndexedQuery],
            candidateRoot,
            baselineRoot,
            AllowContainers: false,
            RegressionConfirmationRun: true,
            new BenchmarkMatrixDimensions([1_000], [0], [5_000], 3));
        var coordinator = new BenchmarkSubprocessCoordinator(
            progress: null,
            async (requestPath, responsePath, cancellationToken) =>
            {
                var invocation = await BenchmarkSubprocessCoordinator.ReadAsync<BenchmarkWorkerInvocation>(
                    requestPath,
                    cancellationToken);
                await MaterializeWorkerAsync(
                    invocation,
                    requestPath,
                    responsePath,
                    latency: 1_300,
                    cancellationToken);
                return 0;
            });

        var result = await coordinator.RunAsync(request, CancellationToken.None);

        Assert.True(result.ConfirmedRegression);
        var manifest = await BenchmarkRunGroupVerifier.VerifyAsync(
            result.RunDirectory,
            CancellationToken.None);
        Assert.True(manifest.ConfirmedRegression);
        Assert.NotNull(manifest.RegressionReport);
    }

    [Fact]
    public async Task Verifier_rejects_tampered_protocol_artifacts()
    {
        var root = Path.Combine(scratch, "tampered");
        var manifest = await WriteGroupAsync(root, "tampered", 1_000);
        var request = Path.Combine(root, manifest.Runs[0].Request.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(request, " ");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verifier_rejects_raw_measurements_that_no_longer_match_consumer_evidence()
    {
        var root = Path.Combine(scratch, "tampered-raw");
        await WriteGroupAsync(root, "tampered-raw", 1_000);
        await File.AppendAllTextAsync(
            Path.Combine(root, "runs", "000001", "raw", "measurements.jsonl"),
            Environment.NewLine);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("raw measurements digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Comparison_rejects_a_candidate_missing_a_baseline_tuple()
    {
        var baselineRoot = Path.Combine(scratch, "missing-baseline");
        var candidateRoot = Path.Combine(scratch, "missing-candidate");
        await WriteGroupAsync(
            baselineRoot,
            "baseline",
            1_000,
            workloads: [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.Insert]);
        var candidate = await WriteGroupAsync(
            candidateRoot,
            "candidate",
            1_000,
            writeManifest: false,
            workloads: [BenchmarkWorkload.IndexedQuery]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("missing baseline tuples", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Insert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_rejects_a_candidate_with_an_extra_tuple()
    {
        var baselineRoot = Path.Combine(scratch, "extra-baseline");
        var candidateRoot = Path.Combine(scratch, "extra-candidate");
        await WriteGroupAsync(
            baselineRoot,
            "baseline",
            1_000,
            workloads: [BenchmarkWorkload.IndexedQuery]);
        var candidate = await WriteGroupAsync(
            candidateRoot,
            "candidate",
            1_000,
            writeManifest: false,
            workloads: [BenchmarkWorkload.IndexedQuery, BenchmarkWorkload.Insert]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupRegressionEvaluator.CompareAsync(
                candidateRoot,
                candidate,
                baselineRoot,
                RegressionPolicy.Scheduled,
                CancellationToken.None));

        Assert.Contains("extra tuples", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Insert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_rejects_duplicate_semantic_worker_tuple_run_identities()
    {
        var root = Path.Combine(scratch, "duplicate-semantic-worker");
        await WriteGroupAsync(
            root,
            "duplicate-semantic-worker",
            1_000,
            independentRunIds: [1, 1, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkRunGroupVerifier.VerifyAsync(root, CancellationToken.None));

        Assert.Contains("duplicate semantic worker", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }

    private static async Task<BenchmarkRunGroupManifest> WriteGroupAsync(
        string root,
        string groupId,
        long latency,
        bool writeManifest = true,
        IReadOnlyList<BenchmarkWorkload>? workloads = null,
        IReadOnlyList<int>? independentRunIds = null)
    {
        const string commit = "test-commit";
        var treeDigest = new string('a', 64);
        var entries = new List<BenchmarkRunGroupEntry>();
        workloads ??= [BenchmarkWorkload.IndexedQuery];
        independentRunIds ??= [1, 2, 3];
        var ordinal = 0;
        foreach (var workload in workloads)
        {
            foreach (var independentRun in independentRunIds)
            {
                var ordinalText = (++ordinal).ToString("D6");
                var runRoot = Path.Combine(root, "runs", ordinalText);
                var requestPath = Path.Combine(root, "protocol", "requests", $"{ordinalText}.json");
                var responsePath = Path.Combine(root, "protocol", "responses", $"{ordinalText}.json");
                var manifestPath = Path.Combine(runRoot, "manifest.json");
                var elsaPath = Path.Combine(runRoot, "reports", "elsa-migration-evidence.json");
                var consumerPath = Path.Combine(runRoot, "reports", "consumer-evidence.json");
                var rawPath = Path.Combine(runRoot, "raw", "measurements.jsonl");
                var configurationPath = Path.Combine(runRoot, "metadata", "configuration.json");
                var machinePath = Path.Combine(runRoot, "metadata", "machine.json");
                var providersPath = Path.Combine(runRoot, "metadata", "providers.json");
                Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(responsePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(elsaPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
                await File.WriteAllTextAsync(elsaPath, "{}");

                var configuration = BenchmarkProfiles.Scheduled with
                {
                    DatasetSize = 1_000,
                    Providers = [BenchmarkProvider.Sqlite],
                    StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
                };
                var shape = new BenchmarkDataShape(1_000, 0, 5_000);
                await WriteJsonAsync(configurationPath, configuration);
                await WriteJsonAsync(machinePath, Machine(commit, treeDigest));
                await WriteJsonAsync(providersPath, new[]
                {
                new BenchmarkProviderMetadata(
                    BenchmarkProvider.Sqlite,
                    "test-provider",
                    new Dictionary<string, string> { ["mode"] = "test" })
            });
                var request = new BenchmarkRunRequest(
                    root,
                    configuration,
                    [workload],
                    runRoot,
                    null,
                    AllowContainers: false,
                    RegressionConfirmationRun: false,
                    DataShape: shape,
                    IndependentRun: independentRun,
                    Role: BenchmarkExecutionRole.Measured);
                var invocation = new BenchmarkWorkerInvocation(
                    BenchmarkRunProtocol.ProtocolVersion,
                    groupId,
                    ordinal,
                    independentRun,
                    BenchmarkExecutionRole.Measured,
                    request)
                {
                    ExpectedGitCommit = commit,
                    ExpectedGitTreeDigest = treeDigest
                };
                await WriteJsonAsync(requestPath, invocation);
                var requestDigest = Digest(requestPath);

                var benchmarkCase = new BenchmarkCase(
                    BenchmarkProvider.Sqlite,
                    PhysicalStorageForm.PhysicalEntityTable,
                    workload);
                var lines = Enumerable.Range(0, 30)
                    .Select(iteration => JsonSerializer.Serialize(
                        new RawBenchmarkRecord(
                            benchmarkCase,
                            new BenchmarkSample(
                                iteration,
                                10,
                                1_000_000_000,
                                1_000,
                                1,
                                0,
                                0,
                                null,
                                null,
                                new Dictionary<string, long>(),
                                Enumerable.Repeat(latency, 10).ToArray())),
                        BenchmarkJson.CompactOptions));
                await File.WriteAllLinesAsync(rawPath, lines);
                await WriteWorkerEnvelopeArtifactsAsync(
                    invocation,
                    manifestPath,
                    rawPath,
                    consumerPath);

                var artifacts = new BenchmarkWorkerArtifactDigests(
                    "manifest.json",
                    Digest(manifestPath),
                    "reports/elsa-migration-evidence.json",
                    Digest(elsaPath),
                    "reports/consumer-evidence.json",
                    Digest(consumerPath));
                var response = new BenchmarkWorkerResponse(
                    BenchmarkRunProtocol.ProtocolVersion,
                    groupId,
                    ordinal,
                    BenchmarkExecutionRole.Measured,
                    Succeeded: true,
                    runRoot,
                    consumerPath,
                    FailureType: null)
                {
                    RequestDigest = requestDigest,
                    GitCommit = commit,
                    GitTreeDigest = treeDigest,
                    Artifacts = artifacts
                };
                await WriteJsonAsync(responsePath, response);
                entries.Add(new BenchmarkRunGroupEntry(
                    ordinal,
                    independentRun,
                    BenchmarkExecutionRole.Measured,
                    Relative(root, requestPath),
                    Relative(root, responsePath),
                    Relative(root, consumerPath),
                    Digest(consumerPath))
                {
                    RequestDigest = requestDigest,
                    ResponseDigest = Digest(responsePath),
                    WorkerManifest = Relative(root, manifestPath),
                    WorkerManifestDigest = Digest(manifestPath),
                    ElsaMigrationEvidence = Relative(root, elsaPath),
                    ElsaMigrationEvidenceDigest = Digest(elsaPath)
                });
            }
        }

        var group = new BenchmarkRunGroupManifest(
            BenchmarkRunProtocol.ProtocolVersion,
            groupId,
            Promotable: false,
            ExternalOracleJoinRequired: true,
            commit,
            GitDirty: false,
            DateTimeOffset.UtcNow,
            entries)
        {
            GitTreeDigest = treeDigest
        };
        if (writeManifest)
            await WriteJsonAsync(Path.Combine(root, "run-group.json"), group);
        return group;
    }

    private static async Task MaterializeWorkerAsync(
        BenchmarkWorkerInvocation invocation,
        string requestPath,
        string responsePath,
        long latency,
        CancellationToken cancellationToken)
    {
        var runRoot = invocation.Request.OutputDirectory!;
        var manifestPath = Path.Combine(runRoot, "manifest.json");
        var elsaPath = Path.Combine(runRoot, "reports", "elsa-migration-evidence.json");
        var consumerPath = Path.Combine(runRoot, "reports", "consumer-evidence.json");
        var rawPath = Path.Combine(runRoot, "raw", "measurements.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(elsaPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        await File.WriteAllTextAsync(elsaPath, "{}", cancellationToken);
        await WriteJsonAsync(
            Path.Combine(runRoot, "metadata", "configuration.json"),
            invocation.Request.Configuration);
        await WriteJsonAsync(
            Path.Combine(runRoot, "metadata", "machine.json"),
            Machine(invocation.ExpectedGitCommit, invocation.ExpectedGitTreeDigest));
        await WriteJsonAsync(
            Path.Combine(runRoot, "metadata", "providers.json"),
            new[]
            {
                new BenchmarkProviderMetadata(
                    BenchmarkProvider.Sqlite,
                    "test-provider",
                    new Dictionary<string, string> { ["mode"] = "test" })
            });

        string? responseConsumer = null;
        string? consumerDigest = null;
        if (invocation.Role == BenchmarkExecutionRole.Measured)
        {
            responseConsumer = consumerPath;
            var benchmarkCase = new BenchmarkCase(
                invocation.Request.Configuration.Providers.Single(),
                invocation.Request.Configuration.StorageForms.Single(),
                invocation.Request.Workloads.Single());
            var lines = Enumerable.Range(0, 30)
                .Select(iteration => JsonSerializer.Serialize(
                    new RawBenchmarkRecord(
                        benchmarkCase,
                        new BenchmarkSample(
                            iteration,
                            10,
                            1_000_000_000,
                            1_000,
                            1,
                            0,
                            0,
                            null,
                            null,
                            new Dictionary<string, long>(),
                            Enumerable.Repeat(latency, 10).ToArray())),
                    BenchmarkJson.CompactOptions));
            await File.WriteAllLinesAsync(rawPath, lines, cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(rawPath, string.Empty, cancellationToken);
        }
        await WriteWorkerEnvelopeArtifactsAsync(
            invocation,
            manifestPath,
            rawPath,
            consumerPath);
        if (responseConsumer is not null)
            consumerDigest = Digest(consumerPath);

        var artifacts = new BenchmarkWorkerArtifactDigests(
            "manifest.json",
            Digest(manifestPath),
            "reports/elsa-migration-evidence.json",
            Digest(elsaPath),
            responseConsumer is null ? null : "reports/consumer-evidence.json",
            consumerDigest);
        await WriteJsonAsync(
            responsePath,
            new BenchmarkWorkerResponse(
                BenchmarkRunProtocol.ProtocolVersion,
                invocation.RunGroupId,
                invocation.Ordinal,
                invocation.Role,
                Succeeded: true,
                runRoot,
                responseConsumer,
                FailureType: null)
            {
                RequestDigest = Digest(requestPath),
                GitCommit = invocation.ExpectedGitCommit,
                GitTreeDigest = invocation.ExpectedGitTreeDigest,
                Artifacts = artifacts
            });
    }

    private static async Task WriteWorkerEnvelopeArtifactsAsync(
        BenchmarkWorkerInvocation invocation,
        string manifestPath,
        string rawPath,
        string consumerPath)
    {
        var runId = $"{invocation.RunGroupId}-{invocation.Ordinal}";
        if (invocation.Role == BenchmarkExecutionRole.Measured)
        {
            await WriteJsonAsync(
                consumerPath,
                new BenchmarkConsumerEvidenceReport(
                    BenchmarkConsumerEvidenceReport.ContractVersion,
                    runId,
                    Promotable: false,
                    ExternalOracleJoinRequired: true,
                    invocation.ExpectedGitCommit,
                    GitDirty: false,
                    "raw/measurements.jsonl",
                    Digest(rawPath),
                    ["test evidence"],
                    []));
        }
        await WriteJsonAsync(
            manifestPath,
            new BenchmarkRunManifest(
                BenchmarkProfiles.SchemaVersion,
                runId,
                "completed",
                invocation.Request.Configuration.Mode,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                invocation.ExpectedGitCommit,
                GitDirty: false,
                "raw/measurements.jsonl",
                "reports/summary.json",
                "reports/elsa-migration-evidence.json",
                "metadata/machine.json",
                "metadata/providers.json",
                "metadata/configuration.json",
                [],
                BaselineRun: null,
                RegressionConfirmationRun: false,
                Failure: null,
                ConsumerEvidence: invocation.Role == BenchmarkExecutionRole.Measured
                    ? "reports/consumer-evidence.json"
                    : null));
    }

    private static BenchmarkMachineMetadata Machine(string commit, string treeDigest) => new(
        "test-os",
        "test-machine",
        "x64",
        ".NET test",
        "Release",
        8,
        false,
        1_000_000,
        "test-harness",
        commit,
        false,
        DateTimeOffset.UnixEpoch)
    {
        GitTreeDigest = treeDigest,
        CpuModel = "test-cpu",
        Memory = "test-memory",
        Storage = "test-storage",
        PowerManagement = "test-power"
    };

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, BenchmarkJson.Options));
    }

    private static string Digest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Groundwork.slnx was not found.");
    }
}
