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
    /// <summary>
    /// Opens a physical document store only when the compiled manifest/provider target has already
    /// been applied exactly. This operation inspects schema state but never applies or mutates it.
    /// The returned handle owns the client created from <paramref name="connectionString"/>.
    /// </summary>
    public static async Task<MongoDbPhysicalDocumentStoreOpenHandle> OpenPhysicalAsync(
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
            return await OpenAdmittedPhysicalAsync(
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

    /// <summary>
    /// Opens a physical document store only when the compiled manifest/provider target has already
    /// been applied exactly. This operation inspects schema state but never applies or mutates it.
    /// The caller retains ownership of <paramref name="database"/> and its client.
    /// </summary>
    public static Task<MongoDbPhysicalDocumentStoreOpenHandle> OpenPhysicalAsync(
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
        return OpenAdmittedPhysicalAsync(
            database,
            client: null,
            model,
            access,
            scopeObserver,
            validatedOptions,
            cancellationToken);
    }

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
            var transactionCapability = MongoDbTransactionCapability.ForDatabase(database);
            await transactionCapability.EnsureSupportedAsync(
                DocumentKinds(manifest),
                "document storage",
                cancellationToken);
            var runtimeCapabilities = MongoDbGroundworkCapabilities.RuntimeForTransactionCapableDeployment(provider);

            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(
                CreateMaterializationPlan(manifest, runtimeCapabilities),
                cancellationToken);
            return new MongoDbDocumentStoreHandle(
                disposableClient,
                new MongoDbDocumentStore(
                    database,
                    manifest,
                    access,
                    scopeObserver,
                    transactionCapability.SupportsTransactionsAsync,
                    startSessionAsync: null,
                    isTransactionSupportKnown: () => transactionCapability.IsKnownSupported));
        }
        catch
        {
            disposableClient?.Dispose();
            throw;
        }
    }

    private static MaterializationPlan CreateMaterializationPlan(
        StorageManifest manifest,
        ProviderCapabilityReport runtimeCapabilities) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(
                manifest,
                runtimeCapabilities,
                MongoDbGroundworkCapabilities.Materialization(runtimeCapabilities.Provider));

    private static string[] DocumentKinds(StorageManifest manifest) =>
        manifest.StorageUnits
            .Select(unit => unit.Identity.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<MongoDbPhysicalDocumentStoreOpenHandle> OpenAdmittedPhysicalAsync(
        IMongoDatabase database,
        IDisposable? client,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        MongoDbPhysicalDocumentStoreOptions options,
        CancellationToken cancellationToken)
    {
        var transactionCapability = MongoDbTransactionCapability.ForDatabase(database);
        await transactionCapability.EnsureSupportedAsync(
            DocumentKinds(model),
            "physical storage",
            cancellationToken);
        var inspection = await new MongoDbPhysicalSchemaExecutor(database).InspectHistoryAsync(
            model.Target,
            cancellationToken);
        EnsureOpenAdmitted(model, inspection);
        return new MongoDbPhysicalDocumentStoreOpenHandle(
            client,
            model,
            inspection,
            CreatePhysicalStore(
                database,
                model,
                access,
                scopeObserver,
                options,
                transactionCapability));
    }

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
        await transactionCapability.EnsureSupportedAsync(
            DocumentKinds(model),
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
            application,
            CreatePhysicalStore(
                database,
                model,
                access,
                scopeObserver,
                options,
                transactionCapability));
    }

    private static string[] DocumentKinds(MongoDbPhysicalStorageModel model) =>
        model.Manifest.StorageUnits
            .Select(unit => unit.Identity.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static MongoDbPhysicalDocumentStore CreatePhysicalStore(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        MongoDbPhysicalDocumentStoreOptions options,
        MongoDbTransactionCapability transactionCapability) =>
        new(
            database,
            model,
            access,
            scopeObserver,
            options,
            TimeProvider.System,
            hooks: null,
            startSessionAsync: null,
            transactionCapability);

    private static void EnsureOpenAdmitted(
        MongoDbPhysicalStorageModel model,
        PhysicalSchemaInspectionResult inspection)
    {
        if (!inspection.IsAppliedSchemaValid)
        {
            throw new InvalidOperationException(
                "MongoDB physical document store admission found drift in the applied schema.");
        }

        var plan = PhysicalSchemaDiffPlanner.Plan(
            model.Target,
            inspection.History,
            DateTimeOffset.UnixEpoch);
        if (!plan.IsApplicable || plan.Operations.Count != 0)
        {
            throw new InvalidOperationException(
                "MongoDB physical document store admission requires the exact target to be applied before the store is opened.");
        }

        EnsureExactAppliedState(
            model,
            inspection.History.AppliedState ?? throw new InvalidOperationException(
                "MongoDB physical document store admission requires durable applied schema state."));
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
        EnsureExactAppliedState(model, applied);
    }

    private static void EnsureExactAppliedState(
        MongoDbPhysicalStorageModel model,
        PhysicalSchemaAppliedState applied)
    {
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
            if (snapshot is null ||
                !string.Equals(snapshot.RouteFingerprint, route.Fingerprint, StringComparison.Ordinal) ||
                identity is null ||
                !identity.Matches(route))
            {
                throw new InvalidOperationException(
                    $"MongoDB physical document store admission has mismatched typed identity state for '{route.StorageUnit.Value}'.");
            }
        }
    }
}

/// <summary>
/// Owns a physical document store opened from an exactly applied MongoDB schema target and exposes
/// the point-in-time inspection evidence used for admission.
/// </summary>
public sealed class MongoDbPhysicalDocumentStoreOpenHandle(
    IDisposable? client,
    MongoDbPhysicalStorageModel model,
    PhysicalSchemaInspectionResult schemaInspection,
    MongoDbPhysicalDocumentStore store) : IAsyncDisposable
{
    private readonly MongoDbClientLease clientLease = new(client);
    private readonly object gate = new();
    private TaskCompletionSource? bindingDrain;
    private TaskCompletionSource? disposal;
    private int activeBindings;
    private bool disposed;

    public MongoDbPhysicalStorageModel Model { get; } = model;

    public PhysicalSchemaInspectionResult SchemaInspection { get; } = schemaInspection;

    public MongoDbPhysicalDocumentStore Store { get; } = store;

    /// <summary>
    /// Creates an immutable access-bound store over this already-admitted MongoDB runtime. This
    /// operation performs no topology probe, physical-model compilation, or schema inspection.
    /// The handle must outlive every store it creates.
    /// </summary>
    public MongoDbPhysicalDocumentStore CreateStore(
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        lock (gate)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(MongoDbPhysicalDocumentStoreOpenHandle));
            activeBindings++;
        }

        try
        {
            return Store.WithAccess(access, scopeObserver);
        }
        finally
        {
            lock (gate)
            {
                activeBindings--;
                if (activeBindings == 0)
                    bindingDrain?.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task drain;
        lock (gate)
        {
            if (disposal is not null)
                return new(disposal.Task);

            disposed = true;
            completion = disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = activeBindings == 0
                ? Task.CompletedTask
                : (bindingDrain = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        _ = CompleteDisposalAsync(drain, clientLease, completion);
        return new(completion.Task);
    }

    private static async Task CompleteDisposalAsync(
        Task bindingDrain,
        MongoDbClientLease clientLease,
        TaskCompletionSource completion)
    {
        try
        {
            await bindingDrain;
            await clientLease.DisposeAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}

public sealed class MongoDbPhysicalDocumentStoreHandle(
    IDisposable? client,
    MongoDbPhysicalStorageModel model,
    PhysicalSchemaApplicationResult schemaApplication,
    MongoDbPhysicalDocumentStore store) : IAsyncDisposable
{
    private readonly MongoDbClientLease clientLease = new(client);

    public MongoDbPhysicalStorageModel Model { get; } = model;

    public PhysicalSchemaApplicationResult SchemaApplication { get; } = schemaApplication;

    public MongoDbPhysicalDocumentStore Store { get; } = store;

    public ValueTask DisposeAsync() => clientLease.DisposeAsync();
}

public sealed class MongoDbDocumentStoreHandle(IDisposable? client, MongoDbDocumentStore store) : IAsyncDisposable
{
    private readonly MongoDbClientLease clientLease = new(client);

    public MongoDbDocumentStore Store { get; } = store;

    public ValueTask DisposeAsync() => clientLease.DisposeAsync();
}

internal sealed class MongoDbClientLease(IDisposable? client) : IAsyncDisposable
{
    private IDisposable? disposableClient = client;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposableClient, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
