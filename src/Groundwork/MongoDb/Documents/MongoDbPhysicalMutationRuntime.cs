using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

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
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        if (!string.Equals(provider.Name, store.Provider.Name, StringComparison.Ordinal) ||
            !string.Equals(provider.Version, store.Provider.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation provider '{provider.Name}/{provider.Version}' does not match " +
                $"the compiled store provider '{store.Provider.Name}/{store.Provider.Version}'.");
        }
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
}
