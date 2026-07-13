using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkRunnerIsolationTests : IAsyncDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), $"groundwork-runner-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Native_plans_use_a_separately_initialized_identically_seeded_target()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.IndexedQuery],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        Assert.Equal(2, environment.Targets.Count);
        var measured = Assert.Single(environment.Targets, target => target.Instance.Contains("measure", StringComparison.Ordinal));
        var plans = Assert.Single(environment.Targets, target => target.Instance.Contains("plan", StringComparison.Ordinal));
        Assert.NotEqual(measured.Instance, plans.Instance);
        Assert.Equal((configuration.Seed, configuration.DatasetSize), measured.Seed);
        Assert.Equal(measured.Seed, plans.Seed);
        Assert.Equal(1, measured.CorrectnessCalls);
        Assert.Equal(0, plans.CorrectnessCalls);
        Assert.Equal(0, measured.PlanCalls);
        Assert.Equal(1, plans.PlanCalls);
        Assert.True(measured.ExecuteCalls > 0);
        Assert.Equal(0, plans.ExecuteCalls);
        Assert.True(measured.Disposed);
        Assert.True(plans.Disposed);
    }

    [Fact]
    public async Task Mutation_only_run_creates_no_plan_target_or_plan_artifacts()
    {
        var environment = new RecordingEnvironment();
        var configuration = BenchmarkProfiles.Smoke with
        {
            DatasetSize = 10,
            MigrationDatasetSize = 1,
            WarmupIterations = 1,
            MeasurementIterations = 5,
            OperationsPerIteration = 1,
            Providers = [BenchmarkProvider.Sqlite],
            StorageForms = [PhysicalStorageForm.SharedDocuments]
        };
        var runner = new BenchmarkRunner(null, () => environment);

        var result = await runner.RunAsync(
            new BenchmarkRunRequest(
                FindRepositoryRoot(),
                configuration,
                [BenchmarkWorkload.Insert],
                output,
                null,
                AllowContainers: false,
                RegressionConfirmationRun: false),
            CancellationToken.None);

        var measured = Assert.Single(environment.Targets);
        Assert.Contains("measure", measured.Instance, StringComparison.Ordinal);
        Assert.Equal(0, measured.PlanCalls);
        Assert.Empty(Assert.Single(result.Report.Cases).PlanArtifacts);
        Assert.True(measured.Disposed);
    }

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
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Groundwork repository root.");
    }

    private sealed class RecordingEnvironment : IBenchmarkProviderEnvironment
    {
        public List<RecordingTarget> Targets { get; } = [];

        public Task StartAsync(
            IReadOnlyList<BenchmarkProvider> providers,
            bool allowContainers,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public IPhysicalStorageBenchmarkTarget CreateTarget(
            BenchmarkProvider provider,
            PhysicalStorageForm form,
            string instance,
            string scratchDirectory,
            int migrationDatasetSize)
        {
            var target = new RecordingTarget(provider, form, instance);
            Targets.Add(target);
            return target;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTarget(
        BenchmarkProvider provider,
        PhysicalStorageForm storageForm,
        string instance) : IPhysicalStorageBenchmarkTarget
    {
        public BenchmarkProvider Provider => provider;
        public PhysicalStorageForm StorageForm => storageForm;
        public string ProviderVersion => "test";
        public IReadOnlyDictionary<string, string> ProviderConfiguration { get; } = new Dictionary<string, string>();
        public string Instance => instance;
        public (int Seed, int Count) Seed { get; private set; }
        public int CorrectnessCalls { get; private set; }
        public int PlanCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SeedAsync(int seed, int count, CancellationToken cancellationToken)
        {
            Seed = (seed, count);
            return Task.CompletedTask;
        }

        public Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken)
        {
            CorrectnessCalls++;
            return Task.FromResult(new CorrectnessGateResult(true, true, true, true, true));
        }

        public Task<IReadOnlyList<NativePlanEvidence>> RunNativePlanGatesAsync(
            IReadOnlyList<BenchmarkPlanRequest> requests,
            CancellationToken cancellationToken)
        {
            PlanCalls++;
            return Task.FromResult<IReadOnlyList<NativePlanEvidence>>(requests.Select(request => new NativePlanEvidence(
                request, Provider.ToString(), StorageForm.ToString(), "query", "table", "index", "plan", ["assertion"])).ToArray());
        }

        public Task PrepareWorkloadAsync(BenchmarkWorkload workload, int totalIterations, int operationsPerIteration, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PrepareIterationAsync(BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkloadExecution> ExecuteAsync(
            BenchmarkWorkload workload,
            int iteration,
            int operations,
            int concurrency,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            return Task.FromResult(new WorkloadExecution(operations, 0, 0, 2, new Dictionary<string, long>()));
        }

        public Task ValidateIterationAsync(BenchmarkWorkload workload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StorageSnapshot(0, 0, 0, 0, new Dictionary<string, long>()));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
