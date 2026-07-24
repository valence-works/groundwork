using Xunit;
using Groundwork.Core.PhysicalStorage;

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
    public void Scheduled_aggregate_mechanically_rejects_missing_workers_and_unequal_results()
    {
        var workflow = File.ReadAllText(WorkflowPath);

        Assert.Contains("if actual_workers != expected_workers:", workflow, StringComparison.Ordinal);
        Assert.Contains("if len(values) != 1", workflow, StringComparison.Ordinal);
        Assert.Contains("\"coverageVerified\": True", workflow, StringComparison.Ordinal);
        Assert.Contains("\"promotable\": False", workflow, StringComparison.Ordinal);
        Assert.Contains("physical-storage-scheduled-aggregate-${{ github.run_id }}", workflow, StringComparison.Ordinal);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }
}
