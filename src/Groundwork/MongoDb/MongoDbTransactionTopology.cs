using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Servers;
using Groundwork.Core.Transactions;

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

/// <summary>
/// Per-store asynchronous transaction-capability gate. Successful and unsupported probe results are
/// cached; cancellation and connectivity failures remain retryable for a later operation.
/// </summary>
internal sealed class MongoDbTransactionCapability
{
    private readonly Func<CancellationToken, Task<bool>> probeAsync;
    private readonly Func<string> deploymentDescription;
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private bool? supported;

    public MongoDbTransactionCapability(
        Func<CancellationToken, Task<bool>> probeAsync,
        Func<string>? deploymentDescription = null,
        bool? knownSupport = null)
    {
        this.probeAsync = probeAsync ?? throw new ArgumentNullException(nameof(probeAsync));
        this.deploymentDescription = deploymentDescription ?? (() => "Unknown");
        supported = knownSupport;
    }

    public bool IsKnownSupported => supported == true;

    public static MongoDbTransactionCapability ForDatabase(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        bool? knownSupport = MongoDbTransactionTopology.IsKnownTransactionCapable(database.Client.Cluster.Description)
            ? true
            : null;
        return new(
            ct => MongoDbTransactionTopology.SupportsTransactionsAsync(database, ct),
            () => database.Client.Cluster.Description.Type.ToString(),
            knownSupport);
    }

    public async Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken)
    {
        if (supported is { } cached)
            return cached;

        await probeGate.WaitAsync(cancellationToken);
        try
        {
            if (supported is { } current)
                return current;

            var result = await probeAsync(cancellationToken);
            supported = result;
            return result;
        }
        finally
        {
            probeGate.Release();
        }
    }

    public async Task EnsureSupportedAsync(
        IReadOnlyList<string> documentKinds,
        string operation,
        CancellationToken cancellationToken)
    {
        if (await SupportsTransactionsAsync(cancellationToken))
            return;

        throw new UnsupportedAtomicCommitException(
            documentKinds,
            $"MongoDB {operation} requires a replica set or sharded cluster, but the connected deployment is " +
            $"'{deploymentDescription()}'.");
    }
}
