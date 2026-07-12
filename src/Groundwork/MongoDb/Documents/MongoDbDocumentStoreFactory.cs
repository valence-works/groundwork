using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.MongoDb.Materialization;
using Groundwork.Documents.Scoping;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

public static class MongoDbDocumentStoreFactory
{
    public static async Task<MongoDbDocumentStoreHandle> CreateAsync(
        string connectionString,
        string databaseName,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(access);

        var client = new MongoClient(connectionString);
        var disposableClient = (object)client as IDisposable;
        try
        {
            var database = client.GetDatabase(databaseName);
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(
                CreateMaterializationPlan(manifest, provider),
                cancellationToken);
            return new MongoDbDocumentStoreHandle(disposableClient, new MongoDbDocumentStore(database, manifest, access, scopeObserver));
        }
        catch
        {
            disposableClient?.Dispose();
            throw;
        }
    }

    private static MaterializationPlan CreateMaterializationPlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, MongoDbGroundworkCapabilities.Runtime(provider), MongoDbGroundworkCapabilities.Materialization(provider));
}

public sealed class MongoDbDocumentStoreHandle(IDisposable? client, MongoDbDocumentStore store) : IAsyncDisposable
{
    public MongoDbDocumentStore Store { get; } = store;

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
