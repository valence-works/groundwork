using Groundwork.DiagnosticRecords;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Servers;

namespace Groundwork.MongoDb.DiagnosticRecords;

public static class MongoDbDiagnosticRecordStoreFactory
{
    public static async Task<MongoDbDiagnosticRecordStoreHandle> CreateAsync(
        string connectionString,
        string databaseName,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var client = new MongoClient(connectionString);
        try
        {
            var database = client.GetDatabase(databaseName);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            EnsureTransactionCapable(client.Cluster.Description);
            await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, definition, cancellationToken);
            return new(client, new(database, definition, timeProvider));
        }
        catch
        {
            (client as IDisposable)?.Dispose();
            throw;
        }
    }

    internal static void EnsureTransactionCapable(ClusterDescription cluster)
    {
        var transactionCapable = cluster.Type is ClusterType.ReplicaSet or ClusterType.Sharded ||
                                 cluster.Servers.Any(server => server.Type is
                                     ServerType.ReplicaSetPrimary or
                                     ServerType.ReplicaSetSecondary or
                                     ServerType.ShardRouter);
        if (!transactionCapable)
            throw new NotSupportedException(
                $"MongoDB diagnostic records require multi-document transactions; deployment type '{cluster.Type}' is unsupported. Configure a replica set or sharded cluster.");
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
