using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkRunRequest(
    string RepositoryRoot,
    BenchmarkRunConfiguration Configuration,
    IReadOnlyList<BenchmarkWorkload> Workloads,
    string? OutputDirectory,
    string? BaselineRun,
    bool AllowContainers,
    bool RegressionConfirmationRun,
    BenchmarkMatrixDimensions? Dimensions = null,
    BenchmarkDataShape? DataShape = null,
    int IndependentRun = 1,
    BenchmarkExecutionRole Role = BenchmarkExecutionRole.Measured);

public sealed record BenchmarkRunResult(
    string RunDirectory,
    BenchmarkRunReport Report,
    bool ConfirmedRegression);

public sealed class BenchmarkRunner
{
    private readonly Action<string> progress;
    private readonly Func<IBenchmarkProviderEnvironment> environmentFactory;
    private readonly TimeProvider timeProvider;

    public BenchmarkRunner(Action<string>? progress = null)
        : this(progress, static () => new BenchmarkProviderEnvironment(), TimeProvider.System)
    {
    }

    internal BenchmarkRunner(
        Action<string>? progress,
        Func<IBenchmarkProviderEnvironment> environmentFactory,
        TimeProvider? timeProvider = null)
    {
        this.progress = progress ?? (_ => { });
        this.environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BenchmarkRunResult> RunAsync(
        BenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Configuration.Validate();
        if (request.Workloads.Count == 0)
            throw new ArgumentException("At least one workload must be selected.", nameof(request));
        var dataShape = request.DataShape ??
                        new BenchmarkDataShape(request.Configuration.DatasetSize, 0, 5_000);
        dataShape.Validate();
        var executionConfiguration = request.Configuration with { DataShape = dataShape };
        executionConfiguration.Validate();

        var machine = BenchmarkMetadata.Capture(request.RepositoryRoot);
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Short(machine.GitCommit)}-" +
                    $"{request.Configuration.Mode.ToString().ToLowerInvariant()}-{nonce}";
        var runDirectory = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(
            request.RepositoryRoot,
            "artifacts",
            "physical-storage",
            "v1",
            runId));
        var layout = new ArtifactLayout(runDirectory);
        layout.RequireEmptyOutput();
        await using var writer = new BenchmarkArtifactWriter(layout);
        await using var environment = environmentFactory();
        using var signals = new DatabaseSignalCollector();
        var started = DateTimeOffset.UtcNow;
        var planArtifacts = new List<string>();
        var casePlanArtifacts = new Dictionary<(BenchmarkProvider, PhysicalStorageForm, BenchmarkWorkload), List<string>>();
        var caseResults = new List<BenchmarkCaseResult>();
        var providerMetadata = new List<BenchmarkProviderMetadata>();
        var manifest = new BenchmarkRunManifest(
            BenchmarkProfiles.SchemaVersion,
            runId,
            "running",
            request.Configuration.Mode,
            started,
            null,
            machine.GitCommit,
            machine.GitDirty,
            layout.RelativePath(layout.RawMeasurements),
            layout.RelativePath(layout.SummaryJson),
            layout.RelativePath(layout.ElsaMigrationEvidenceJson),
            layout.RelativePath(layout.MachineMetadata),
            layout.RelativePath(layout.ProviderMetadata),
            layout.RelativePath(layout.Configuration),
            [],
            request.BaselineRun,
            request.RegressionConfirmationRun,
            null,
            request.Role == BenchmarkExecutionRole.Measured
                ? layout.RelativePath(layout.ConsumerEvidenceJson)
                : null);
        await writer.WriteManifestAsync(manifest, cancellationToken);
        await writer.WriteMachineAsync(machine, cancellationToken);
        await writer.WriteConfigurationAsync(executionConfiguration, cancellationToken);

        try
        {
            progress($"Starting providers: {string.Join(", ", request.Configuration.Providers)}");
            await environment.StartAsync(
                request.Configuration.Providers,
                request.AllowContainers,
                cancellationToken);
            var scratch = Path.Combine(runDirectory, "scratch");
            Directory.CreateDirectory(scratch);

            foreach (var provider in request.Configuration.Providers)
            {
                foreach (var form in request.Configuration.StorageForms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var instancePrefix = $"r{nonce}_p{(int)provider}_f{(int)form}";
                    var planRequests = request.Role == BenchmarkExecutionRole.Measured
                        ? BenchmarkPlanRequests.ForWorkloads(request.Workloads)
                        : [];
                    if (planRequests.Count > 0)
                    {
                        var planInstance = $"{instancePrefix}_plan";
                        await using var planTarget = environment.CreateTarget(
                            provider,
                            form,
                            planInstance,
                            scratch,
                            request.Configuration.MigrationDatasetSize);
                        progress($"[{provider}/{form}] materializing isolated native-plan target");
                        await planTarget.InitializeAsync(cancellationToken);
                        await planTarget.SeedAsync(
                            request.Configuration.Seed,
                            dataShape,
                            cancellationToken);
                        var evidence = await planTarget.RunNativePlanGatesAsync(planRequests, cancellationToken);
                        if (evidence.Count != planRequests.Count || !evidence.Select(item => item.Request).SequenceEqual(planRequests))
                            throw new InvalidOperationException($"[{provider}/{form}] native-plan evidence did not match the requested typed shapes.");
                        foreach (var item in evidence)
                        {
                            var planCase = new BenchmarkCase(provider, form, item.Request.Workload);
                            var isolatedPlanArtifact = await writer.WritePlanAsync(planCase, item, cancellationToken);
                            planArtifacts.Add(isolatedPlanArtifact);
                            var key = (provider, form, item.Request.Workload);
                            if (!casePlanArtifacts.TryGetValue(key, out var artifacts))
                                casePlanArtifacts.Add(key, artifacts = []);
                            artifacts.Add(isolatedPlanArtifact);
                        }
                    }

                    var instance = $"{instancePrefix}_measure";
                    await using var target = environment.CreateTarget(
                        provider,
                        form,
                        instance,
                        scratch,
                        request.Configuration.MigrationDatasetSize);
                    progress($"[{provider}/{form}] materializing and seeding {dataShape.DatasetSize:N0} documents");
                    await target.InitializeAsync(cancellationToken);
                    await target.SeedAsync(
                        request.Configuration.Seed,
                        dataShape,
                        cancellationToken);
                    var correctness = await target.RunCorrectnessGateAsync(cancellationToken);
                    providerMetadata.Add(new BenchmarkProviderMetadata(
                        provider,
                        target.ProviderVersion,
                        target.ProviderConfiguration));

                    foreach (var workload in request.Workloads)
                    {
                        var benchmarkCase = new BenchmarkCase(provider, form, workload);
                        var warmupOnly = request.Role == BenchmarkExecutionRole.UntimedWarmup;
                        var warmupIterations = warmupOnly ? request.Configuration.WarmupIterations : 0;
                        var measurementIterations = warmupOnly ? 0 : request.Configuration.MeasurementIterations;
                        progress(
                            $"[{benchmarkCase.Identity}] warmup {warmupIterations}, measure at least " +
                            $"{measurementIterations} iterations/{request.Configuration.MinimumMeasuredOperations} operations/" +
                            $"{request.Configuration.MinimumSteadyStateDurationSeconds} seconds");
                        var totalIterations = warmupIterations + measurementIterations;
                        await target.PrepareWorkloadAsync(
                            workload,
                            totalIterations,
                            request.Configuration.OperationsPerIteration,
                            cancellationToken);
                        for (var iteration = 0; iteration < warmupIterations; iteration++)
                        {
                            await target.PrepareIterationAsync(workload, iteration, cancellationToken);
                            await target.ExecuteAsync(
                                workload,
                                iteration,
                                request.Configuration.OperationsPerIteration,
                                request.Configuration.Concurrency,
                                cancellationToken);
                            await target.ValidateIterationAsync(workload, cancellationToken);
                        }
                        if (warmupOnly)
                            continue;

                        var samples = new List<BenchmarkSample>(request.Configuration.MeasurementIterations);
                        long measuredOperations = 0;
                        long measuredElapsedNanoseconds = 0;
                        var minimumElapsedNanoseconds =
                            request.Configuration.MinimumSteadyStateDurationSeconds * 1_000_000_000L;
                        for (var iteration = 0;
                             iteration < request.Configuration.MeasurementIterations ||
                             measuredOperations < request.Configuration.MinimumMeasuredOperations ||
                             measuredElapsedNanoseconds < minimumElapsedNanoseconds;
                             iteration++)
                        {
                            var globalIteration = iteration;
                            if (iteration >= request.Configuration.MeasurementIterations)
                            {
                                await target.PrepareWorkloadAsync(
                                    workload,
                                    totalIterations: 1,
                                    request.Configuration.OperationsPerIteration,
                                    cancellationToken);
                            }
                            await target.PrepareIterationAsync(workload, globalIteration, cancellationToken);
                            var capturesStorage = CapturesStorage(workload);
                            var storageBefore = capturesStorage
                                ? await target.CaptureStorageAsync(cancellationToken)
                                : null;
                            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                            using var measurement = signals.BeginMeasurement();
                            var timestamp = timeProvider.GetTimestamp();
                            var execution = await target.ExecuteAsync(
                                workload,
                                globalIteration,
                                request.Configuration.OperationsPerIteration,
                                request.Configuration.Concurrency,
                                cancellationToken);
                            ValidateExecution(execution, benchmarkCase);
                            var elapsedNanoseconds = Math.Max(
                                1,
                                (long)Math.Round(timeProvider.GetElapsedTime(timestamp).TotalNanoseconds));
                            var signalSnapshot = measurement.Complete();
                            QueryBranchEvidence.EnsureObserved(
                                workload,
                                request.Configuration.OperationsPerIteration,
                                execution.RoundTrips ?? signalSnapshot.ObservableRoundTrips);
                            var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
                            await target.ValidateIterationAsync(workload, cancellationToken);
                            var storageAfter = capturesStorage
                                ? await target.CaptureStorageAsync(cancellationToken)
                                : null;
                            var sample = new BenchmarkSample(
                                iteration,
                                execution.Operations,
                                elapsedNanoseconds,
                                allocatedBytes,
                                execution.RoundTrips ?? signalSnapshot.ObservableRoundTrips,
                                execution.LogicalPayloadBytes,
                                execution.LogicalMutations,
                                storageBefore,
                                storageAfter,
                                Merge(execution.ProviderWork, signalSnapshot.ToProviderWork()),
                                execution.OperationLatencyNanoseconds);
                            samples.Add(sample);
                            measuredOperations += sample.Operations;
                            measuredElapsedNanoseconds += sample.ElapsedNanoseconds;
                            await writer.AppendSampleAsync(new RawBenchmarkRecord(benchmarkCase, sample), cancellationToken);
                        }
                        caseResults.Add(new BenchmarkCaseResult(
                            benchmarkCase,
                            correctness,
                            casePlanArtifacts.TryGetValue((provider, form, workload), out var applicablePlans)
                                ? applicablePlans.ToArray()
                                : [],
                            BenchmarkSummarizer.Summarize(benchmarkCase.Identity, samples),
                            samples));
                    }
                }
            }

            var distinctProviders = DistinctProviders(providerMetadata);
            var regressions = request.BaselineRun is null
                ? []
                : await CompareBaselineAsync(
                    request.BaselineRun,
                    caseResults,
                    executionConfiguration,
                    machine,
                    distinctProviders,
                    request.Configuration.Mode == BenchmarkRunMode.Scheduled
                        ? RegressionPolicy.Scheduled
                        : RegressionPolicy.Smoke,
                    cancellationToken);
            var baselineEligibility = BaselineEligibilityEvaluator.Evaluate(
                executionConfiguration,
                request.Workloads,
                machine,
                caseResults);
            var report = new BenchmarkRunReport(
                BenchmarkProfiles.SchemaVersion,
                runId,
                request.Configuration.Mode,
                caseResults,
                regressions,
                baselineEligibility,
                dataShape);
            await writer.WriteProvidersAsync(
                distinctProviders,
                cancellationToken);
            await writer.WriteReportAsync(report, cancellationToken);
            if (request.Role == BenchmarkExecutionRole.Measured)
            {
                await writer.WriteConsumerEvidenceAsync(
                    BenchmarkConsumerEvidenceReport.Create(
                        report,
                        executionConfiguration,
                        machine,
                        distinctProviders,
                        layout,
                        request.IndependentRun),
                    cancellationToken);
            }
            manifest = manifest with
            {
                Status = "completed",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                PlanArtifacts = planArtifacts.Distinct(StringComparer.Ordinal).ToArray()
            };
            await writer.WriteManifestAsync(manifest, cancellationToken);
            TryDeleteDirectory(scratch);
            var confirmedRegression = request.RegressionConfirmationRun &&
                                      regressions.Any(evaluation => evaluation.Regressed && evaluation.RequiresConfirmation);
            return new BenchmarkRunResult(runDirectory, report, confirmedRegression);
        }
        catch (Exception exception)
        {
            try
            {
                await writer.WriteProvidersAsync(DistinctProviders(providerMetadata), CancellationToken.None);
                await writer.WriteManifestAsync(manifest with
                {
                    Status = "failed",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    PlanArtifacts = planArtifacts.Distinct(StringComparer.Ordinal).ToArray(),
                    Failure = new BenchmarkRunFailure(
                        exception.GetType().FullName ?? exception.GetType().Name,
                        string.IsNullOrWhiteSpace(exception.Message) ? "No failure message was provided." : exception.Message)
                }, CancellationToken.None);
            }
            catch (Exception artifactFailure)
            {
                exception.Data["Groundwork.PhysicalStorage.BenchmarkArtifactFailure"] = artifactFailure;
            }
            throw;
        }
    }

    private static void ValidateExecution(WorkloadExecution execution, BenchmarkCase benchmarkCase)
    {
        if (execution.Operations <= 0 ||
            execution.OperationLatencyNanoseconds is null ||
            execution.OperationLatencyNanoseconds.Count != execution.Operations ||
            execution.OperationLatencyNanoseconds.Any(latency => latency <= 0))
        {
            throw new InvalidOperationException(
                $"[{benchmarkCase.Identity}] target must return one positive raw latency observation per operation.");
        }
    }

    private static async Task<IReadOnlyList<RegressionEvaluation>> CompareBaselineAsync(
        string baselineRun,
        IReadOnlyList<BenchmarkCaseResult> candidate,
        BenchmarkRunConfiguration candidateConfiguration,
        BenchmarkMachineMetadata candidateMachine,
        IReadOnlyList<BenchmarkProviderMetadata> candidateProviders,
        RegressionPolicy policy,
        CancellationToken cancellationToken)
    {
        var baseline = await BenchmarkArtifactWriter.ReadBaselineAsync(baselineRun, cancellationToken);
        var compatibility = BaselineCompatibilityEvaluator.Evaluate(
            candidateConfiguration,
            candidateMachine,
            candidateProviders,
            baseline);
        if (!compatibility.IsCompatible)
        {
            return candidate.Select(result => new RegressionEvaluation(
                    result.Case.Identity,
                    false,
                    policy.RequiresConfirmation,
                    [],
                    compatibility.Diagnostics))
                .ToArray();
        }

        var groups = baseline.Records.GroupBy(record => record.Case.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BenchmarkSample>)group.Select(record => record.Sample).ToArray(), StringComparer.Ordinal);
        return candidate.Select(result => groups.TryGetValue(result.Case.Identity, out var baselineSamples)
                ? WithDiagnostics(
                    RegressionEvaluator.Compare(result.Case.Identity, baselineSamples, result.Samples, policy),
                    compatibility.Diagnostics)
                : new RegressionEvaluation(
                    result.Case.Identity,
                    false,
                    policy.RequiresConfirmation,
                    [],
                    ["No matching case exists in the baseline run."]))
            .ToArray();
    }

    private static RegressionEvaluation WithDiagnostics(
        RegressionEvaluation evaluation,
        IReadOnlyList<string> diagnostics) => diagnostics.Count == 0
        ? evaluation
        : evaluation with { Diagnostics = evaluation.Diagnostics.Concat(diagnostics).ToArray() };

    private static BenchmarkProviderMetadata[] DistinctProviders(
        IEnumerable<BenchmarkProviderMetadata> providers) => providers
        .GroupBy(metadata => new { metadata.Provider, metadata.Version })
        .Select(group => group.First())
        .OrderBy(metadata => metadata.Provider)
        .ToArray();

    private static bool CapturesStorage(BenchmarkWorkload workload) => workload is
        BenchmarkWorkload.Insert or
        BenchmarkWorkload.Update or
        BenchmarkWorkload.Delete or
        BenchmarkWorkload.UnitOfWork or
        BenchmarkWorkload.StorageGrowth;

    private static IReadOnlyDictionary<string, long> Merge(
        IReadOnlyDictionary<string, long> first,
        IReadOnlyDictionary<string, long> second) =>
        first.Concat(second)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
