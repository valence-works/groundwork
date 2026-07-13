using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record NativePlanEvidence(
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
    IReadOnlyDictionary<string, long> ProviderWork);

public interface IPhysicalStorageBenchmarkTarget : IAsyncDisposable
{
    BenchmarkProvider Provider { get; }
    PhysicalStorageForm StorageForm { get; }
    string ProviderVersion { get; }
    IReadOnlyDictionary<string, string> ProviderConfiguration { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task SeedAsync(int seed, int count, CancellationToken cancellationToken);
    Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken);
    Task<NativePlanEvidence> RunNativePlanGateAsync(CancellationToken cancellationToken);
    Task PrepareWorkloadAsync(BenchmarkWorkload workload, int totalIterations, int operationsPerIteration, CancellationToken cancellationToken);
    Task PrepareIterationAsync(BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);
    Task<WorkloadExecution> ExecuteAsync(BenchmarkWorkload workload, int iteration, int operations, int concurrency, CancellationToken cancellationToken);
    Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken);
}
