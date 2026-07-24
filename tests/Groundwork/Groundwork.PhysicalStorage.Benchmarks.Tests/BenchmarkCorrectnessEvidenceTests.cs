using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkCorrectnessEvidenceTests
{
    private static readonly BenchmarkObservableResult[] CanonicalResults =
    [
        new(0, "seed-00000001", "selected", 1, 2, """{"status":"open","rank":9}"""),
        new(1, "seed-00000002", "selected", 3, 2, """{"status":"open","rank":4}""")
    ];

    public static TheoryData<BenchmarkObservableResult> ChangedResults => new()
    {
        CanonicalResults[1] with { Identity = "seed-00000003" },
        CanonicalResults[1] with { Status = "updated" },
        CanonicalResults[1] with { Version = 4 },
        CanonicalResults[1] with { Count = 3 },
        CanonicalResults[1] with { Payload = """{"status":"closed","rank":4}""" }
    };

    [Theory]
    [MemberData(nameof(ChangedResults))]
    public void Digest_changes_when_an_observable_outcome_changes(BenchmarkObservableResult changed)
    {
        var original = BenchmarkObservableResultVector.Create(CanonicalResults);
        var modified = BenchmarkObservableResultVector.Create([CanonicalResults[0], changed]);

        Assert.NotEqual(original.Digest, modified.Digest);
    }

    [Fact]
    public void Digest_excludes_provider_form_machine_and_timing_metadata()
    {
        var sqliteEntityLinuxTiming = BenchmarkObservableResultVector.Create(CanonicalResults);
        var sqlServerSharedWindowsTiming = BenchmarkObservableResultVector.Create(
            CanonicalResults.Select(result => result with { }).ToArray());

        Assert.Equal(sqliteEntityLinuxTiming.Digest, sqlServerSharedWindowsTiming.Digest);
    }

    [Fact]
    public void Vector_rejects_noncanonical_order()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            BenchmarkObservableResultVector.Create([CanonicalResults[1], CanonicalResults[0]]));

        Assert.Contains("canonical position", exception.Message, StringComparison.Ordinal);
    }
}
