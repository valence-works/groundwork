using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public enum NativePlanOperation
{
    Selection,
    Count
}

public sealed record BenchmarkPlanRequest(
    BenchmarkWorkload Workload,
    NativePlanOperation Operation,
    bool Ordered,
    int? Skip,
    int? Take);

public static class BenchmarkPlanRequests
{
    private static readonly IReadOnlyList<BenchmarkPlanRequest> Canonical =
    [
        new(BenchmarkWorkload.IndexedQuery, NativePlanOperation.Selection, Ordered: false, Skip: null, Take: 20),
        new(BenchmarkWorkload.IndexedQuery, NativePlanOperation.Count, Ordered: false, Skip: null, Take: 20),
        new(BenchmarkWorkload.MixedCompoundOrdering, NativePlanOperation.Selection, Ordered: true, Skip: null, Take: 20),
        new(BenchmarkWorkload.MixedCompoundOrdering, NativePlanOperation.Count, Ordered: true, Skip: null, Take: 20),
        new(BenchmarkWorkload.PaginationAndCount, NativePlanOperation.Selection, Ordered: true, Skip: 7, Take: 20),
        new(BenchmarkWorkload.PaginationAndCount, NativePlanOperation.Count, Ordered: true, Skip: 7, Take: 20)
    ];

    public static IReadOnlyList<BenchmarkPlanRequest> ForWorkloads(IEnumerable<BenchmarkWorkload> workloads)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        var selected = workloads.ToHashSet();
        return Canonical.Where(request => selected.Contains(request.Workload)).ToArray();
    }
}

public sealed record NativePlanEvidence(
    BenchmarkPlanRequest Request,
    string Provider,
    string StorageForm,
    string QueryIdentity,
    string PhysicalObject,
    string IndexName,
    string NativePlan,
    IReadOnlyList<string> Assertions);

public sealed record CorrectnessGateResult(
    bool ScopeIsolation,
    bool OptimisticConcurrency,
    bool UnitOfWorkRollback,
    bool BoundedQuery,
    bool MixedOrdering);

public sealed record WorkloadExecution(
    int Operations,
    long LogicalPayloadBytes,
    long LogicalMutations,
    long? RoundTrips,
    IReadOnlyDictionary<string, long> ProviderWork,
    IReadOnlyList<long> OperationLatencyNanoseconds);

public interface IPhysicalStorageBenchmarkTarget : IAsyncDisposable
{
    BenchmarkProvider Provider { get; }
    PhysicalStorageForm StorageForm { get; }
    string ProviderVersion { get; }
    IReadOnlyDictionary<string, string> ProviderConfiguration { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task SeedAsync(int seed, int count, CancellationToken cancellationToken);
    Task SeedAsync(int seed, BenchmarkDataShape shape, CancellationToken cancellationToken) =>
        SeedAsync(seed, shape.DatasetSize, cancellationToken);
    Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NativePlanEvidence>> RunNativePlanGatesAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        CancellationToken cancellationToken);
    Task PrepareWorkloadAsync(BenchmarkWorkload workload, int totalIterations, int operationsPerIteration, CancellationToken cancellationToken);
    Task PrepareIterationAsync(BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);
    Task<WorkloadExecution> ExecuteAsync(BenchmarkWorkload workload, int iteration, int operations, int concurrency, CancellationToken cancellationToken);
    Task ValidateIterationAsync(BenchmarkWorkload workload, CancellationToken cancellationToken);
    Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken);
}
