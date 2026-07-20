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
        RelationalPhysicalMutationRuntimeContext context,
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept = null,
        RelationalPhysicalQueryExplainExecutor? explain = null) =>
        CreateCore(
            context,
            intercept is null ? null : (point, _, _, _) => intercept(point),
            explain);

    internal static IBoundedDocumentMutationStore CreateWithInterceptor(
        RelationalPhysicalMutationRuntimeContext context,
        RelationalPhysicalMutationInterceptor intercept) =>
        CreateCore(
            context,
            intercept ?? throw new ArgumentNullException(nameof(intercept)),
            explain: null);

    private static IBoundedDocumentMutationStore CreateCore(
        RelationalPhysicalMutationRuntimeContext context,
        RelationalPhysicalMutationInterceptor? intercept,
        RelationalPhysicalQueryExplainExecutor? explain)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runtime = BuildRuntime(context);
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
            handlers,
            explain is null ? null : (mutation, plan, cancellationToken) =>
                ExplainAsync(context, mutation, plan, explain, cancellationToken));
    }

    internal static RelationalPhysicalQueryCommand BuildSelectionCommand(
        RelationalPhysicalMutationRuntimeContext context,
        DocumentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutation);
        var runtime = BuildRuntime(context);
        var (handler, plan) = ResolveCommandHandler(context.Store, runtime, mutation);
        return handler.BuildSelectionCommand(
            mutation,
            plan,
            context.Store.ResolveMutationScope(mutation.DocumentKind));
    }

    internal static RelationalPhysicalQueryCommand BuildOperationReadCommand(
        RelationalPhysicalMutationRuntimeContext context,
        DocumentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutation);
        var runtime = BuildRuntime(context);
        var (handler, plan) = ResolveCommandHandler(context.Store, runtime, mutation);
        return handler.BuildOperationReadCommand(
            mutation,
            plan,
            context.Store.ResolveMutationScope(mutation.DocumentKind));
    }

    private static Task<PhysicalDocumentMutationExplanation> ExplainAsync(
        RelationalPhysicalMutationRuntimeContext context,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        RelationalPhysicalQueryExplainExecutor explain,
        CancellationToken cancellationToken)
    {
        var runtime = BuildRuntime(context);
        var (handler, admitted) = ResolveCommandHandler(context.Store, runtime, mutation);
        if (!admitted.Equals(plan))
            throw new InvalidOperationException("Provider-native mutation explanation did not resolve the admitted plan.");
        var scope = context.Store.ResolveMutationScope(mutation.DocumentKind);
        if (scope.AcrossScopes || scope.StorageKey is null)
            throw new InvalidOperationException("Bounded mutations require one route-derived target scope.");
        var selection = handler.BuildSelectionCommand(mutation, plan, scope);
        return context.Store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, selection.CommandText);
            foreach (var (name, value) in selection.Parameters)
                context.Store.AddPhysicalParameter(command, name, value);
            var native = await explain(command, ct);
            NativeMutationPlanEvidence.Validate(plan, native.Content, selection.CommandText);
            return new PhysicalDocumentMutationExplanation(
                plan,
                BoundedMutationRequestFingerprint.Create(mutation, plan, scope.StorageKey),
                native.Format,
                native.Content,
                [new PhysicalDocumentMutationSelectorEvidence(
                    Target(plan),
                    plan.Predicate.LookupObject,
                    plan.Predicate.IndexName!,
                    plan.Predicate.IndexName!.Identifier)],
                selection.CommandText);
        }, cancellationToken);
    }

    private static ExecutableStorageObjectRole Target(PhysicalMutationPlan plan) =>
        plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? ExecutableStorageObjectRole.LinkedIndexStorage
            : ExecutableStorageObjectRole.PrimaryStorage;

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

    private static RuntimeComponents BuildRuntime(RelationalPhysicalMutationRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Store);
        ArgumentNullException.ThrowIfNull(context.Manifest);
        ArgumentNullException.ThrowIfNull(context.Route);
        ArgumentNullException.ThrowIfNull(context.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ExpectedProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.HandlerPrefix);
        if (!string.Equals(context.Provider.Name, context.ExpectedProviderName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Provider '{context.Provider.Name}' cannot use the '{context.ExpectedProviderName}' bounded-mutation runtime.",
                nameof(context));
        }
        if (!CryptographicOperations.FixedTimeEquals(
                ManifestFingerprint(context.Manifest),
                ManifestFingerprint(context.Store.BoundManifest)))
        {
            throw new ArgumentException(
                "The mutation runtime manifest content must exactly match the document store manifest.",
                nameof(context));
        }
        if (!context.Store.IsBoundRoute(context.Route))
        {
            throw new ArgumentException(
                "The mutation runtime route fingerprint must match the document store route.",
                nameof(context));
        }
        var storage = context.Store.BoundManifest.StorageUnits
            .Single(candidate => candidate.Identity == context.Route.StorageUnit).PhysicalStorage
            ?? throw new InvalidOperationException(
                $"Storage unit '{context.Route.StorageUnit.Value}' has no physical mutation declarations.");
        var capabilities = RelationalPhysicalQueryRuntime.Capabilities(
            context.Provider,
            context.HandlerPrefix,
            context.CanonicalJsonValueKinds);
        var compilation = PhysicalMutationPlanCompiler.Compile(context.Route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        CertifyTransitionValues(context.Store, context.Route, compilation.Plans);
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

internal static class NativeMutationPlanEvidence
{
    internal static void Validate(PhysicalMutationPlan plan, string nativePlan, string renderedCommand)
    {
        if (string.IsNullOrWhiteSpace(nativePlan) || plan.Predicate.IndexName is null ||
            !nativePlan.Contains(plan.Predicate.IndexName.Identifier, StringComparison.Ordinal) ||
            !renderedCommand.Contains(plan.Predicate.LookupObject.Identifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider-native mutation plan for '{plan.MutationIdentity}' did not use declared target " +
                $"'{plan.Predicate.LookupObject.Identifier}' and index '{plan.Predicate.IndexName?.Identifier ?? "<none>"}'.");
        }
    }
}
