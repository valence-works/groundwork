using System.Text.Json;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkSchemaTests
{
    private const string JsonSchemaDraft = "https://json-schema.org/draft/2020-12/schema";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string> PublishedSchemas => new()
    {
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/run-manifest.schema.json",
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/raw-measurement.schema.json",
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/elsa-migration-evidence.schema.json",
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/consumer-evidence.schema.json",
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/worker-invocation.schema.json",
        "benchmarks/Groundwork.PhysicalStorage.Benchmarks/baselines/v1/baseline-index.schema.json"
    };

    [Theory]
    [MemberData(nameof(PublishedSchemas))]
    public void Published_schema_has_a_strict_versioned_object_contract(string relativePath)
    {
        using var document = Read(relativePath);
        var root = document.RootElement;

        Assert.Equal(JsonSchemaDraft, root.GetProperty("$schema").GetString());
        Assert.StartsWith("https://github.com/ValenceWorks/Groundwork/benchmarks/physical-storage/v1/", root.GetProperty("$id").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(PublishedSchemas))]
    public void Published_schema_declares_every_required_property(string relativePath)
    {
        using var document = Read(relativePath);
        var root = document.RootElement;
        var properties = root.GetProperty("properties");

        foreach (var required in root.GetProperty("required").EnumerateArray())
            Assert.True(properties.TryGetProperty(required.GetString()!, out _), $"Missing required property '{required}'.");
    }

    [Fact]
    public void Versioned_baseline_index_references_its_schema_and_starts_empty()
    {
        using var document = Read("benchmarks/Groundwork.PhysicalStorage.Benchmarks/baselines/v1/baseline-index.json");
        var root = document.RootElement;

        Assert.Equal("baseline-index.schema.json", root.GetProperty("$schema").GetString());
        Assert.Equal(BenchmarkProfiles.SchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("scaffolding-no-approved-baselines", root.GetProperty("status").GetString());
        Assert.Empty(root.GetProperty("baselines").EnumerateArray());
    }

    [Fact]
    public void Raw_measurement_schema_requires_direct_operation_latency_observations()
    {
        using var document = Read(
            "benchmarks/Groundwork.PhysicalStorage.Benchmarks/schemas/v1/raw-measurement.schema.json");
        var sample = document.RootElement.GetProperty("$defs").GetProperty("sample");

        Assert.Contains(
            sample.GetProperty("required").EnumerateArray(),
            property => property.GetString() == "operationLatencyNanoseconds");
        Assert.True(sample.GetProperty("properties").TryGetProperty("operationLatencyNanoseconds", out var latency));
        Assert.Equal(1, latency.GetProperty("minItems").GetInt32());
        Assert.False(
            sample.GetProperty("properties")
                .TryGetProperty("normalizedBatchLatencyNanosecondsPerOperation", out _));
    }

    private static JsonDocument Read(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, relativePath)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }
}
