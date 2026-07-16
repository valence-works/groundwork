using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Groundwork.SqlServer.Materialization;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerDocumentStoreFactory
{
    /// <summary>
    /// Opens a physical document store after inspect-only runtime schema admission. Safe pending
    /// operations are applied only when <see cref="GroundworkRuntimeSchemaAdmissionOptions.AutoApplyOnStartup"/>
    /// is enabled.
    /// </summary>
    public static async Task<SqlServerPhysicalDocumentStore> OpenPhysicalAsync(
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
            SqlServerGroundworkCapabilities.PhysicalNames,
            namePolicy);
        var admission = await new SqlServerPhysicalSchemaExecutor(connectionString)
            .InspectRuntimeAdmissionAsync(target, options, schemaAdmissionLog, cancellationToken);
        admission.EnsureReady();
        return new SqlServerPhysicalDocumentStore(
            connectionString,
            manifest,
            target.Routes,
            access,
            scopeObserver);
    }

    public static Task<SqlServerDocumentStore> CreateAsync(
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
            () => new SqlConnection(connectionString),
            () => new SqlConnection(connectionString),
            access,
            scopeObserver,
            cancellationToken);

    internal static async Task<SqlServerDocumentStore> CreateAsync(
        string connectionString,
        StorageManifest manifest,
        ProviderIdentity provider,
        Func<SqlConnection> createMaterializationConnection,
        Func<SqlConnection> createOperationConnection,
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

        var store = new SqlServerDocumentStore(
            RelationalSessionFactory.Concurrent(createOperationConnection),
            manifest,
            access,
            scopeObserver);
        var materializationSessions = RelationalSessionFactory.Concurrent(createMaterializationConnection);
        await materializationSessions.ExecuteAsync(async (connection, ct) =>
        {
            await new SqlServerGroundworkMaterializer((SqlConnection)connection).MaterializeAsync(
                plan,
                ct);
            return true;
        }, cancellationToken);
        return store;
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqlServerGroundworkCapabilities.Runtime(provider), SqlServerGroundworkCapabilities.Materialization(provider));
}
