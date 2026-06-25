using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.PostgreSql.Materialization;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public static class PostgreSqlDocumentStoreFactory
{
    public static async Task<PostgreSqlDocumentStoreHandle> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await new PostgreSqlGroundworkMaterializer(connection).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                cancellationToken);
            return new PostgreSqlDocumentStoreHandle(connection, new PostgreSqlDocumentStore(connection, manifest, ambientTenantId));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, PostgreSqlGroundworkCapabilities.Runtime(provider), PostgreSqlGroundworkCapabilities.Materialization(provider));
}

public sealed class PostgreSqlDocumentStoreHandle(NpgsqlConnection connection, PostgreSqlDocumentStore store) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;
    public PostgreSqlDocumentStore Store { get; } = store;

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
