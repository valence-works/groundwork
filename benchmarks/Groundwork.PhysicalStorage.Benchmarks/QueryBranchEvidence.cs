namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class QueryBranchEvidence
{
    public static void EnsureObserved(
        BenchmarkWorkload workload,
        int requestedOperations,
        long? observedDatabaseBranches)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedOperations);
        var commandsPerOperation = workload switch
        {
            BenchmarkWorkload.IndexedQuery or BenchmarkWorkload.MixedCompoundOrdering => 2,
            BenchmarkWorkload.PaginationAndCount => 3,
            _ => 0
        };
        if (commandsPerOperation == 0 || observedDatabaseBranches is not { } observed)
            return;
        var expected = (long)commandsPerOperation * requestedOperations;
        if (observed < expected)
        {
            throw new InvalidOperationException(
                $"Observable provider activity covered {observed} database branches for {workload}; expected at least {expected} for {requestedOperations} timed operations.");
        }
    }
}
