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
        await RelationalSessionPoolPressure.AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
            () => new SqlConnection(builder.ConnectionString));
    }

    protected override Task<IRelationalProviderHarness> CreateHarnessAsync()
    {
        var connection = new SqlConnection(container.GetConnectionString());
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = new SqlServerDocumentStore(container.GetConnectionString(), manifest);
        return Task.FromResult<IRelationalProviderHarness>(new SqlServerProviderHarness(connection, store, manifest, container.GetConnectionString()));
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
            await new SqlServerGroundworkMaterializer(connection).MaterializeAsync(targetManifest, RelationalTestManifests.SqlServerProvider);
            return new SqlServerDocumentStore(connectionString, targetManifest);
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
