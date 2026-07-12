using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Provider.Relational;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public static class SqliteDocumentStoreFactory
{
    public static Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            () => new SqliteConnection(connectionString),
            () => new SqliteConnection(connectionString),
            ambientTenantId,
            cancellationToken);

    internal static Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqliteConnection> createMaterializationConnection,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            createMaterializationConnection,
            () => new SqliteConnection(connectionString),
            ambientTenantId,
            cancellationToken);

    internal static async Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqliteConnection> createMaterializationConnection,
        Func<SqliteConnection> createOperationConnection,
        Func<string?>? ambientTenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createMaterializationConnection);
        ArgumentNullException.ThrowIfNull(createOperationConnection);
        SqliteRelationalSessions.ValidateStatelessConnectionString(connectionString);

        var store = new SqliteDocumentStore(
            RelationalSessionFactory.Serialized(createOperationConnection),
            manifest,
            ambientTenantId);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new SqliteGroundworkMaterializer((SqliteConnection)connection).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqliteGroundworkCapabilities.Runtime(provider), SqliteGroundworkCapabilities.Materialization(provider));
}
