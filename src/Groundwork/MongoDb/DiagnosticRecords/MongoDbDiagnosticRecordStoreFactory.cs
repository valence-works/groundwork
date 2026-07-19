using Groundwork.DiagnosticRecords;
using MongoDB.Driver;

namespace Groundwork.MongoDb.DiagnosticRecords;

public static class MongoDbDiagnosticRecordStoreFactory
{
    /// <summary>Creates a provider-neutral scope/session factory for a declared deployment.</summary>
    public static IDiagnosticRecordStoreSessionFactory CreateSessionFactory(
        string connectionString,
        string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        return new DelegatingDiagnosticRecordStoreSessionFactory(async (definition, cancellationToken) =>
        {
            var handle = await CreateAsync(connectionString, databaseName, definition, cancellationToken: cancellationToken);
            return new DiagnosticRecordStoreLease(handle.Store, handle);
        });
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
