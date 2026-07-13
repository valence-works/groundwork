using Xunit;

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
