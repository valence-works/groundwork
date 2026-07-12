using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.PostgreSql.Materialization;
using Groundwork.Provider.Relational;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public static class PostgreSqlDocumentStoreFactory
{
    public static Task<PostgreSqlDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            () => new NpgsqlConnection(connectionString),
            () => new NpgsqlConnection(connectionString),
            ambientTenantId,
            cancellationToken);

    internal static async Task<PostgreSqlDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<NpgsqlConnection> createMaterializationConnection,
        Func<NpgsqlConnection> createOperationConnection,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createMaterializationConnection);
        ArgumentNullException.ThrowIfNull(createOperationConnection);

        var store = new PostgreSqlDocumentStore(
            RelationalSessionFactory.Concurrent(createOperationConnection),
            manifest,
            ambientTenantId);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new PostgreSqlGroundworkMaterializer((NpgsqlConnection)connection).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, PostgreSqlGroundworkCapabilities.Runtime(provider), PostgreSqlGroundworkCapabilities.Materialization(provider));
}
