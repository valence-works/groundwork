using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.PostgreSql.Materialization;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public static class PostgreSqlDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<PostgreSqlPhysicalDocumentStore> OpenPhysicalAsync(
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
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(access);
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            namePolicy);
        var admission = await new PostgreSqlPhysicalSchemaExecutor(connectionString)
            .InspectRuntimeAdmissionAsync(target, options, schemaAdmissionLog, cancellationToken);
        admission.EnsureReady();
        return new PostgreSqlPhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver);
    }

    public static Task<PostgreSqlDocumentStore> CreateAsync(
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
            () => new NpgsqlConnection(connectionString),
            () => new NpgsqlConnection(connectionString),
            access,
            scopeObserver,
            cancellationToken);

    internal static async Task<PostgreSqlDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<NpgsqlConnection> createMaterializationConnection,
        Func<NpgsqlConnection> createOperationConnection,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createMaterializationConnection);
        ArgumentNullException.ThrowIfNull(createOperationConnection);

        var plan = CreateMaterializationPlan(manifest, provider).RequirePlannable();

        var store = new PostgreSqlDocumentStore(
            RelationalSessionFactory.Concurrent(createOperationConnection),
            manifest,
            access,
            scopeObserver);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new PostgreSqlGroundworkMaterializer((NpgsqlConnection)connection).MaterializeAsync(
                plan,
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, PostgreSqlGroundworkCapabilities.Runtime(provider), PostgreSqlGroundworkCapabilities.Materialization(provider));
}
