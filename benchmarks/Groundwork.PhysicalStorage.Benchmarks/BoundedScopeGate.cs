using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class BoundedScopeGate
{
    public static void EnsureExpectedTenantPage(
        DocumentQueryResult page,
        long independentCount,
        long expectedCount,
        string excludedSentinelId)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedSentinelId);
        if (page.Documents.Any(document => document.Id.Equals(excludedSentinelId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Correctness gate failed: the tenant-B open sentinel leaked into tenant A's bounded page.");
        if (page.TotalCount != expectedCount || independentCount != expectedCount)
        {
            throw new InvalidOperationException(
                $"Correctness gate failed: expected tenant-A count {expectedCount}, but the page reported {page.TotalCount} and the independent count returned {independentCount}.");
        }
    }
}
