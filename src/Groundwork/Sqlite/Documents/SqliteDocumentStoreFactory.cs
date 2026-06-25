using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public static class SqliteDocumentStoreFactory
{
    public static async Task<SqliteDocumentStoreHandle> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var connection = new SqliteConnection(connectionString);
        try
        {
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                cancellationToken);
            return new SqliteDocumentStoreHandle(connection, new SqliteDocumentStore(connection, manifest, ambientTenantId));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqliteGroundworkCapabilities.Runtime(provider), SqliteGroundworkCapabilities.Materialization(provider));
}

public sealed class SqliteDocumentStoreHandle(SqliteConnection connection, SqliteDocumentStore store) : IAsyncDisposable
{
    public SqliteConnection Connection { get; } = connection;
    public SqliteDocumentStore Store { get; } = store;

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
