using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>Builds the certified MongoDB bounded-mutation runtime for one compiled route.</summary>
public static class MongoDbPhysicalMutationRuntime
{
    public static IBoundedDocumentMutationStore Create(
        MongoDbPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        Create(store, manifest, route, provider, null);

    internal static IBoundedDocumentMutationStore Create(
        MongoDbPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept)
    {
        var binding = Resolve(store, manifest, route, provider);
        route = binding.Route;
        var storage = binding.Storage;
        var capabilities = binding.Capabilities;
        var compilation = binding.Compilation;
        var handlers = capabilities.HandlerIdentities.Select(registration =>
        {
            var certifications = compilation.Plans
                .Where(plan => plan.HandlerIdentity == registration.Value)
                .Select(plan => new PhysicalMutationHandlerCertification(plan))
                .ToArray();
            return (IPhysicalDocumentMutationHandler)new MongoDbPhysicalDocumentMutationHandler(
                registration.Value,
                registration.Key,
                store,
                route,
                storage,
                certifications,
                capabilities.NativeFieldIdentifiers,
                intercept);
        }).ToArray();
        return new PhysicalMutationDocumentStore(route, storage, capabilities, handlers);
    }

    /// <summary>Returns MongoDB query-planner evidence for the exact primary mutation selector.</summary>
    public static async Task<BsonDocument> ExplainAsync(
        MongoDbPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        DocumentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var binding = Resolve(store, manifest, route, provider);
        var plan = binding.Compilation.Plans.Single(candidate =>
            candidate.MutationIdentity == mutation.MutationIdentity);
        var registration = binding.Capabilities.HandlerIdentities.Single(candidate =>
            candidate.Value == plan.HandlerIdentity);
        var handler = new MongoDbPhysicalDocumentMutationHandler(
            registration.Value,
            registration.Key,
            store,
            binding.Route,
            binding.Storage,
            [new PhysicalMutationHandlerCertification(plan)],
            binding.Capabilities.NativeFieldIdentifiers,
            null);
        var collection = store.Database.GetCollection<BsonDocument>(
            binding.Route.PrimaryStorage.Name.Identifier);
        var filter = handler.BuildPrimaryFilter(
            mutation,
            plan,
            store.ResolveMutationScope(mutation.DocumentKind));
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = binding.Route.PrimaryStorage.Name.Identifier,
                ["filter"] = filter.Render(new RenderArgs<BsonDocument>(
                    collection.DocumentSerializer,
                    BsonSerializer.SerializerRegistry))
            },
            ["verbosity"] = "queryPlanner"
        };
        await store.EnsureMutationSupportedAsync(mutation.DocumentKind, cancellationToken);
        return await store.Database.RunCommandAsync<BsonDocument>(
            command,
            cancellationToken: cancellationToken);
    }

    private static RuntimeBinding Resolve(
        MongoDbPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        if (!string.Equals(manifest.Identity.Value, store.Manifest.Identity.Value, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version.Value, store.Manifest.Version.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation manifest '{manifest.Identity.Value}/{manifest.Version.Value}' does not match " +
                $"the compiled store manifest '{store.Manifest.Identity.Value}/{store.Manifest.Version.Value}'.");
        }
        if (!string.Equals(provider.Name, store.Provider.Name, StringComparison.Ordinal) ||
            !string.Equals(provider.Version, store.Provider.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation provider '{provider.Name}/{provider.Version}' does not match " +
                $"the compiled store provider '{store.Provider.Name}/{store.Provider.Version}'.");
        }
        var boundRoute = store.GetRoute(route.StorageUnit.Value);
        var suppliedUnit = manifest.StorageUnits.SingleOrDefault(candidate =>
            candidate.Identity == boundRoute.StorageUnit);
        var boundUnit = store.Manifest.StorageUnits.Single(candidate =>
            candidate.Identity == boundRoute.StorageUnit);
        if (suppliedUnit?.PhysicalStorage is null ||
            !suppliedUnit.PhysicalStorage.Equals(boundUnit.PhysicalStorage))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation declarations for storage unit '{boundRoute.StorageUnit.Value}' do not match " +
                "the canonical manifest compiled into the store.");
        }
        if (!string.Equals(route.DefinitionFingerprint, boundRoute.DefinitionFingerprint, StringComparison.Ordinal) ||
            !string.Equals(route.Fingerprint, boundRoute.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation route '{route.StorageUnit.Value}/{route.Fingerprint}' does not match " +
                $"the compiled store route '{boundRoute.StorageUnit.Value}/{boundRoute.Fingerprint}'.");
        }
        manifest = store.Manifest;
        route = boundRoute;
        var storage = manifest.StorageUnits.Single(candidate => candidate.Identity == route.StorageUnit).PhysicalStorage
            ?? throw new InvalidOperationException(
                $"Storage unit '{route.StorageUnit.Value}' has no physical mutation declarations.");
        var capabilities = store.GetMutationCapabilities(route, storage);
        var compilation = PhysicalMutationPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }

        return new RuntimeBinding(route, storage, capabilities, compilation);
    }

    private sealed record RuntimeBinding(
        ExecutableStorageRoute Route,
        StorageUnitPhysicalStorage Storage,
        PhysicalQueryPlannerCapabilities Capabilities,
        PhysicalMutationPlanCompilationResult Compilation);
}
