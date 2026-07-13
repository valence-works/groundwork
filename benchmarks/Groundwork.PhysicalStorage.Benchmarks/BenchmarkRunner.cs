using System.Diagnostics;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkRunRequest(
    string RepositoryRoot,
    BenchmarkRunConfiguration Configuration,
    IReadOnlyList<BenchmarkWorkload> Workloads,
    string? OutputDirectory,
    string? BaselineRun,
    bool AllowContainers,
    bool RegressionConfirmationRun);

public sealed record BenchmarkRunResult(
    string RunDirectory,
    BenchmarkRunReport Report,
    bool ConfirmedRegression);

public sealed class BenchmarkRunner(Action<string>? progress = null)
{
    private readonly Action<string> progress = progress ?? (_ => { });

    public async Task<BenchmarkRunResult> RunAsync(
        BenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Configuration.Validate();
        if (request.Workloads.Count == 0)
            throw new ArgumentException("At least one workload must be selected.", nameof(request));

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
        await using var environment = new BenchmarkProviderEnvironment();
        using var signals = new DatabaseSignalCollector();
        var started = DateTimeOffset.UtcNow;
        var planArtifacts = new List<string>();
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
            null);
        await writer.WriteManifestAsync(manifest, cancellationToken);
        await writer.WriteMachineAsync(machine, cancellationToken);
        await writer.WriteConfigurationAsync(request.Configuration, cancellationToken);

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
                    var instance = $"p{(int)provider}_f{(int)form}_{BenchmarkProfiles.ReproducibleSeed}";
                    await using var target = environment.CreateTarget(
                        provider,
                        form,
                        instance,
                        scratch,
                        request.Configuration.MigrationDatasetSize);
                    progress($"[{provider}/{form}] materializing and seeding {request.Configuration.DatasetSize:N0} documents");
                    await target.InitializeAsync(cancellationToken);
                    await target.SeedAsync(
                        request.Configuration.Seed,
                        request.Configuration.DatasetSize,
                        cancellationToken);
                    var correctness = await target.RunCorrectnessGateAsync(cancellationToken);
                    var planEvidence = await target.RunNativePlanGateAsync(cancellationToken);
                    var planCase = new BenchmarkCase(provider, form, BenchmarkWorkload.IndexedQuery);
                    var planArtifact = await writer.WritePlanAsync(planCase, planEvidence, cancellationToken);
                    planArtifacts.Add(planArtifact);
                    providerMetadata.Add(new BenchmarkProviderMetadata(
                        provider,
                        target.ProviderVersion,
                        target.ProviderConfiguration));

                    foreach (var workload in request.Workloads)
                    {
                        var benchmarkCase = new BenchmarkCase(provider, form, workload);
                        progress($"[{benchmarkCase.Identity}] warmup {request.Configuration.WarmupIterations}, measure {request.Configuration.MeasurementIterations}");
                        var totalIterations = request.Configuration.WarmupIterations + request.Configuration.MeasurementIterations;
                        await target.PrepareWorkloadAsync(
                            workload,
                            totalIterations,
                            request.Configuration.OperationsPerIteration,
                            cancellationToken);
                        for (var iteration = 0; iteration < request.Configuration.WarmupIterations; iteration++)
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

                        var samples = new List<BenchmarkSample>(request.Configuration.MeasurementIterations);
                        for (var iteration = 0; iteration < request.Configuration.MeasurementIterations; iteration++)
                        {
                            var globalIteration = request.Configuration.WarmupIterations + iteration;
                            await target.PrepareIterationAsync(workload, globalIteration, cancellationToken);
                            var capturesStorage = CapturesStorage(workload);
                            var storageBefore = capturesStorage
                                ? await target.CaptureStorageAsync(cancellationToken)
                                : null;
                            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                            using var measurement = signals.BeginMeasurement();
                            var timestamp = Stopwatch.GetTimestamp();
                            var execution = await target.ExecuteAsync(
                                workload,
                                globalIteration,
                                request.Configuration.OperationsPerIteration,
                                request.Configuration.Concurrency,
                                cancellationToken);
                            var elapsedTicks = Stopwatch.GetTimestamp() - timestamp;
                            var signalSnapshot = measurement.Complete();
                            var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
                            await target.ValidateIterationAsync(workload, cancellationToken);
                            var storageAfter = capturesStorage
                                ? await target.CaptureStorageAsync(cancellationToken)
                                : null;
                            var sample = new BenchmarkSample(
                                iteration,
                                execution.Operations,
                                Math.Max(1, (long)Math.Round(elapsedTicks * 1_000_000_000d / Stopwatch.Frequency)),
                                allocatedBytes,
                                execution.RoundTrips ?? signalSnapshot.ObservableRoundTrips,
                                execution.LogicalPayloadBytes,
                                execution.LogicalMutations,
                                storageBefore,
                                storageAfter,
                                Merge(execution.ProviderWork, signalSnapshot.ToProviderWork()));
                            samples.Add(sample);
                            await writer.AppendSampleAsync(new RawBenchmarkRecord(benchmarkCase, sample), cancellationToken);
                        }
                        caseResults.Add(new BenchmarkCaseResult(
                            benchmarkCase,
                            correctness,
                            planArtifact,
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
                    request.Configuration,
                    machine,
                    distinctProviders,
                    request.Configuration.Mode == BenchmarkRunMode.Scheduled
                        ? RegressionPolicy.Scheduled
                        : RegressionPolicy.Smoke,
                    cancellationToken);
            var baselineEligibility = BaselineEligibilityEvaluator.Evaluate(
                request.Configuration,
                request.Workloads,
                machine,
                caseResults);
            var report = new BenchmarkRunReport(
                BenchmarkProfiles.SchemaVersion,
                runId,
                request.Configuration.Mode,
                caseResults,
                regressions,
                baselineEligibility);
            await writer.WriteProvidersAsync(
                distinctProviders,
                cancellationToken);
            await writer.WriteReportAsync(report, cancellationToken);
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
