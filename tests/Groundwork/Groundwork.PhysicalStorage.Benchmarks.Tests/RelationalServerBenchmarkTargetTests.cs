using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class RelationalServerBenchmarkTargetTests(
    RelationalBenchmarkProviderFixture fixture) : IClassFixture<RelationalBenchmarkProviderFixture>
{
    public static TheoryData<BenchmarkProvider, PhysicalStorageForm> ProviderForms => new()
    {
        { BenchmarkProvider.SqlServer, PhysicalStorageForm.SharedDocuments },
        { BenchmarkProvider.SqlServer, PhysicalStorageForm.DedicatedDocumentTable },
        { BenchmarkProvider.SqlServer, PhysicalStorageForm.PhysicalEntityTable },
        { BenchmarkProvider.PostgreSql, PhysicalStorageForm.SharedDocuments },
        { BenchmarkProvider.PostgreSql, PhysicalStorageForm.DedicatedDocumentTable },
        { BenchmarkProvider.PostgreSql, PhysicalStorageForm.PhysicalEntityTable }
    };

    [Theory]
    [MemberData(nameof(ProviderForms))]
    public async Task Schema_drift_blocks_server_target_initialization_before_timing(
        BenchmarkProvider provider,
        PhysicalStorageForm form)
    {
        await using var target = fixture.Environment.CreateTarget(
            provider,
            form,
            Guid.NewGuid().ToString("N")[..8],
            fixture.Scratch,
            migrationDatasetSize: 5);

        var exception = provider switch
        {
            BenchmarkProvider.SqlServer => await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(
                () => ((SqlServerBenchmarkTarget)target).InitializeAsync(
                    DropSqlServerIndexAsync,
                    CancellationToken.None)),
            BenchmarkProvider.PostgreSql => await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(
                () => ((PostgreSqlBenchmarkTarget)target).InitializeAsync(
                    DropPostgreSqlIndexAsync,
                    CancellationToken.None)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

        Assert.Contains("admission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ProviderForms))]
    public async Task Native_plan_capture_never_changes_the_server_measured_shape(
        BenchmarkProvider provider,
        PhysicalStorageForm form)
    {
        await using var target = fixture.Environment.CreateTarget(
            provider,
            form,
            Guid.NewGuid().ToString("N")[..8],
            fixture.Scratch,
            migrationDatasetSize: 5);
        await target.InitializeAsync(CancellationToken.None);
        await target.SeedAsync(
            BenchmarkProfiles.ReproducibleSeed,
            new BenchmarkDataShape(250, 0, 100),
            CancellationToken.None);
        var beforePlans = await target.CaptureStorageAsync(CancellationToken.None);

        var planFailure = await Record.ExceptionAsync(() =>
            target.RunNativePlanGatesAsync(
                BenchmarkPlanRequests.ForWorkloads([BenchmarkWorkload.IndexedQuery]),
                CancellationToken.None));
        var afterPlans = await target.CaptureStorageAsync(CancellationToken.None);

        if (planFailure is not null)
            Assert.Contains("native-plan gate rejected", planFailure.Message, StringComparison.Ordinal);
        Assert.Equal(beforePlans.PrimaryRows, afterPlans.PrimaryRows);
        Assert.Equal(beforePlans.LinkedRows, afterPlans.LinkedRows);
    }

    private static async Task DropSqlServerIndexAsync(
        string connectionString,
        BenchmarkPhysicalModel model,
        CancellationToken cancellationToken)
    {
        var index = model.Route.Indexes.Single();
        var table = (model.Route.LinkedIndexStorage ?? model.Route.PrimaryStorage).Name.Identifier;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX {SqlServerQ(index.Name.Identifier)} ON {SqlServerQ(table)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropPostgreSqlIndexAsync(
        string connectionString,
        BenchmarkPhysicalModel model,
        CancellationToken cancellationToken)
    {
        var index = model.Route.Indexes.Single();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX {PostgreSqlQ(index.Name.Identifier)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SqlServerQ(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string PostgreSqlQ(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

public sealed class RelationalBenchmarkProviderFixture : IAsyncLifetime
{
    internal BenchmarkProviderEnvironment Environment { get; } = new();
    internal string Scratch { get; } =
        Path.Combine(Path.GetTempPath(), $"groundwork-relational-benchmark-test-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Scratch);
        await Environment.StartAsync(
            [BenchmarkProvider.SqlServer, BenchmarkProvider.PostgreSql],
            allowContainers: true,
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await Environment.DisposeAsync();
        if (Directory.Exists(Scratch))
            Directory.Delete(Scratch, recursive: true);
    }
}
