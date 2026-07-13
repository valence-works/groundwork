using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Servers;

namespace Groundwork.MongoDb;

/// <summary>Single MongoDB topology probe for every transaction-backed Groundwork adapter.</summary>
internal static class MongoDbTransactionTopology
{
    public static bool IsKnownTransactionCapable(ClusterDescription cluster) =>
        cluster.Type is ClusterType.ReplicaSet or ClusterType.Sharded ||
        cluster.Servers.Any(server => server.Type is
            ServerType.ReplicaSetPrimary or
            ServerType.ReplicaSetSecondary or
            ServerType.ShardRouter);

    public static bool IsHelloTransactionCapable(BsonDocument hello) =>
        hello.Contains("setName") || hello.GetValue("msg", BsonNull.Value) == "isdbgrid";

    public static async Task<bool> SupportsTransactionsAsync(
        IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (IsKnownTransactionCapable(database.Client.Cluster.Description))
            return true;

        var hello = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument("hello", 1),
            cancellationToken: cancellationToken);
        return IsHelloTransactionCapable(hello);
    }
}
