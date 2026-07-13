using Groundwork.Core.PhysicalStorage;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkArtifactWriterTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"groundwork-artifact-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Raw_measurements_are_one_json_object_per_line_and_round_trip_as_a_baseline()
    {
        var layout = new ArtifactLayout(root);
        var record = new RawBenchmarkRecord(
            new BenchmarkCase(BenchmarkProvider.Sqlite, PhysicalStorageForm.SharedDocuments, BenchmarkWorkload.ReusedClientPointReadBatch),
            new BenchmarkSample(0, 1, 100, 50, null, 0, 0, null, null, new Dictionary<string, long>()));
        await using (var writer = new BenchmarkArtifactWriter(layout))
            await writer.AppendSampleAsync(record, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(layout.RawMeasurements);
        var reloaded = await BenchmarkArtifactWriter.ReadRawAsync(root, CancellationToken.None);

        Assert.Single(lines);
        var actual = Assert.Single(reloaded);
        Assert.Equal(record.Case, actual.Case);
        Assert.Equal(record.Sample.Iteration, actual.Sample.Iteration);
        Assert.Equal(record.Sample.ElapsedNanoseconds, actual.Sample.ElapsedNanoseconds);
        Assert.Empty(actual.Sample.ProviderWork);
    }

    [Fact]
    public async Task Report_includes_insufficient_Elsa_migration_evidence_without_a_decision_claim()
    {
        var layout = new ArtifactLayout(root);
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Smoke,
            [],
            [],
            new BaselineEligibility(false, ["Smoke runs are not promotable."]));

        await using (var writer = new BenchmarkArtifactWriter(layout))
            await writer.WriteReportAsync(report, CancellationToken.None);

        Assert.True(File.Exists(layout.ElsaMigrationEvidenceJson));
        var evidence = System.Text.Json.JsonSerializer.Deserialize<ElsaMigrationEvidenceReport>(
            await File.ReadAllTextAsync(layout.ElsaMigrationEvidenceJson),
            BenchmarkJson.Options);
        Assert.NotNull(evidence);
        Assert.Equal(BenchmarkProfiles.SchemaVersion, evidence.SchemaVersion);
        Assert.Equal(BenchmarkEvidenceReadiness.Insufficient, evidence.Readiness);
        Assert.True(evidence.ElsaEfOracleRequired);
        Assert.False(evidence.BaselineEligibility.Eligible);
        Assert.NotEmpty(evidence.RemainingAcceptanceWork);
    }

    [Fact]
    public async Task Evidence_uses_honest_net_growth_names_and_empty_plan_arrays_for_mutations()
    {
        var layout = new ArtifactLayout(root);
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.Insert);
        var before = new StorageSnapshot(100, 10, 1, 0, new Dictionary<string, long>());
        var after = new StorageSnapshot(500, 20, 5, 0, new Dictionary<string, long>());
        var sample = new BenchmarkSample(
            0, 4, 1_000, 40, 4, 200, 4, before, after, new Dictionary<string, long>());
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Smoke,
            [new BenchmarkCaseResult(
                benchmarkCase,
                new CorrectnessGateResult(true, true, true, true, true),
                [],
                BenchmarkSummarizer.Summarize(benchmarkCase.Identity, [sample]),
                [sample])],
            [],
            new BaselineEligibility(false, ["Smoke runs are not promotable."]));

        await using (var writer = new BenchmarkArtifactWriter(layout))
            await writer.WriteReportAsync(report, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(layout.ElsaMigrationEvidenceJson));
        var evidenceCase = document.RootElement.GetProperty("cases")[0];
        Assert.Equal(2, evidenceCase.GetProperty("netStorageGrowthBytesPerLogicalPayloadByte").GetDouble());
        Assert.Equal(1, evidenceCase.GetProperty("netPhysicalRowGrowthPerLogicalMutation").GetDouble());
        Assert.Empty(evidenceCase.GetProperty("planArtifacts").EnumerateArray());
        Assert.False(evidenceCase.TryGetProperty("writeAmplificationBytesPerLogicalByte", out _));
    }

    [Fact]
    public async Task Complete_run_directory_round_trips_with_baseline_provenance()
    {
        var layout = new ArtifactLayout(root);
        var machine = new BenchmarkMachineMetadata(
            "test-os", "benchmark-host", "Arm64", ".NET 10", "Release", 8, true, 1_000_000_000,
            "1.0.0", "abcdef", false, DateTimeOffset.UnixEpoch);
        var providers = new BenchmarkProviderMetadata[]
        {
            new(BenchmarkProvider.Sqlite, "3.50.4", new Dictionary<string, string>())
        };
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Smoke,
            [],
            [],
            new BaselineEligibility(false, ["Smoke run."]));
        var manifest = new BenchmarkRunManifest(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            "completed",
            BenchmarkRunMode.Smoke,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "abcdef",
            false,
            layout.RelativePath(layout.RawMeasurements),
            layout.RelativePath(layout.SummaryJson),
            layout.RelativePath(layout.ElsaMigrationEvidenceJson),
            layout.RelativePath(layout.MachineMetadata),
            layout.RelativePath(layout.ProviderMetadata),
            layout.RelativePath(layout.Configuration),
            [],
            null,
            false,
            null);

        await using (var writer = new BenchmarkArtifactWriter(layout))
        {
            await writer.WriteManifestAsync(manifest, CancellationToken.None);
            await writer.WriteMachineAsync(machine, CancellationToken.None);
            await writer.WriteProvidersAsync(providers, CancellationToken.None);
            await writer.WriteConfigurationAsync(BenchmarkProfiles.Smoke, CancellationToken.None);
            await writer.WriteReportAsync(report, CancellationToken.None);
        }

        var baseline = await BenchmarkArtifactWriter.ReadBaselineAsync(root, CancellationToken.None);

        Assert.True(baseline.HasProvenance);
        Assert.Equal("test-run", baseline.Manifest!.RunId);
        Assert.Equal(BenchmarkProfiles.ReproducibleSeed, baseline.Configuration!.Seed);
        Assert.Equal(BenchmarkProvider.Sqlite, Assert.Single(baseline.Providers!).Provider);
    }

    [Fact]
    public async Task Baseline_reader_rejects_fields_outside_the_versioned_schema()
    {
        var layout = new ArtifactLayout(root);
        layout.CreateDirectories();
        var record = new RawBenchmarkRecord(
            new BenchmarkCase(BenchmarkProvider.Sqlite, PhysicalStorageForm.SharedDocuments, BenchmarkWorkload.ReusedClientPointReadBatch),
            new BenchmarkSample(0, 1, 100, 50, null, 0, 0, null, null, new Dictionary<string, long>()));
        var json = JsonNode.Parse(JsonSerializer.Serialize(record, BenchmarkJson.CompactOptions))!.AsObject();
        json["unexpected"] = true;
        await File.WriteAllTextAsync(layout.RawMeasurements, json.ToJsonString());

        await Assert.ThrowsAsync<JsonException>(() => BenchmarkArtifactWriter.ReadRawAsync(root, CancellationToken.None));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
