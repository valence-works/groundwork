using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

internal sealed record RelationalPhysicalMutationRuntimeContext(
    RelationalPhysicalDocumentStore Store,
    StorageManifest Manifest,
    ExecutableStorageRoute Route,
    ProviderIdentity Provider,
    string ExpectedProviderName,
    string HandlerPrefix,
    IReadOnlySet<IndexValueKind>? CanonicalJsonValueKinds = null);

/// <summary>Builds a certified bounded-mutation runtime over the shared relational execution kernel.</summary>
internal static class RelationalPhysicalMutationRuntime
{
    private static readonly JsonSerializerOptions ManifestFingerprintOptions = CreateManifestFingerprintOptions();

    internal static IBoundedDocumentMutationStore Create(
        RelationalPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string expectedProviderName,
        string handlerPrefix,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds = null) =>
        Create(new RelationalPhysicalMutationRuntimeContext(
            store,
            manifest,
            route,
            provider,
            expectedProviderName,
            handlerPrefix,
            canonicalJsonValueKinds));

    internal static IBoundedDocumentMutationStore Create(
        RelationalPhysicalMutationRuntimeContext context,
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept = null) =>
        CreateCore(
            context,
            intercept is null ? null : (point, _, _, _) => intercept(point));

    internal static IBoundedDocumentMutationStore CreateWithInterceptor(
        RelationalPhysicalMutationRuntimeContext context,
        RelationalPhysicalMutationInterceptor intercept) =>
        CreateCore(
            context,
            intercept ?? throw new ArgumentNullException(nameof(intercept)));

    private static IBoundedDocumentMutationStore CreateCore(
        RelationalPhysicalMutationRuntimeContext context,
        RelationalPhysicalMutationInterceptor? intercept)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runtime = BuildRuntime(
            context.Store,
            context.Manifest,
            context.Route,
            context.Provider,
            context.ExpectedProviderName,
            context.HandlerPrefix,
            context.CanonicalJsonValueKinds);
        var handlers = runtime.Capabilities.HandlerIdentities.Select(registration =>
        {
            var certifications = runtime.Compilation.Plans
                .Where(plan => plan.HandlerIdentity == registration.Value)
                .Select(RelationalPhysicalDocumentMutationHandler.Certify)
                .ToArray();
            return (IPhysicalDocumentMutationHandler)new RelationalPhysicalDocumentMutationHandler(
                registration.Value,
                registration.Key,
                context.Store,
                certifications,
                intercept);
        }).ToArray();
        return new PhysicalMutationDocumentStore(
            context.Route,
            runtime.Storage,
            runtime.Capabilities,
            handlers);
    }

    internal static RelationalPhysicalQueryCommand BuildSelectionCommand(
        RelationalPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string expectedProviderName,
        string handlerPrefix,
        DocumentMutation mutation,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var runtime = BuildRuntime(
            store,
            manifest,
            route,
            provider,
            expectedProviderName,
            handlerPrefix,
            canonicalJsonValueKinds);
        var (handler, plan) = ResolveCommandHandler(store, runtime, mutation);
        return handler.BuildSelectionCommand(
            mutation,
            plan,
            store.ResolveMutationScope(mutation.DocumentKind));
    }

    internal static RelationalPhysicalQueryCommand BuildOperationReadCommand(
        RelationalPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string expectedProviderName,
        string handlerPrefix,
        DocumentMutation mutation,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var runtime = BuildRuntime(
            store,
            manifest,
            route,
            provider,
            expectedProviderName,
            handlerPrefix,
            canonicalJsonValueKinds);
        var (handler, plan) = ResolveCommandHandler(store, runtime, mutation);
        return handler.BuildOperationReadCommand(
            mutation,
            plan,
            store.ResolveMutationScope(mutation.DocumentKind));
    }

    private static (RelationalPhysicalDocumentMutationHandler Handler, PhysicalMutationPlan Plan) ResolveCommandHandler(
        RelationalPhysicalDocumentStore store,
        RuntimeComponents runtime,
        DocumentMutation mutation)
    {
        var plan = runtime.Compilation.Plans.Single(candidate =>
            candidate.MutationIdentity == mutation.MutationIdentity);
        var source = runtime.Capabilities.HandlerIdentities.Single(item =>
            item.Value == plan.HandlerIdentity).Key;
        return (
            new RelationalPhysicalDocumentMutationHandler(
                plan.HandlerIdentity,
                source,
                store,
                [RelationalPhysicalDocumentMutationHandler.Certify(plan)]),
            plan);
    }

    private static RuntimeComponents BuildRuntime(
        RelationalPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string expectedProviderName,
        string handlerPrefix,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerPrefix);
        if (!string.Equals(provider.Name, expectedProviderName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Provider '{provider.Name}' cannot use the '{expectedProviderName}' bounded-mutation runtime.",
                nameof(provider));
        }
        if (!CryptographicOperations.FixedTimeEquals(
                ManifestFingerprint(manifest),
                ManifestFingerprint(store.BoundManifest)))
        {
            throw new ArgumentException(
                "The mutation runtime manifest content must exactly match the document store manifest.",
                nameof(manifest));
        }
        if (!store.IsBoundRoute(route))
        {
            throw new ArgumentException(
                "The mutation runtime route fingerprint must match the document store route.",
                nameof(route));
        }
        var storage = store.BoundManifest.StorageUnits
            .Single(candidate => candidate.Identity == route.StorageUnit).PhysicalStorage
            ?? throw new InvalidOperationException(
                $"Storage unit '{route.StorageUnit.Value}' has no physical mutation declarations.");
        var capabilities = RelationalPhysicalQueryRuntime.Capabilities(
            provider,
            handlerPrefix,
            canonicalJsonValueKinds);
        var compilation = PhysicalMutationPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        CertifyTransitionValues(store, route, compilation.Plans);
        return new RuntimeComponents(storage, capabilities, compilation);
    }

    private static void CertifyTransitionValues(
        RelationalPhysicalDocumentStore store,
        ExecutableStorageRoute route,
        IReadOnlyList<PhysicalMutationPlan> plans)
    {
        foreach (var transition in plans.Select(plan => plan.Action).OfType<PhysicalTransitionMutationAction>())
        {
            var projections = route.ProjectedColumns
                .Where(column => string.Equals(column.Definition.Path, transition.Path, StringComparison.Ordinal))
                .ToArray();
            foreach (var value in transition.AllowedSourceValues.Append(transition.TargetValue))
            {
                RelationalPhysicalProjectionValues.ConvertScalar(value, transition.Field.ValueKind);
                store.ConvertMutationJsonValue(value, transition.Field.ValueKind);
                foreach (var projection in projections)
                {
                    store.ConvertPhysicalQueryValue(
                        value,
                        transition.Field.ValueKind,
                        projection.Definition);
                }
            }
        }
    }

    private static byte[] ManifestFingerprint(StorageManifest manifest) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestFingerprintOptions));

    private static JsonSerializerOptions CreateManifestFingerprintOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(PhysicalStoragePolicy))
            {
                typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$policy",
                    DerivedTypes =
                    {
                        new JsonDerivedType(typeof(PhysicalStoragePolicy.DefaultPolicy), "default"),
                        new JsonDerivedType(typeof(PhysicalStoragePolicy.ExplicitPolicy), "explicit")
                    }
                };
            }
            else if (typeInfo.Type == typeof(BoundedMutationAction))
            {
                typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$action",
                    DerivedTypes =
                    {
                        new JsonDerivedType(typeof(BoundedDeleteMutationAction), "delete"),
                        new JsonDerivedType(typeof(BoundedTransitionMutationAction), "transition")
                    }
                };
            }
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    private sealed record RuntimeComponents(
        StorageUnitPhysicalStorage Storage,
        PhysicalQueryPlannerCapabilities Capabilities,
        PhysicalMutationPlanCompilationResult Compilation);
}
