using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Groundwork.Sqlite.Materialization;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public static class SqliteDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<SqlitePhysicalDocumentStore> OpenPhysicalAsync(
        SqliteConnection connection,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var target = await AdmitPhysicalAsync(
            connection,
            manifest,
            provider,
            namePolicy,
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            access,
            scopeObserver);
    }

    /// <summary>
    /// Opens a file-backed physical document store after inspect-only runtime schema admission.
    /// Safe pending operations are applied only when
    /// <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/> is enabled.
    /// </summary>
    public static async Task<SqlitePhysicalDocumentStore> OpenPhysicalAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(access);
        SqliteRelationalSessions.ValidateStatelessConnectionString(connectionString);
        await using var admissionConnection = SqliteConnectionFactory.Create(connectionString);
        var target = await AdmitPhysicalAsync(
            admissionConnection,
            manifest,
            provider,
            namePolicy,
            options,
            schemaAdmissionLog,
            cancellationToken);
        return new SqlitePhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver);
    }

    private static async Task<PhysicalSchemaTarget> AdmitPhysicalAsync(
        SqliteConnection connection,
        StorageManifest manifest,
        ProviderIdentity provider,
        IPhysicalNamePolicy? namePolicy,
        GroundworkRuntimeSchemaAdmissionOptions? options,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? schemaAdmissionLog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            provider,
            SqliteGroundworkCapabilities.PhysicalNames,
            namePolicy);
        var admission = await new SqlitePhysicalSchemaExecutor(connection).InspectRuntimeAdmissionAsync(
            target,
            options,
            schemaAdmissionLog,
            cancellationToken);
        admission.EnsureReady();
        return target;
    }

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
            () => SqliteConnectionFactory.Create(connectionString),
            () => SqliteConnectionFactory.Create(connectionString),
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
            () => SqliteConnectionFactory.Create(connectionString),
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
