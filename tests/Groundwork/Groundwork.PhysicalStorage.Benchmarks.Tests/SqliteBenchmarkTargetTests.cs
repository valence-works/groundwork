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
        var plans = await target.RunNativePlanGatesAsync(
            BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
            CancellationToken.None);

        Assert.True(correctness.ScopeIsolation);
        Assert.True(correctness.OptimisticConcurrency);
        Assert.True(correctness.UnitOfWorkRollback);
        Assert.True(correctness.BoundedQuery);
        Assert.True(correctness.MixedOrdering);
        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Contains(plan.IndexName, plan.NativePlan, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Backfill_measurement_is_followed_by_projected_query_validation(PhysicalStorageForm form)
    {
        await using IPhysicalStorageBenchmarkTarget target =
            new SqliteBenchmarkTarget(form, Guid.NewGuid().ToString("N")[..8], scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.PrepareIterationAsync(BenchmarkWorkload.BackfillMigration, 0, CancellationToken.None);

        var execution = await target.ExecuteAsync(
            BenchmarkWorkload.BackfillMigration,
            0,
            operations: 1,
            concurrency: 1,
            CancellationToken.None);
        await target.ValidateIterationAsync(BenchmarkWorkload.BackfillMigration, CancellationToken.None);

        Assert.Equal(5, execution.LogicalMutations);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        return ValueTask.CompletedTask;
    }
}
