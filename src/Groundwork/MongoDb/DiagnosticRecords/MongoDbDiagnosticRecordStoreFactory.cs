using Groundwork.DiagnosticRecords;
using MongoDB.Driver;

namespace Groundwork.MongoDb.DiagnosticRecords;

public static class MongoDbDiagnosticRecordStoreFactory
{
    public static void ValidateDefinition(DiagnosticRecordStreamDefinition definition) =>
        MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);

    public static async Task ValidateAdmissionAsync(
        string connectionString,
        string databaseName,
        IReadOnlyList<DiagnosticRecordStreamDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
            ValidateDefinition(definition);

        var client = new MongoClient(connectionString);
        try
        {
            var database = client.GetDatabase(databaseName);
            if (!await MongoDbTransactionTopology.SupportsTransactionsAsync(database, cancellationToken))
            {
                throw new NotSupportedException(
                    "MongoDB diagnostic records require a replica set or sharded cluster.");
            }
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Creates a provider-neutral scope/session factory that admits only an already-deployed,
    /// compatible schema. Session opening and store leasing never materialize or repair storage.
    /// </summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(
        string connectionString,
        string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        return new DelegatingDiagnosticRecordStoreSessionFactory(
            new MongoDbDiagnosticRecordDeploymentInspector(connectionString, databaseName),
            (definition, _) =>
            {
                var handle = OpenExisting(connectionString, databaseName, definition);
                return ValueTask.FromResult(new DiagnosticRecordStoreLease(handle.Store, handle));
            });
    }

    /// <summary>
    /// Creates read-only native-plan inspection for already-admitted diagnostic-record storage.
    /// Returned raw plans may contain database metadata and query values; hosts must treat them
    /// as sensitive diagnostic evidence.
    /// </summary>
    public static IDiagnosticRecordPlanInspector CreatePlanInspector(
        string connectionString,
        string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        return new DelegatingDiagnosticRecordPlanInspector(
            new MongoDbDiagnosticRecordDeploymentInspector(connectionString, databaseName),
            (definition, query, cancellationToken) => InspectQueryPlanAsync(connectionString, databaseName, definition, query, cancellationToken),
            (definition, request, cancellationToken) => InspectStatisticsPlanAsync(connectionString, databaseName, definition, request, cancellationToken),
            (definition, request, cancellationToken) => InspectTrimPlanAsync(connectionString, databaseName, definition, request, cancellationToken));
    }

    public static async Task<MongoDbDiagnosticRecordStoreHandle> CreateAsync(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        var client = new MongoClient(connectionString);
        try
        {
            var database = client.GetDatabase(databaseName);
            if (!await MongoDbTransactionTopology.SupportsTransactionsAsync(database, cancellationToken))
            {
                throw new NotSupportedException(
                    $"MongoDB diagnostic records require multi-document transactions; deployment type " +
                    $"'{client.Cluster.Description.Type}' is unsupported. Configure a replica set or sharded cluster.");
            }
            await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, definition, cancellationToken);
            return new(client, new(database, definition, timeProvider));
        }
        catch
        {
            (client as IDisposable)?.Dispose();
            throw;
        }
    }

    private static MongoDbDiagnosticRecordStoreHandle OpenExisting(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition)
    {
        MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        var client = new MongoClient(connectionString);
        try
        {
            return new(client, new(client.GetDatabase(databaseName), definition));
        }
        catch
        {
            (client as IDisposable)?.Dispose();
            throw;
        }
    }

    private static async ValueTask<DiagnosticRecordNativePlan> InspectQueryPlanAsync(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken)
    {
        await using var handle = OpenExisting(connectionString, databaseName, definition);
        var plan = await handle.Store.ExplainQueryAsync(query, cancellationToken);
        return new("mongodb", DiagnosticRecordPlanOperation.Query, DiagnosticRecordNativePlanFormats.MongoDbExplainJson, [plan.ToString()]);
    }

    private static async ValueTask<DiagnosticRecordNativePlan> InspectTrimPlanAsync(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken)
    {
        await using var handle = OpenExisting(connectionString, databaseName, definition);
        var plan = await handle.Store.ExplainTrimAsync(request, cancellationToken);
        return new("mongodb", DiagnosticRecordPlanOperation.TrimSelection, DiagnosticRecordNativePlanFormats.MongoDbExplainJson, [plan.ToString()]);
    }

    private static async ValueTask<DiagnosticRecordNativePlan> InspectStatisticsPlanAsync(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var handle = OpenExisting(connectionString, databaseName, definition);
        var plans = await handle.Store.ExplainStatisticsAsync(request, cancellationToken);
        return new("mongodb", DiagnosticRecordPlanOperation.Statistics, DiagnosticRecordNativePlanFormats.MongoDbExplainJson,
            plans.Select(plan => plan.ToString()).ToArray());
    }

}

public sealed class MongoDbDiagnosticRecordStoreHandle(
    IMongoClient client,
    MongoDbDiagnosticRecordStore store) : IAsyncDisposable
{
    public MongoDbDiagnosticRecordStore Store { get; } = store;

    public ValueTask DisposeAsync()
    {
        (client as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
