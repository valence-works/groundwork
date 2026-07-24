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
            new BenchmarkSample(0, 1, 100, 50, null, 0, 0, null, null, new Dictionary<string, long>(), [100]));
        await using (var writer = new BenchmarkArtifactWriter(layout))
            await writer.AppendSampleAsync(record, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(layout.RawMeasurements);
        var reloaded = await BenchmarkArtifactWriter.ReadRawAsync(root, CancellationToken.None);

        Assert.Single(lines);
        var actual = Assert.Single(reloaded);
        Assert.Equal(record.Case, actual.Case);
        Assert.Equal(record.Sample.Iteration, actual.Sample.Iteration);
        Assert.Equal(record.Sample.ElapsedNanoseconds, actual.Sample.ElapsedNanoseconds);
        Assert.Equal([100L], actual.Sample.OperationLatencyNanoseconds);
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
    public async Task Consumer_evidence_exposes_stable_join_keys_without_provider_configuration_secrets()
    {
        var layout = new ArtifactLayout(root);
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var sample = new BenchmarkSample(
            0, 4, 1_000, 40, 4, 200, 0, null, null, new Dictionary<string, long>(), [100, 200, 300, 400]);
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Scheduled,
            [new BenchmarkCaseResult(
                benchmarkCase,
                new CorrectnessGateResult(true, true, true, true, true),
                [],
                BenchmarkSummarizer.Summarize(benchmarkCase.Identity, [sample]),
                [sample])],
            [],
            new BaselineEligibility(false, ["The Elsa EF oracle has not been joined."]),
            new BenchmarkDataShape(100_000, 1_024, 1_000));
        var machine = new BenchmarkMachineMetadata(
            "test-os", "benchmark-host", "Arm64", ".NET 10", "Release", 8, true, 1_000_000_000,
            "1.0.0", "abcdef", false, DateTimeOffset.UnixEpoch);
        var providers = new[]
        {
            new BenchmarkProviderMetadata(
                BenchmarkProvider.Sqlite,
                "3.50.4",
                new Dictionary<string, string> { ["connection"] = "secret-value" })
        };

        await using (var writer = new BenchmarkArtifactWriter(layout))
        {
            await writer.AppendSampleAsync(new RawBenchmarkRecord(benchmarkCase, sample), CancellationToken.None);
            await writer.WriteConsumerEvidenceAsync(
                BenchmarkConsumerEvidenceReport.Create(report, BenchmarkProfiles.Scheduled, machine, providers, layout),
                CancellationToken.None);
        }

        var json = await File.ReadAllTextAsync(layout.ConsumerEvidenceJson);
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
        var evidence = JsonSerializer.Deserialize<BenchmarkConsumerEvidenceReport>(json, BenchmarkJson.Options);
        Assert.NotNull(evidence);
        Assert.False(evidence.Promotable);
        Assert.True(evidence.ExternalOracleJoinRequired);
        var result = Assert.Single(evidence.Results);
        Assert.Equal("groundwork.physical-storage/indexed-query", result.WorkloadIdentity);
        Assert.Equal("1.1", result.WorkloadVersion);
        Assert.Equal("direct-operation-latency/v1", result.MeasurementProtocol);
        Assert.Equal("groundwork.sqlite", result.ProviderIdentity);
        Assert.Equal(100_000, result.DataShape.DatasetSize);
        Assert.Equal(1_024, result.DataShape.PayloadPaddingBytes);
        Assert.Equal(1_000, result.DataShape.QuerySelectivityBasisPoints);
        Assert.Equal(64, result.WorkloadFingerprint.Length);
        Assert.Equal(64, result.ResultDigest.Length);
        Assert.Equal(64, result.MeasurementDigest.Length);
        Assert.Equal(64, result.ProviderConfigurationDigest.Length);
        Assert.Equal(1, result.RawSampleCount);
        Assert.Equal(4, result.RawOperationLatencyCount);
        Assert.Equal(64, evidence.RawMeasurementsDigest.Length);

        var alternateCase = benchmarkCase with
        {
            Provider = BenchmarkProvider.SqlServer,
            StorageForm = PhysicalStorageForm.SharedDocuments
        };
        var alternateSample = sample with { ElapsedNanoseconds = 2_000 };
        var alternateReport = report with
        {
            Cases =
            [
                new BenchmarkCaseResult(
                    alternateCase,
                    new CorrectnessGateResult(true, true, true, true, true),
                    [],
                    BenchmarkSummarizer.Summarize(alternateCase.Identity, [alternateSample]),
                    [alternateSample])
            ]
        };
        var alternate = Assert.Single(BenchmarkConsumerEvidenceReport.Create(
            alternateReport,
            BenchmarkProfiles.Scheduled,
            machine with { OperatingSystem = "other-os", MachineName = "other-host" },
            [new BenchmarkProviderMetadata(
                BenchmarkProvider.SqlServer,
                "17.0",
                new Dictionary<string, string> { ["source"] = "other-source" })],
            layout).Results);

        Assert.Equal(result.WorkloadFingerprint, alternate.WorkloadFingerprint);
        Assert.Equal(result.ResultDigest, alternate.ResultDigest);
        Assert.NotEqual(result.MeasurementDigest, alternate.MeasurementDigest);
    }

    [Fact]
    public async Task Consumer_evidence_rejects_native_plan_paths_outside_the_run_root()
    {
        var layout = new ArtifactLayout(root);
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var sample = new BenchmarkSample(
            0, 4, 1_000, 40, 4, 200, 0, null, null, new Dictionary<string, long>(), [100, 200, 300, 400]);
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Scheduled,
            [new BenchmarkCaseResult(
                benchmarkCase,
                new CorrectnessGateResult(true, true, true, true, true),
                ["../outside-plan.json"],
                BenchmarkSummarizer.Summarize(benchmarkCase.Identity, [sample]),
                [sample])],
            [],
            new BaselineEligibility(false, ["The Elsa EF oracle has not been joined."]),
            new BenchmarkDataShape(100_000, 0, 1_000));
        var machine = new BenchmarkMachineMetadata(
            "test-os", "benchmark-host", "Arm64", ".NET 10", "Release", 8, true, 1_000_000_000,
            "1.0.0", "abcdef", false, DateTimeOffset.UnixEpoch);
        var providers = new[]
        {
            new BenchmarkProviderMetadata(
                BenchmarkProvider.Sqlite,
                "3.50.4",
                new Dictionary<string, string>())
        };

        await using var writer = new BenchmarkArtifactWriter(layout);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkConsumerEvidenceReport.Create(
                report,
                BenchmarkProfiles.Scheduled,
                machine,
                providers,
                layout));

        Assert.Contains("escapes the run root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consumer_evidence_rejects_missing_native_plan_artifacts()
    {
        var layout = new ArtifactLayout(root);
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var sample = new BenchmarkSample(
            0, 4, 1_000, 40, 4, 200, 0, null, null, new Dictionary<string, long>(), [100, 200, 300, 400]);
        var report = new BenchmarkRunReport(
            BenchmarkProfiles.SchemaVersion,
            "test-run",
            BenchmarkRunMode.Scheduled,
            [new BenchmarkCaseResult(
                benchmarkCase,
                new CorrectnessGateResult(true, true, true, true, true),
                ["plans/sqlite/entity/missing.json"],
                BenchmarkSummarizer.Summarize(benchmarkCase.Identity, [sample]),
                [sample])],
            [],
            new BaselineEligibility(false, ["The Elsa EF oracle has not been joined."]),
            new BenchmarkDataShape(100_000, 0, 1_000));
        var machine = new BenchmarkMachineMetadata(
            "test-os", "benchmark-host", "Arm64", ".NET 10", "Release", 8, true, 1_000_000_000,
            "1.0.0", "abcdef", false, DateTimeOffset.UnixEpoch);
        var providers = new[]
        {
            new BenchmarkProviderMetadata(
                BenchmarkProvider.Sqlite,
                "3.50.4",
                new Dictionary<string, string>())
        };

        await using var writer = new BenchmarkArtifactWriter(layout);
        var exception = Assert.Throws<FileNotFoundException>(() =>
            BenchmarkConsumerEvidenceReport.Create(
                report,
                BenchmarkProfiles.Scheduled,
                machine,
                providers,
                layout));

        Assert.Contains("Native-plan evidence artifact", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_plan_digest_binds_relative_path_as_well_as_file_content()
    {
        var layout = new ArtifactLayout(root);
        var benchmarkCase = new BenchmarkCase(
            BenchmarkProvider.Sqlite,
            PhysicalStorageForm.PhysicalEntityTable,
            BenchmarkWorkload.IndexedQuery);
        var sample = new BenchmarkSample(
            0, 4, 1_000, 40, 4, 200, 0, null, null, new Dictionary<string, long>(), [100, 200, 300, 400]);
        var machine = new BenchmarkMachineMetadata(
            "test-os", "benchmark-host", "Arm64", ".NET 10", "Release", 8, true, 1_000_000_000,
            "1.0.0", "abcdef", false, DateTimeOffset.UnixEpoch);
        var providers = new[]
        {
            new BenchmarkProviderMetadata(
                BenchmarkProvider.Sqlite,
                "3.50.4",
                new Dictionary<string, string>())
        };
        const string firstArtifact = "plans/selection-a/query.json";
        const string secondArtifact = "plans/selection-b/query.json";
        foreach (var artifact in new[] { firstArtifact, secondArtifact })
        {
            foreach (var path in new[] { artifact, $"{artifact}.assertions.json" })
            {
                var absolutePath = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                await File.WriteAllTextAsync(absolutePath, """{"plan":"same-content"}""");
            }
        }

        await using var writer = new BenchmarkArtifactWriter(layout);
        var first = Create(firstArtifact);
        var second = Create(secondArtifact);

        Assert.NotEqual(first.NativePlanDigest, second.NativePlanDigest);
        Assert.Equal([firstArtifact, $"{firstArtifact}.assertions.json"], first.NativePlanArtifacts);
        Assert.Equal([secondArtifact, $"{secondArtifact}.assertions.json"], second.NativePlanArtifacts);
        return;

        BenchmarkConsumerEvidenceResult Create(string artifact)
        {
            var report = new BenchmarkRunReport(
                BenchmarkProfiles.SchemaVersion,
                "test-run",
                BenchmarkRunMode.Scheduled,
                [new BenchmarkCaseResult(
                    benchmarkCase,
                    new CorrectnessGateResult(true, true, true, true, true),
                    [artifact],
                    BenchmarkSummarizer.Summarize(benchmarkCase.Identity, [sample]),
                    [sample])],
                [],
                new BaselineEligibility(false, ["The Elsa EF oracle has not been joined."]),
                new BenchmarkDataShape(100_000, 0, 1_000));
            return Assert.Single(BenchmarkConsumerEvidenceReport.Create(
                report,
                BenchmarkProfiles.Scheduled,
                machine,
                providers,
                layout).Results);
        }
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
            0, 4, 1_000, 40, 4, 200, 4, before, after, new Dictionary<string, long>(), [100, 200, 300, 400]);
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
            new BenchmarkSample(0, 1, 100, 50, null, 0, 0, null, null, new Dictionary<string, long>(), [100]));
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
