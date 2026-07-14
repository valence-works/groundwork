using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public static class SqliteDocumentStoreFactory
{
    public static async Task<SqliteDocumentStore> CreateAsync(
        SqliteConnection connection,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(access);

        var plan = CreateMaterializationPlan(manifest, provider).RequirePlannable();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(plan, cancellationToken);
        return new SqliteDocumentStore(connection, manifest, access, scopeObserver);
    }

    public static Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            () => new SqliteConnection(connectionString),
            () => new SqliteConnection(connectionString),
            access,
            scopeObserver,
            cancellationToken);

    internal static Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqliteConnection> createMaterializationConnection,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            connectionString,
            manifest,
            provider,
            createMaterializationConnection,
            () => new SqliteConnection(connectionString),
            access,
            scopeObserver,
            cancellationToken);

    internal static async Task<SqliteDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqliteConnection> createMaterializationConnection,
        Func<SqliteConnection> createOperationConnection,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createMaterializationConnection);
        ArgumentNullException.ThrowIfNull(createOperationConnection);
        SqliteRelationalSessions.ValidateStatelessConnectionString(connectionString);

        var plan = CreateMaterializationPlan(manifest, provider).RequirePlannable();

        var store = new SqliteDocumentStore(
            RelationalSessionFactory.Serialized(createOperationConnection),
            manifest,
            access,
            scopeObserver);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new SqliteGroundworkMaterializer((SqliteConnection)connection).MaterializeAsync(
                plan,
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqliteGroundworkCapabilities.Runtime(provider), SqliteGroundworkCapabilities.Materialization(provider));
}
