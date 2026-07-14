using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
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
            return await CreateAdmittedPhysicalAsync(
                database,
                disposableClient,
                model,
                access,
                scopeObserver,
                validatedOptions,
                cancellationToken);
        }
        catch
        {
            disposableClient?.Dispose();
            throw;
        }
    }

    public static Task<MongoDbPhysicalDocumentStoreHandle> CreatePhysicalAsync(
        IMongoDatabase database,
        StorageManifest manifest,
        ProviderIdentity provider,
        DocumentStoreAccess access,
        IPhysicalNamePolicy? namePolicy = null,
        IStorageScopeObserver? scopeObserver = null,
        MongoDbPhysicalDocumentStoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(access);
        var validatedOptions = options ?? new MongoDbPhysicalDocumentStoreOptions();
        validatedOptions.Validate();
        var model = MongoDbPhysicalStorageModel.Compile(manifest, provider, namePolicy);
        return CreateAdmittedPhysicalAsync(
            database,
            client: null,
            model,
            access,
            scopeObserver,
            validatedOptions,
            cancellationToken);
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

    private static async Task<MongoDbPhysicalDocumentStoreHandle> CreateAdmittedPhysicalAsync(
        IMongoDatabase database,
        IDisposable? client,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        MongoDbPhysicalDocumentStoreOptions options,
        CancellationToken cancellationToken)
    {
        var transactionCapability = MongoDbTransactionCapability.ForDatabase(database);
        var documentKinds = model.Manifest.StorageUnits
            .Select(unit => unit.Identity.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await transactionCapability.EnsureSupportedAsync(
            documentKinds,
            "physical storage",
            cancellationToken);
        var application = await new MongoDbGroundworkMaterializer(database).MaterializeAsync(
            model,
            transactionCapability,
            cancellationToken);
        EnsureAdmitted(model, application);
        return new MongoDbPhysicalDocumentStoreHandle(
            client,
            model,
            new MongoDbPhysicalDocumentStore(
                database,
                model,
                access,
                scopeObserver,
                options,
                TimeProvider.System,
                hooks: null,
                startSessionAsync: null,
                transactionCapability));
    }

    private static void EnsureAdmitted(
        MongoDbPhysicalStorageModel model,
        PhysicalSchemaApplicationResult application)
    {
        if (application.Outcome is not PhysicalSchemaApplicationOutcome.Applied and
            not PhysicalSchemaApplicationOutcome.NoChanges)
        {
            var diagnostics = application.Plan.Diagnostics
                .Concat(application.AuthorizationDiagnostics)
                .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
                .ToArray();
            throw new InvalidOperationException(
                $"MongoDB physical document store admission was {application.Outcome}." +
                (diagnostics.Length == 0
                    ? string.Empty
                    : $"{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"));
        }

        var applied = application.AppliedState ?? throw new InvalidOperationException(
            "MongoDB physical document store admission requires durable applied schema state.");
        if (applied.ManifestIdentity != model.Manifest.Identity ||
            applied.ManifestVersion != model.Manifest.Version ||
            applied.Provider != model.Provider ||
            !string.Equals(applied.TargetFingerprint, model.Target.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MongoDB physical document store admission does not match the compiled manifest/provider target.");
        }

        foreach (var route in model.Routes)
        {
            var snapshot = applied.Snapshot.Routes.SingleOrDefault(candidate =>
                candidate.StorageUnit == route.StorageUnit);
            var identity = snapshot?.IdentitySchemaState;
            var primary = route.Envelope.Identity;
            var expectedPrimary = new DocumentIdentityColumnMapping(
                primary.OriginalId,
                primary.ComparisonKey,
                primary.LookupKey);
            var linked = route.LinkedRelationship?.Identity;
            var expectedLinked = linked is null
                ? null
                : new DocumentIdentityColumnMapping(
                    linked.OriginalId,
                    linked.ComparisonKey,
                    linked.LookupKey);
            if (snapshot is null ||
                !string.Equals(snapshot.RouteFingerprint, route.Fingerprint, StringComparison.Ordinal) ||
                identity is null ||
                identity.StringCasePolicy != primary.StringCasePolicy ||
                !string.Equals(identity.ComparisonAlgorithmId, primary.ComparisonAlgorithmId, StringComparison.Ordinal) ||
                !string.Equals(identity.LookupAlgorithmId, primary.LookupAlgorithmId, StringComparison.Ordinal) ||
                identity.Primary != expectedPrimary ||
                identity.Linked != expectedLinked)
            {
                throw new InvalidOperationException(
                    $"MongoDB physical document store admission has mismatched typed identity state for '{route.StorageUnit.Value}'.");
            }
        }
    }
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
