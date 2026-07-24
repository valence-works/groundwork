using Xunit;
using Groundwork.Core.PhysicalStorage;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkWorkflowContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Pull_request_paths_include_every_central_build_and_benchmark_input()
    {
        var workflow = File.ReadAllLines(Path.Combine(
            RepositoryRoot,
            ".github/workflows/physical-storage-benchmarks.yml"));
        var pathsHeader = Array.FindIndex(workflow, line => line == "    paths:");

        Assert.True(pathsHeader >= 0, "The pull-request paths trigger was not found.");

        var paths = workflow
            .Skip(pathsHeader + 1)
            .TakeWhile(line => line.StartsWith("      - ", StringComparison.Ordinal))
            .Select(line => line[8..].Trim('"'))
            .ToHashSet(StringComparer.Ordinal);

        AssertSuperset(
            paths,
            ".github/workflows/physical-storage-benchmarks.yml",
            "tools/verify_physical_storage_scheduled_coverage.py",
            "benchmarks/Groundwork.PhysicalStorage.Benchmarks/**",
            "src/Groundwork/**",
            "tests/Groundwork/Groundwork.PhysicalStorage.Benchmarks.Tests/**",
            "Build.props",
            "Directory.Build.props",
            "Directory.Packages.props",
            "Groundwork.slnx");
    }

    [Fact]
    public void Scheduled_workflow_shards_the_complete_matrix_within_a_six_hour_execution_budget()
    {
        var workflow = File.ReadAllLines(WorkflowPath);
        var providerCount = InlineMatrixValues(workflow, "        provider:").Count;
        var formCount = InlineMatrixValues(workflow, "        form:").Count;
        var datasetCount = InlineMatrixValues(workflow, "        dataset:").Count;
        var shardCount = providerCount * formCount * datasetCount;
        var workersPerShard =
            Enum.GetValues<BenchmarkWorkload>().Length *
            (1 + BenchmarkProfiles.ScheduledDimensions.IndependentRuns);

        Assert.Equal(Enum.GetValues<BenchmarkProvider>().Length, providerCount);
        Assert.Equal(Enum.GetValues<PhysicalStorageForm>().Length, formCount);
        Assert.Equal(BenchmarkProfiles.ScheduledDimensions.DatasetSizes.Count, datasetCount);
        Assert.Equal(36, shardCount);
        Assert.Equal(56, workersPerShard);
        Assert.Equal(2_016, shardCount * workersPerShard);
        Assert.Contains("      max-parallel: 36", workflow);
        Assert.Equal(360, JobTimeout(workflow, "scheduled-contracts") +
                          JobTimeout(workflow, "scheduled-shard") +
                          JobTimeout(workflow, "verify-and-aggregate"));
    }

    [Fact]
    public void Scheduled_aggregate_uses_the_versioned_verifier_with_production_defaults()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        Assert.Contains("python3 tools/verify_physical_storage_scheduled_coverage.py", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("python3 - <<'PY'", workflow, StringComparison.Ordinal);
        Assert.Contains("physical-storage-scheduled-aggregate-${{ github.run_id }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduled_aggregate_checks_out_the_repository_before_running_the_versioned_verifier()
    {
        var workflow = File.ReadAllLines(WorkflowPath);
        var aggregateJob = Array.FindIndex(workflow, line => line == "  verify-and-aggregate:");
        var checkout = Array.FindIndex(
            workflow,
            aggregateJob,
            line => line.Trim() == "uses: actions/checkout@v4");
        var verifier = Array.FindIndex(
            workflow,
            aggregateJob,
            line => line.Contains("python3 tools/verify_physical_storage_scheduled_coverage.py", StringComparison.Ordinal));

        Assert.True(checkout > aggregateJob, "The aggregate job must check out the verifier script.");
        Assert.True(verifier > checkout, "The aggregate job must check out the repository before running the verifier.");
    }

    [Fact]
    public void Scheduled_aggregate_verifier_accepts_serialized_enum_tokens_for_every_provider()
    {
        const string runId = "fixture-run";
        var root = Path.Combine(Path.GetTempPath(), $"physical-storage-aggregate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (artifactToken, requestToken) in ProviderTokens)
                WriteScheduledFixture(root, runId, artifactToken, requestToken);

            var process = new ProcessStartInfo("python3")
            {
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.ArgumentList.Add(Path.Combine(RepositoryRoot, "tools/verify_physical_storage_scheduled_coverage.py"));
            process.ArgumentList.Add("--root");
            process.ArgumentList.Add(root);
            process.ArgumentList.Add("--run-id");
            process.ArgumentList.Add(runId);
            process.ArgumentList.Add("--test-mode");
            process.ArgumentList.Add("--providers");
            process.ArgumentList.Add("sqlite,sqlserver,postgresql,mongodb");
            process.ArgumentList.Add("--forms");
            process.ArgumentList.Add("shared");
            process.ArgumentList.Add("--datasets");
            process.ArgumentList.Add("1000");
            process.ArgumentList.Add("--workloads");
            process.ArgumentList.Add("clientResetPointReadBatch");
            process.ArgumentList.Add("--independent-runs");
            process.ArgumentList.Add("1");

            using var verifier = Process.Start(process);
            Assert.NotNull(verifier);
            var standardOutput = verifier.StandardOutput.ReadToEnd();
            var standardError = verifier.StandardError.ReadToEnd();
            verifier.WaitForExit();
            Assert.True(verifier.ExitCode == 0, $"Verifier failed: {standardOutput}{standardError}");

            using var verification = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "coverage-verification.json")));
            var result = verification.RootElement;
            Assert.True(result.GetProperty("coverageVerified").GetBoolean());
            Assert.False(result.GetProperty("promotable").GetBoolean());
            Assert.Equal(4, result.GetProperty("requiredShardCount").GetInt32());
            Assert.Equal(8, result.GetProperty("verifiedWorkerCount").GetInt32());
            Assert.Equal(4, result.GetProperty("verifiedMeasuredWorkerCount").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pull_request_smoke_is_a_narrow_non_decision_subset()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        Assert.Contains("SQLite shared-form PR smoke (non-decision)", workflow, StringComparison.Ordinal);
        Assert.Contains("--providers sqlite", workflow, StringComparison.Ordinal);
        Assert.Contains("--forms shared", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "--workloads reused-client-point-read-batch,indexed-query,insert,optimistic-concurrency,pagination-and-count",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--providers all", workflow, StringComparison.Ordinal);
    }

    private static string WorkflowPath => Path.Combine(
        RepositoryRoot,
        ".github/workflows/physical-storage-benchmarks.yml");

    private static IReadOnlyList<string> InlineMatrixValues(IReadOnlyList<string> lines, string prefix)
    {
        var line = Assert.Single(lines.Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)));
        var opening = line.IndexOf('[', StringComparison.Ordinal);
        var closing = line.IndexOf(']', opening + 1);
        Assert.True(opening >= 0 && closing > opening, $"Matrix line '{line}' is not an inline list.");
        return line[(opening + 1)..closing]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static int JobTimeout(IReadOnlyList<string> lines, string job)
    {
        var jobHeader = Array.FindIndex(lines.ToArray(), line => line == $"  {job}:");
        Assert.True(jobHeader >= 0, $"Workflow job '{job}' was not found.");
        var timeout = lines
            .Skip(jobHeader + 1)
            .TakeWhile(line => !line.StartsWith("  ", StringComparison.Ordinal) ||
                               line.StartsWith("    ", StringComparison.Ordinal))
            .Single(line => line.StartsWith("    timeout-minutes:", StringComparison.Ordinal));
        return int.Parse(timeout[(timeout.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertSuperset(IReadOnlySet<string> actual, params string[] expected)
    {
        foreach (var path in expected)
            Assert.Contains(path, actual);
    }

    private static readonly (string ArtifactToken, string RequestToken)[] ProviderTokens =
    [
        ("sqlite", "sqlite"),
        ("sqlserver", "sqlServer"),
        ("postgresql", "postgreSql"),
        ("mongodb", "mongoDb")
    ];

    private static void WriteScheduledFixture(
        string root,
        string runId,
        string artifactToken,
        string requestToken)
    {
        var evidenceRoot = Path.Combine(
            root,
            "shards",
            $"physical-storage-scheduled-{runId}-{artifactToken}-shared-n1000",
            "evidence");
        Directory.CreateDirectory(evidenceRoot);

        var runs = new List<object>();
        WriteWorker(evidenceRoot, artifactToken, requestToken, "untimedWarmup", 0, runs);
        WriteWorker(evidenceRoot, artifactToken, requestToken, "measured", 1, runs);
        WriteJson(Path.Combine(evidenceRoot, "run-group.json"), new { promotable = false, runs });
    }

    private static void WriteWorker(
        string evidenceRoot,
        string artifactToken,
        string requestToken,
        string role,
        int independentRun,
        ICollection<object> runs)
    {
        var suffix = $"{role}-{independentRun}";
        var request = $"request-{suffix}.json";
        var response = $"response-{suffix}.json";
        WriteJson(Path.Combine(evidenceRoot, request), new
        {
            request = new
            {
                configuration = new { providers = new[] { requestToken }, storageForms = new[] { "sharedDocuments" } },
                dataShape = new { datasetSize = 1000, payloadPaddingBytes = 0, querySelectivityBasisPoints = 5000 },
                workloads = new[] { "clientResetPointReadBatch" }
            },
            role,
            independentRun
        });
        WriteJson(Path.Combine(evidenceRoot, response), new { succeeded = true });

        if (role == "measured")
        {
            var evidence = $"consumer-evidence-{suffix}.json";
            WriteJson(Path.Combine(evidenceRoot, evidence), new
            {
                promotable = false,
                gitCommit = "fixture-commit",
                results = new[]
                {
                    new
                    {
                        workloadIdentity = "groundwork.physical-storage/client-reset-point-read-batch",
                        providerIdentity = ProviderIdentity(artifactToken),
                        storageForm = "sharedDocuments",
                        dataShape = new { datasetSize = 1000, payloadPaddingBytes = 0, querySelectivityBasisPoints = 5000 },
                        independentRun,
                        rawSampleCount = 1,
                        rawOperationLatencyCount = 1,
                        resultDigest = new string('a', 64),
                        workloadFingerprint = "fixture-workload-v1"
                    }
                }
            });
            var evidencePath = Path.Combine(evidenceRoot, evidence);
            runs.Add(new
            {
                request,
                response,
                consumerEvidence = evidence,
                consumerEvidenceDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))).ToLowerInvariant()
            });
            return;
        }

        runs.Add(new { request, response });
    }

    private static string ProviderIdentity(string artifactToken) => artifactToken switch
    {
        "sqlite" => "groundwork.sqlite",
        "sqlserver" => "groundwork.sql-server",
        "postgresql" => "groundwork.postgre-sql",
        "mongodb" => "groundwork.mongo-db",
        _ => throw new ArgumentOutOfRangeException(nameof(artifactToken))
    };

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }
}
