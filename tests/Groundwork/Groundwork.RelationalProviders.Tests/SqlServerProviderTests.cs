using System.Data.Common;
using Groundwork.Documents.Store;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.Materialization;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerProviderTests : RelationalProviderContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task IndependentOperationsUseTheProviderPoolWithoutGlobalSerialization()
    {
        var builder = new SqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 2 };
        var blockerBuilder = new SqlConnectionStringBuilder(builder.ConnectionString) { Pooling = false };
        var twoConnectionsOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = await SqlServerDocumentStoreFactory.CreateAsync(
            builder.ConnectionString,
            manifest,
            RelationalTestManifests.SqlServerProvider,
            () => new SqlConnection(builder.ConnectionString),
            () =>
            {
                var connection = new SqlConnection(builder.ConnectionString);
                connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == System.Data.ConnectionState.Open && Interlocked.Increment(ref opened) == 2)
                        twoConnectionsOpened.TrySetResult();
                };
                return connection;
            });
        var blocker = await RelationalSessionPoolPressure.BlockSqlServerDocumentsAsync(blockerBuilder.ConnectionString);

        await RelationalSessionPoolPressure.AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
            store,
            twoConnectionsOpened.Task,
            blocker);
    }

    protected override async Task<IRelationalProviderHarness> CreateHarnessAsync()
    {
        var connectionString = container.GetConnectionString();
        var connection = new SqlConnection(connectionString);
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = await SqlServerDocumentStoreFactory.CreateAsync(
            connectionString,
            manifest,
            RelationalTestManifests.SqlServerProvider);
        return new SqlServerProviderHarness(connection, store, manifest, connectionString);
    }

    private sealed class SqlServerProviderHarness(
        SqlConnection connection,
        IDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        string connectionString)
        : IRelationalProviderHarness
    {
        public IDocumentStore Store { get; } = store;

        public Task MaterializeAsync() =>
            new SqlServerGroundworkMaterializer(connection).MaterializeAsync(manifest, RelationalTestManifests.SqlServerProvider);

        public async Task<IDocumentStore> ApplyManifestAsync(Groundwork.Core.Manifests.StorageManifest targetManifest)
        {
            return await SqlServerDocumentStoreFactory.CreateAsync(
                connectionString,
                targetManifest,
                RelationalTestManifests.SqlServerProvider);
        }

        public async Task<long> CountSchemaHistoryRowsAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM groundwork_schema_history
                WHERE manifest_id = @manifestId AND provider_name = @providerName;
                """;
            command.Parameters.AddWithValue("@manifestId", manifest.Identity.Value);
            command.Parameters.AddWithValue("@providerName", RelationalTestManifests.SqlServerProvider.Name);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();

        private async Task EnsureOpenAsync()
        {
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();
        }
    }
}
