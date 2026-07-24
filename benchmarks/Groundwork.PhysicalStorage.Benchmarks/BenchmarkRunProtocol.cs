namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkWorkerInvocation(
    string ProtocolVersion,
    string RunGroupId,
    int Ordinal,
    int IndependentRun,
    BenchmarkExecutionRole Role,
    BenchmarkRunRequest Request)
{
    public string ExpectedGitCommit { get; init; } = string.Empty;
    public string ExpectedGitTreeDigest { get; init; } = string.Empty;
}

public static class BenchmarkRunProtocol
{
    public const string ProtocolVersion = "groundwork.physical-storage.worker/v1";

    public static void ValidateInvocation(BenchmarkWorkerInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.ProtocolVersion != ProtocolVersion)
            throw new InvalidOperationException($"Unsupported worker protocol '{invocation.ProtocolVersion}'.");
        if (string.IsNullOrWhiteSpace(invocation.RunGroupId) || invocation.Ordinal <= 0)
            throw new InvalidOperationException("Worker invocation identity is incomplete.");
        if (string.IsNullOrWhiteSpace(invocation.ExpectedGitCommit) ||
            string.IsNullOrWhiteSpace(invocation.ExpectedGitTreeDigest))
            throw new InvalidOperationException("Worker invocation Git provenance is incomplete.");
        if (invocation.Role != invocation.Request.Role)
            throw new InvalidOperationException("Worker envelope role must match the embedded request role.");
        if (invocation.IndependentRun != invocation.Request.IndependentRun)
            throw new InvalidOperationException("Worker envelope run number must match the embedded request run number.");
        if (invocation.Role == BenchmarkExecutionRole.UntimedWarmup && invocation.IndependentRun != 0)
            throw new InvalidOperationException("Untimed warm-up workers must use independent run zero.");
        if (invocation.Role == BenchmarkExecutionRole.Measured && invocation.IndependentRun <= 0)
            throw new InvalidOperationException("Measured workers must use a positive independent run number.");
        if (invocation.Request.Configuration.Providers.Count != 1 ||
            invocation.Request.Configuration.StorageForms.Count != 1 ||
            invocation.Request.Workloads.Count != 1)
        {
            throw new InvalidOperationException(
                "Each worker must execute exactly one provider, storage form, and workload.");
        }
        if (invocation.Request.Dimensions is not null || invocation.Request.DataShape is null)
            throw new InvalidOperationException("Each worker must carry one fixed data shape and no matrix dimensions.");
        if (invocation.Request.Configuration.DatasetSize != invocation.Request.DataShape.DatasetSize)
            throw new InvalidOperationException("Worker dataset size must match its fixed data shape.");
    }

    public static IReadOnlyList<BenchmarkWorkerInvocation> CreateInvocations(
        BenchmarkRunRequest request,
        string runGroupId,
        BenchmarkGitState? expectedGit = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runGroupId);
        var dimensions = request.Dimensions ?? DimensionsFor(request);
        expectedGit ??= BenchmarkMetadata.CaptureGit(request.RepositoryRoot);
        dimensions.Validate();
        if (request.Configuration.Mode == BenchmarkRunMode.Scheduled &&
            dimensions.IndependentRuns < RegressionPolicy.Scheduled.MinimumIndependentRuns)
        {
            throw new InvalidOperationException(
                $"Scheduled run groups require at least {RegressionPolicy.Scheduled.MinimumIndependentRuns} independent measured processes.");
        }
        var invocations = new List<BenchmarkWorkerInvocation>();
        var ordinal = 0;
        foreach (var shape in dimensions.CreateShapes())
        {
            foreach (var provider in request.Configuration.Providers)
            {
                foreach (var form in request.Configuration.StorageForms)
                {
                    foreach (var workload in request.Workloads)
                    {
                        var workerConfiguration = request.Configuration with
                        {
                            DatasetSize = shape.DatasetSize,
                            Providers = [provider],
                            StorageForms = [form],
                            DataShape = null
                        };
                        Add(BenchmarkExecutionRole.UntimedWarmup, independentRun: 0);
                        for (var independentRun = 1; independentRun <= dimensions.IndependentRuns; independentRun++)
                            Add(BenchmarkExecutionRole.Measured, independentRun);

                        void Add(BenchmarkExecutionRole role, int independentRun)
                        {
                            var invocation = new BenchmarkWorkerInvocation(
                                ProtocolVersion,
                                runGroupId,
                                ++ordinal,
                                independentRun,
                                role,
                                request with
                                {
                                    Configuration = workerConfiguration,
                                    Workloads = [workload],
                                    OutputDirectory = null,
                                    Dimensions = null,
                                    DataShape = shape,
                                    IndependentRun = independentRun,
                                    Role = role,
                                    BaselineRun = null,
                                    RegressionConfirmationRun = false
                                })
                            {
                                ExpectedGitCommit = expectedGit.Commit,
                                ExpectedGitTreeDigest = expectedGit.TreeDigest
                            };
                            ValidateInvocation(invocation);
                            invocations.Add(invocation);
                        }
                    }
                }
            }
        }
        return invocations;
    }

    private static BenchmarkMatrixDimensions DimensionsFor(BenchmarkRunRequest request) =>
        request.Configuration.Mode == BenchmarkRunMode.Scheduled
            ? BenchmarkProfiles.ScheduledDimensions
            : new BenchmarkMatrixDimensions(
                [request.Configuration.DatasetSize],
                PayloadPaddingBytes: [0],
                QuerySelectivityBasisPoints: [5_000],
                IndependentRuns: 1);
}
