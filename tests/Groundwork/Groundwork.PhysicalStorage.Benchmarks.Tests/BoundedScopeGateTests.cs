using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BoundedScopeGateTests
{
    [Fact]
    public void Tenant_b_open_sentinel_is_rejected_even_when_page_and_count_agree()
    {
        const string sentinelId = "tenant-b-open-sentinel";
        var page = new DocumentQueryResult(
            [Envelope(sentinelId, 2), Envelope("tenant-a", 1)],
            TotalCount: 2);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BoundedScopeGate.EnsureExpectedTenantPage(page, independentCount: 2, expectedCount: 2, sentinelId));

        Assert.Contains("sentinel", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_and_independent_count_must_both_equal_the_seed_derived_expectation()
    {
        var page = new DocumentQueryResult([Envelope("tenant-a", 1)], TotalCount: 2);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BoundedScopeGate.EnsureExpectedTenantPage(page, independentCount: 2, expectedCount: 1, "tenant-b-open-sentinel"));

        Assert.Contains("expected tenant-A count 1", exception.Message, StringComparison.Ordinal);
    }

    private static DocumentEnvelope Envelope(string id, int rank) => new(
        BenchmarkModelFactory.DocumentKind,
        id,
        "1",
        1,
        $"{{\"status\":\"open\",\"rank\":{rank},\"category\":\"test\"}}",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
