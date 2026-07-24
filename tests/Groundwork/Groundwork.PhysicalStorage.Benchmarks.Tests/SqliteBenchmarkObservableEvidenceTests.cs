using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteObservableEvidenceCollection
{
    public const string Name = nameof(SqliteObservableEvidenceCollection);
}

[Collection(SqliteObservableEvidenceCollection.Name)]
public sealed class SqliteBenchmarkObservableEvidenceTests : IAsyncDisposable
{
    private readonly string output =
        Path.Combine(Path.GetTempPath(), $"groundwork-sqlite-observable-evidence-{Guid.NewGuid():N}");

    [Fact]
    public async Task Real_runner_writes_nonempty_observable_result_evidence()
    {
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 25,
            MigrationDatasetSize = 5,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var result = await new BenchmarkRunner().RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.IndexedQuery],
                output,
                BaselineRun: null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        var benchmarkCase = Assert.Single(result.Report.Cases);
        Assert.NotNull(benchmarkCase.ObservableResults);
        Assert.NotEmpty(benchmarkCase.ObservableResults);
        Assert.Contains(
            benchmarkCase.ObservableResults,
            item => item.Identity.StartsWith("query-0000/match-", StringComparison.Ordinal) &&
                    item.Version == 1 &&
                    item.Payload == """{"status":"open","paddingBytes":0}""");

        var evidencePath = Path.Combine(output, "reports", "consumer-evidence.json");
        var evidence = JsonSerializer.Deserialize<BenchmarkConsumerEvidenceReport>(
            await File.ReadAllTextAsync(evidencePath),
            BenchmarkJson.Options);
        var evidenceResult = Assert.Single(Assert.IsType<BenchmarkConsumerEvidenceReport>(evidence).Results);
        Assert.Matches("^[0-9a-f]{64}$", evidenceResult.ResultDigest);
    }

    [Theory]
    [MemberData(nameof(AllWorkloads))]
    public async Task Every_real_SQLite_workload_returns_stable_nonempty_observable_results(
        BenchmarkWorkload workload)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using IPhysicalStorageBenchmarkTarget target =
            new SqliteBenchmarkTarget(
                PhysicalStorageForm.SharedDocuments,
                instance,
                output,
                migrationDatasetSize: 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(25, 0, 5_000),
            CancellationToken.None);
        await target.PrepareWorkloadAsync(
            workload,
            totalIterations: 2,
            operationsPerIteration: 2,
            CancellationToken.None);

        var digests = new List<string>();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await target.PrepareIterationAsync(workload, iteration, CancellationToken.None);
            var execution = await target.ExecuteAsync(
                workload,
                iteration,
                operations: 2,
                concurrency: 2,
                CancellationToken.None);
            await target.ValidateIterationAsync(workload, CancellationToken.None);

            var observable = Assert.IsType<BenchmarkObservableResultVector>(
                execution.ObservableResultVector);
            Assert.NotEmpty(observable.Results);
            Assert.All(
                observable.Results,
                (result, index) => Assert.Equal(index, result.Sequence));
            digests.Add(observable.Digest);
        }

        Assert.Equal(digests[0], digests[1]);
    }

    public static TheoryData<BenchmarkWorkload> AllWorkloads =>
        new(Enum.GetValues<BenchmarkWorkload>());

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }
}
