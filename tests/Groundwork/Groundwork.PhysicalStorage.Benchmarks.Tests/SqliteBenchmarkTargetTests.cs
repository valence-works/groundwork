using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Sqlite;
using Microsoft.Data.Sqlite;
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
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(250, 0, 100),
            CancellationToken.None);

        var correctness = await target.RunCorrectnessGateAsync(CancellationToken.None);
        var beforePlans = await target.CaptureStorageAsync(CancellationToken.None);
        var plans = await target.RunNativePlanGatesAsync(
            BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
            CancellationToken.None);
        var afterPlans = await target.CaptureStorageAsync(CancellationToken.None);

        Assert.True(correctness.ScopeIsolation);
        Assert.True(correctness.OptimisticConcurrency);
        Assert.True(correctness.UnitOfWorkRollback);
        Assert.True(correctness.BoundedQuery);
        Assert.True(correctness.MixedOrdering);
        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Contains(plan.IndexName, plan.NativePlan, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforePlans.PrimaryRows, afterPlans.PrimaryRows);
        Assert.Equal(beforePlans.LinkedRows, afterPlans.LinkedRows);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_plan_gate_rejects_a_scan_without_changing_the_measured_shape(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using var target = new SqliteBenchmarkTarget(form, instance, scratch, 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(250, 0, 100),
            CancellationToken.None);
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            instance,
            SqliteGroundworkCapabilities.Provider,
            SqliteGroundworkCapabilities.PhysicalNames);
        await ReplaceIndexWithScanShapeAsync(DatabasePath(instance, form), model);
        var beforePlans = await target.CaptureStorageAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.RunNativePlanGatesAsync(
                BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
                CancellationToken.None));
        var afterPlans = await target.CaptureStorageAsync(CancellationToken.None);

        Assert.Contains("native-plan gate rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SCAN", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforePlans.PrimaryRows, afterPlans.PrimaryRows);
        Assert.Equal(beforePlans.LinkedRows, afterPlans.LinkedRows);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SQLite_schema_drift_blocks_target_initialization_before_timing(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        await using var target = new SqliteBenchmarkTarget(form, instance, scratch, 5);

        var exception = await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
            target.InitializeAsync(
                async (connectionString, model, cancellationToken) =>
                    await DropIndexAsync(
                        new SqliteConnectionStringBuilder(connectionString).DataSource,
                        model.Route.Indexes.Single().Name.Identifier,
                        cancellationToken),
                CancellationToken.None));

        Assert.Contains("admission", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private string DatabasePath(string instance, PhysicalStorageForm form) =>
        Path.Combine(scratch, $"sqlite-{instance}-{form}.db");

    private static async Task DropIndexAsync(
        string databasePath,
        string indexName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX \"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceIndexWithScanShapeAsync(
        string databasePath,
        BenchmarkPhysicalModel model)
    {
        var indexName = model.Route.Indexes.Single().Name.Identifier;
        var table = (model.Route.LinkedIndexStorage ?? model.Route.PrimaryStorage).Name.Identifier;
        var rank = model.Route.ProjectedColumns.Single(column => column.Definition.Path == "rank").Column.Identifier;
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP INDEX "{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}";
            CREATE INDEX "{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}"
                ON "{table.Replace("\"", "\"\"", StringComparison.Ordinal)}"
                ("{rank.Replace("\"", "\"\"", StringComparison.Ordinal)}");
            """;
        await command.ExecuteNonQueryAsync();
    }
}
