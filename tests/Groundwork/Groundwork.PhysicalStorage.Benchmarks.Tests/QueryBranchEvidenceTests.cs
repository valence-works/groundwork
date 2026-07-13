using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class QueryBranchEvidenceTests
{
    [Theory]
    [InlineData(BenchmarkWorkload.IndexedQuery, 2, 3)]
    [InlineData(BenchmarkWorkload.MixedCompoundOrdering, 2, 3)]
    [InlineData(BenchmarkWorkload.PaginationAndCount, 3, 5)]
    public void Observable_provider_signal_must_cover_every_timed_query_branch(
        BenchmarkWorkload workload,
        int commandsPerOperation,
        long observedCommands)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            QueryBranchEvidence.EnsureObserved(workload, requestedOperations: 2, observedCommands));

        Assert.Contains((commandsPerOperation * 2).ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_signal_is_unknown_while_an_explicit_complete_counter_passes()
    {
        QueryBranchEvidence.EnsureObserved(BenchmarkWorkload.IndexedQuery, requestedOperations: 2, null);
        QueryBranchEvidence.EnsureObserved(BenchmarkWorkload.IndexedQuery, requestedOperations: 2, observedDatabaseBranches: 4);
    }
}
