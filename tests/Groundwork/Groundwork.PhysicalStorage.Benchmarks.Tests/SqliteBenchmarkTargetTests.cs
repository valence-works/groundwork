using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqliteBenchmarkTargetTests : IAsyncDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), $"groundwork-benchmark-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_target_passes_correctness_scope_occ_and_native_plan_gates(PhysicalStorageForm form)
    {
        await using var target = new SqliteBenchmarkTarget(form, Guid.NewGuid().ToString("N")[..8], scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(BenchmarkProfiles.ReproducibleSeed, 40, CancellationToken.None);

        var correctness = await target.RunCorrectnessGateAsync(CancellationToken.None);
        var plan = await target.RunNativePlanGateAsync(CancellationToken.None);

        Assert.True(correctness.ScopeIsolation);
        Assert.True(correctness.OptimisticConcurrency);
        Assert.True(correctness.UnitOfWorkRollback);
        Assert.True(correctness.BoundedQuery);
        Assert.True(correctness.MixedOrdering);
        Assert.Contains(plan.IndexName, plan.NativePlan, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        return ValueTask.CompletedTask;
    }
}
