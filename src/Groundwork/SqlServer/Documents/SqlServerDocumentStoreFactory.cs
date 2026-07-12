using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Provider.Relational;
using Groundwork.SqlServer.Materialization;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerDocumentStoreFactory
{
    public static Task<SqlServerDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            () => new SqlConnection(connectionString),
            () => new SqlConnection(connectionString),
            ambientTenantId,
            cancellationToken);

    internal static async Task<SqlServerDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqlConnection> createMaterializationConnection,
        Func<SqlConnection> createOperationConnection,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createMaterializationConnection);
        ArgumentNullException.ThrowIfNull(createOperationConnection);

        var store = new SqlServerDocumentStore(
            RelationalSessionFactory.Concurrent(createOperationConnection),
            manifest,
            ambientTenantId);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new SqlServerGroundworkMaterializer((SqlConnection)connection).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqlServerGroundworkCapabilities.Runtime(provider), SqlServerGroundworkCapabilities.Materialization(provider));
}
