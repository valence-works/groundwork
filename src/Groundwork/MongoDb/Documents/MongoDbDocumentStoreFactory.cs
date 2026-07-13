using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Validation;
using Groundwork.Documents.Scoping;
using Groundwork.Materialization;
using Groundwork.MongoDb.Materialization;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

public static class MongoDbDocumentStoreFactory
{
    public static async Task<MongoDbPhysicalDocumentStoreHandle> CreatePhysicalAsync(
        string connectionString,
        string databaseName,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        MongoDbPhysicalDocumentStoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(access);
        var validatedOptions = options ?? new MongoDbPhysicalDocumentStoreOptions();
        validatedOptions.Validate();
        var model = MongoDbPhysicalStorageModel.Compile(manifest, provider, namePolicy);

        var client = new MongoClient(connectionString);
        var disposableClient = (object)client as IDisposable;
        try
        {
            var database = client.GetDatabase(databaseName);
            var transactionCapability = MongoDbTransactionCapability.ForDatabase(database);
            var documentKinds = manifest.StorageUnits
                .Select(unit => unit.Identity.Value)
                .Order(StringComparer.Ordinal)
                .ToArray();
            await transactionCapability.EnsureSupportedAsync(
                documentKinds,
                "physical storage",
                cancellationToken);
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(
                model,
                transactionCapability,
                cancellationToken);
            return new MongoDbPhysicalDocumentStoreHandle(
                disposableClient,
                model,
                new MongoDbPhysicalDocumentStore(
                    database,
                    model,
                    access,
                    scopeObserver,
                    validatedOptions,
                    TimeProvider.System,
                    hooks: null,
                    startSessionAsync: null,
                    transactionCapability));
        }
        catch
        {
            disposableClient?.Dispose();
            throw;
        }
    }

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

public sealed class MongoDbPhysicalDocumentStoreHandle(
    IDisposable? client,
    MongoDbPhysicalStorageModel model,
    MongoDbPhysicalDocumentStore store) : IAsyncDisposable
{
    public MongoDbPhysicalStorageModel Model { get; } = model;

    public MongoDbPhysicalDocumentStore Store { get; } = store;

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        return ValueTask.CompletedTask;
    }
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
