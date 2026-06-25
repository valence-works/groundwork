using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;
using Groundwork.SqlServer.Materialization;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerDocumentStoreFactory
{
    public static async Task<SqlServerDocumentStoreHandle> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);

        var connection = new SqlConnection(connectionString);
        try
        {
            await new SqlServerGroundworkMaterializer(connection).MaterializeAsync(
                PortableMaterializationPlanFactory.Create(manifest, provider),
                cancellationToken);
            return new SqlServerDocumentStoreHandle(connection, new SqlServerDocumentStore(connection, manifest, ambientTenantId));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

}

public sealed class SqlServerDocumentStoreHandle(SqlConnection connection, SqlServerDocumentStore store) : IAsyncDisposable
{
    public SqlConnection Connection { get; } = connection;
    public SqlServerDocumentStore Store { get; } = store;

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
