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
        return CreateRuntime(store, manifest, route, provider, intercept).Mutations;
    }

    private static BoundRuntime CreateRuntime(
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
        var handlers = capabilities.HandlerIdentities.Select(registration =>
        {
            var executableBindings = binding.Bindings
                .Where(candidate => candidate.Plan.HandlerIdentity == registration.Value)
                .ToArray();
            return new MongoDbPhysicalDocumentMutationHandler(
                registration.Value,
                registration.Key,
                store,
                route,
                executableBindings,
                capabilities.NativeFieldIdentifiers,
                intercept);
        }).ToArray();
        var mutations = new PhysicalMutationDocumentStore(
            route,
            storage,
            capabilities,
            handlers.Cast<IPhysicalDocumentMutationHandler>().ToArray());
        return new BoundRuntime(
            binding,
            mutations,
            handlers.ToDictionary(handler => handler.Identity, StringComparer.Ordinal));
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
        var runtime = CreateRuntime(store, manifest, route, provider, intercept: null);
        var plan = runtime.Mutations.Admit(mutation);
        var executable = runtime.Binding.Bindings.Single(candidate => candidate.Plan.Equals(plan));
        var handler = runtime.Handlers[plan.HandlerIdentity];
        var invocation = handler.BindInvocation(mutation, plan);
        await store.EnsureMutationSupportedAsync(mutation.DocumentKind, cancellationToken);
        var evidence = new BsonDocument
        {
            ["bindingFingerprint"] = executable.Schema.Fingerprint,
            ["mutationIdentity"] = executable.Plan.MutationIdentity,
            ["action"] = executable.Plan.Action.Kind.ToString(),
            ["primary"] = await ExplainSelectorAsync(
                store,
                executable.Schema.Primary,
                invocation.PrimaryFilter,
                cancellationToken)
        };
        if (executable.Schema.Linked is not null)
        {
            evidence["linked"] = await ExplainSelectorAsync(
                store,
                executable.Schema.Linked,
                invocation.LinkedFilter!,
                cancellationToken);
        }
        else
        {
            evidence["linked"] = BsonNull.Value;
        }
        return evidence;
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
        if (!manifest.HasSameDefinitionAs(store.Manifest))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation manifest '{manifest.Identity.Value}/{manifest.Version.Value}' does not match " +
                $"the whole canonical manifest compiled into the store " +
                $"'{store.Manifest.Identity.Value}/{store.Manifest.Version.Value}'.");
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

        var bindings = store.GetMutationBindings(route.StorageUnit.Value);
        if (bindings.Count != compilation.Plans.Count ||
            compilation.Plans.Any(plan => !bindings.Any(binding => binding.Plan.Equals(plan))))
        {
            throw new InvalidOperationException(
                $"MongoDB mutation bindings for storage unit '{route.StorageUnit.Value}' do not match the compiled mutation plans.");
        }

        return new RuntimeBinding(route, storage, capabilities, bindings);
    }

    private static async Task<BsonDocument> ExplainSelectorAsync(
        MongoDbPhysicalDocumentStore store,
        MongoDbPhysicalMutationSelector selector,
        FilterDefinition<BsonDocument> filter,
        CancellationToken cancellationToken)
    {
        var collection = store.Database.GetCollection<BsonDocument>(selector.StorageObject.Identifier);
        var rendered = filter.Render(new RenderArgs<BsonDocument>(
            collection.DocumentSerializer,
            BsonSerializer.SerializerRegistry));
        var explanation = await store.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = selector.StorageObject.Identifier,
                    ["filter"] = rendered,
                    ["hint"] = selector.Index.Identifier
                },
                ["verbosity"] = "queryPlanner"
            },
            cancellationToken: cancellationToken);
        var winningPlan = MongoDbWinningPlanInspector.ExactWinningPlan(explanation);
        var observation = MongoDbWinningPlanInspector.Inspect(winningPlan);
        var indexes = observation.IndexScans
            .Select(scan => scan.IndexName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (observation.HasCollectionScan ||
            indexes.Length != 1 ||
            indexes[0] != selector.Index.Identifier)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation selector for '{selector.StorageObject.Identifier}' did not use exact index '{selector.Index.Identifier}'.");
        }
        return new BsonDocument
        {
            ["collection"] = selector.StorageObject.Identifier,
            ["indexName"] = selector.Index.Identifier,
            ["filter"] = rendered,
            ["winningPlanIndex"] = indexes[0],
            ["winningPlan"] = winningPlan
        };
    }

    private sealed record RuntimeBinding(
        ExecutableStorageRoute Route,
        StorageUnitPhysicalStorage Storage,
        PhysicalQueryPlannerCapabilities Capabilities,
        IReadOnlyList<MongoDbPhysicalMutationBinding> Bindings);

    private sealed record BoundRuntime(
        RuntimeBinding Binding,
        PhysicalMutationDocumentStore Mutations,
        IReadOnlyDictionary<string, MongoDbPhysicalDocumentMutationHandler> Handlers);
}
