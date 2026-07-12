using Groundwork.Documents.Store;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.Materialization;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class PostgreSqlProviderTests : RelationalProviderContractTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task IndependentOperationsUseTheProviderPoolWithoutGlobalSerialization()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 2 };
        await RelationalSessionPoolPressure.AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
            () => new NpgsqlConnection(builder.ConnectionString));
    }

    protected override Task<IRelationalProviderHarness> CreateHarnessAsync()
    {
        var connection = new NpgsqlConnection(container.GetConnectionString());
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = new PostgreSqlDocumentStore(container.GetConnectionString(), manifest);
        return Task.FromResult<IRelationalProviderHarness>(new PostgreSqlProviderHarness(connection, store, manifest, container.GetConnectionString()));
    }

    private sealed class PostgreSqlProviderHarness(
        NpgsqlConnection connection,
        IDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        string connectionString)
        : IRelationalProviderHarness
    {
        public IDocumentStore Store { get; } = store;

        public Task MaterializeAsync() =>
            new PostgreSqlGroundworkMaterializer(connection).MaterializeAsync(manifest, RelationalTestManifests.PostgreSqlProvider);

        public async Task<IDocumentStore> ApplyManifestAsync(Groundwork.Core.Manifests.StorageManifest targetManifest)
        {
            await new PostgreSqlGroundworkMaterializer(connection).MaterializeAsync(targetManifest, RelationalTestManifests.PostgreSqlProvider);
            return new PostgreSqlDocumentStore(connectionString, targetManifest);
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
            command.Parameters.AddWithValue("@providerName", RelationalTestManifests.PostgreSqlProvider.Name);
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
