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
            var executableBindings = binding.Bindings
                .Where(candidate => candidate.Plan.HandlerIdentity == registration.Value)
                .ToArray();
            return (IPhysicalDocumentMutationHandler)new MongoDbPhysicalDocumentMutationHandler(
                registration.Value,
                registration.Key,
                store,
                route,
                executableBindings,
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
        var executable = binding.Bindings.Single(candidate =>
            candidate.Plan.MutationIdentity == mutation.MutationIdentity);
        var plan = executable.Plan;
        var registration = binding.Capabilities.HandlerIdentities.Single(candidate =>
            candidate.Value == plan.HandlerIdentity);
        var handler = new MongoDbPhysicalDocumentMutationHandler(
            registration.Value,
            registration.Key,
            store,
            binding.Route,
            [executable],
            binding.Capabilities.NativeFieldIdentifiers,
            null);
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

        return new RuntimeBinding(route, storage, capabilities, compilation, bindings);
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
        var winningPlan = ExactWinningPlan(explanation);
        var stages = Descendants(winningPlan)
            .Where(document => document.TryGetValue("stage", out _))
            .Select(document => document["stage"].AsString)
            .ToArray();
        var indexes = Descendants(winningPlan)
            .Where(document => document.TryGetValue("indexName", out _))
            .Select(document => document["indexName"].AsString)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (stages.Contains("COLLSCAN", StringComparer.Ordinal) ||
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

    private static BsonDocument ExactWinningPlan(BsonDocument explanation)
    {
        var planners = Descendants(explanation)
            .Where(document => document.TryGetValue("queryPlanner", out var value) && value.IsBsonDocument)
            .Select(document => document["queryPlanner"].AsBsonDocument)
            .ToArray();
        if (planners.Length != 1 ||
            !planners[0].TryGetValue("winningPlan", out var winningPlan) ||
            !winningPlan.IsBsonDocument)
        {
            throw new InvalidOperationException(
                "MongoDB bounded-mutation explain must contain exactly one queryPlanner.winningPlan.");
        }
        return winningPlan.AsBsonDocument;
    }

    private static IEnumerable<BsonDocument> Descendants(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            var document = value.AsBsonDocument;
            yield return document;
            foreach (var child in document.Elements.SelectMany(element => Descendants(element.Value)))
                yield return child;
            yield break;
        }
        if (!value.IsBsonArray)
            yield break;
        foreach (var child in value.AsBsonArray.SelectMany(Descendants))
            yield return child;
    }

    private sealed record RuntimeBinding(
        ExecutableStorageRoute Route,
        StorageUnitPhysicalStorage Storage,
        PhysicalQueryPlannerCapabilities Capabilities,
        PhysicalMutationPlanCompilationResult Compilation,
        IReadOnlyList<MongoDbPhysicalMutationBinding> Bindings);
}
