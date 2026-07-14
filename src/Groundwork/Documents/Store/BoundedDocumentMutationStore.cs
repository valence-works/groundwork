using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>A runtime invocation of one named mutation. Its effect is never caller supplied.</summary>
public sealed class DocumentMutation
{
    public DocumentMutation(
        string documentKind,
        string mutationIdentity,
        string operationId,
        IReadOnlyList<DocumentQueryClause>? clauses = null)
    {
        DocumentKind = string.IsNullOrWhiteSpace(documentKind)
            ? throw new ArgumentException("Document kind must be provided.", nameof(documentKind))
            : documentKind;
        MutationIdentity = string.IsNullOrWhiteSpace(mutationIdentity)
            ? throw new ArgumentException("Bounded mutation identity must be provided.", nameof(mutationIdentity))
            : mutationIdentity;
        OperationId = string.IsNullOrWhiteSpace(operationId)
            ? throw new ArgumentException("Mutation operation identity must be provided.", nameof(operationId))
            : operationId;
        Clauses = Array.AsReadOnly((clauses ?? []).ToArray());
    }

    public string DocumentKind { get; }

    public string MutationIdentity { get; }

    public string OperationId { get; }

    public IReadOnlyList<DocumentQueryClause> Clauses { get; }
}

public enum BoundedMutationStatus
{
    Completed,
    Replayed
}

/// <summary>The exact durable outcome of one bounded mutation operation.</summary>
public sealed record BoundedMutationResult
{
    public BoundedMutationResult(BoundedMutationStatus status, long affectedCount)
    {
        if (affectedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(affectedCount), affectedCount, "Affected count cannot be negative.");
        Status = status;
        AffectedCount = affectedCount;
    }

    public BoundedMutationStatus Status { get; }

    public long AffectedCount { get; }

    public static BoundedMutationResult Completed(long affectedCount) =>
        new(BoundedMutationStatus.Completed, affectedCount);

    public static BoundedMutationResult Replayed(long affectedCount) =>
        new(BoundedMutationStatus.Replayed, affectedCount);
}

public interface IBoundedDocumentMutationStore
{
    Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when one durable operation identity is reused for a different bounded mutation request.
/// Reusing the identity for the same canonical request is an idempotent replay instead.
/// </summary>
public sealed class BoundedMutationOperationConflictException : InvalidOperationException
{
    public BoundedMutationOperationConflictException(
        string operationId,
        string requestedFingerprint,
        string durableFingerprint)
        : base($"Bounded mutation operation '{operationId}' was already used for a different request.")
    {
        OperationId = operationId;
        RequestedFingerprint = requestedFingerprint;
        DurableFingerprint = durableFingerprint;
    }

    public string OperationId { get; }

    public string RequestedFingerprint { get; }

    public string DurableFingerprint { get; }
}

/// <summary>
/// Canonical provider-neutral request identity used by durable mutation ledgers. AND-clause,
/// OR-comparison, and IN-value ordering do not change the fingerprint.
/// </summary>
public static class BoundedMutationRequestFingerprint
{
    public static string Create(DocumentMutation mutation, PhysicalMutationPlan plan, string storageScope)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageScope);
        var action = plan.Action switch
        {
            PhysicalDeleteMutationAction => "delete",
            PhysicalTransitionMutationAction transition => string.Join(
                "\u001f",
                "transition",
                Encode(transition.Path),
                Encode(transition.TargetValue),
                string.Join("\u001e", transition.AllowedSourceValues.Order(StringComparer.Ordinal).Select(Encode))),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Action.Kind, null)
        };
        var clauses = mutation.Clauses
            .Select(clause => clause.Comparisons
                .Select(comparison => CanonicalComparison(comparison, plan.Predicate))
                .Order(StringComparer.Ordinal)
                .ToArray())
            .Select(comparisons => string.Join("\u001d", comparisons))
            .Order(StringComparer.Ordinal);
        var canonical = string.Join(
            "\u001c",
            CanonicalPlan(plan),
            Encode(mutation.DocumentKind),
            Encode(mutation.MutationIdentity),
            Encode(storageScope),
            action,
            string.Join("\u001b", clauses));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string CanonicalPlan(PhysicalMutationPlan plan)
    {
        var predicate = plan.Predicate;
        var predicates = predicate.Predicates
            .Select(item => string.Join(
                "\u0018",
                Encode(item.Path),
                string.Join("\u0017", item.Operations.Order().Select(operation => ((int)operation).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)))))
            .Order(StringComparer.Ordinal);
        return string.Join(
            "\u0016",
            Encode(plan.RouteFingerprint),
            Encode(predicate.StorageUnit.Value),
            Encode(predicate.QueryIdentity),
            Encode(predicate.LogicalIndexIdentity),
            string.Join("\u0015", predicate.LogicalIndexPaths.Select(Encode)),
            ((int)predicate.Form).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)predicate.AccessKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)predicate.Scope.Policy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            predicate.Scope.IsMandatory ? "1" : "0",
            predicate.Scope.UsesGlobalSentinel ? "1" : "0",
            CanonicalIdentityPlan(predicate.DocumentIdentity),
            string.Join("\u0014", predicates),
            string.Join("\u0013", predicate.RequiredEqualityPrefixPaths.Select(Encode)),
            predicate.SupportsDisjunction ? "1" : "0",
            predicate.IsScaleBearing ? "1" : "0");
    }

    private static string CanonicalIdentityPlan(PhysicalQueryDocumentIdentityBinding identity) =>
        string.Join(
            "\u0011",
            ((int)identity.StringCasePolicy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Encode(identity.ComparisonAlgorithmId),
            Encode(identity.LookupAlgorithmId),
            CanonicalIdentityField(identity.Original),
            CanonicalIdentityField(identity.Comparison),
            CanonicalIdentityField(identity.Lookup));

    private static string CanonicalIdentityField(PhysicalQueryField field) =>
        string.Join(
            "\u0010",
            Encode(field.Path),
            Encode(field.Identifier),
            ((int)field.Source).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)field.Target).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Encode(field.ObjectName.Identifier));

    private static string CanonicalComparison(
        DocumentQueryComparison comparison,
        PhysicalQueryPlan plan)
    {
        IEnumerable<string?> values = comparison.Path == PhysicalDocumentFieldPaths.Id
            ? PhysicalDocumentIdentityQuery.Bind(plan, comparison).Values
                .Select(value => (string?)CanonicalIdentityValue(value))
            : comparison.Values;
        if (comparison.Operator == QueryComparisonOperator.In)
            values = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return string.Join(
            "\u001a",
            Encode(comparison.Path),
            ((int)comparison.Operator).ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("\u0019", values.Select(Encode)));
    }

    private static string CanonicalIdentityValue(PhysicalQueryIdentityValue value) =>
        string.Join(
            "\u0012",
            ((int)value.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Encode(value.ComparisonKey),
            Encode(value.LookupKey));

    private static string Encode(string? value) => value is null ? "-" : $"{value.Length}:{value}";
}

/// <summary>Immutable evidence that a mutation handler serves one exact compiled mutation plan.</summary>
public sealed class PhysicalMutationSelectorCertification
{
    public PhysicalMutationSelectorCertification(
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName storageObject,
        ProviderPhysicalObjectName index,
        IReadOnlyDictionary<string, string> fieldIdentifiers)
    {
        Target = target;
        StorageObject = storageObject ?? throw new ArgumentNullException(nameof(storageObject));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        FieldIdentifiers = (fieldIdentifiers ?? throw new ArgumentNullException(nameof(fieldIdentifiers)))
            .ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ExecutableStorageObjectRole Target { get; }

    public ProviderPhysicalObjectName StorageObject { get; }

    public ProviderPhysicalObjectName Index { get; }

    public IReadOnlyDictionary<string, string> FieldIdentifiers { get; }
}

/// <summary>
/// Provider-owned evidence for the immutable physical selectors actually consumed by one bounded
/// mutation. It supplements the provider-neutral predicate plan without rewriting its semantics.
/// </summary>
public sealed class PhysicalMutationExecutionCertification
{
    public PhysicalMutationExecutionCertification(
        PhysicalMutationPlan plan,
        PhysicalMutationSelectorCertification primary,
        PhysicalMutationSelectorCertification? linked,
        string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Primary = primary ?? throw new ArgumentNullException(nameof(primary));
        Linked = linked;
        Fingerprint = string.IsNullOrWhiteSpace(fingerprint)
            ? throw new ArgumentException("An executable mutation binding fingerprint is required.", nameof(fingerprint))
            : fingerprint;
        Provider = plan.Predicate.Provider;
        StorageUnit = plan.Predicate.StorageUnit;
        MutationIdentity = plan.MutationIdentity;
        RouteFingerprint = plan.RouteFingerprint;
        ActionKind = plan.Action.Kind;
        var transition = plan.Action as PhysicalTransitionMutationAction;
        TransitionPath = transition?.Path;
        TransitionTarget = transition?.TargetValue;
        TransitionSources = Array.AsReadOnly(transition?.AllowedSourceValues.ToArray() ?? []);
    }

    public ProviderIdentity Provider { get; }

    public StorageUnitIdentity StorageUnit { get; }

    public string MutationIdentity { get; }

    public string RouteFingerprint { get; }

    public BoundedMutationActionKind ActionKind { get; }

    public string? TransitionPath { get; }

    public string? TransitionTarget { get; }

    public IReadOnlyList<string> TransitionSources { get; }

    public PhysicalMutationSelectorCertification Primary { get; }

    public PhysicalMutationSelectorCertification? Linked { get; }

    public string Fingerprint { get; }

    internal bool Certifies(PhysicalMutationPlan plan)
    {
        var transition = plan.Action as PhysicalTransitionMutationAction;
        return Provider == plan.Predicate.Provider &&
               StorageUnit == plan.Predicate.StorageUnit &&
               MutationIdentity == plan.MutationIdentity &&
               RouteFingerprint == plan.RouteFingerprint &&
               ActionKind == plan.Action.Kind &&
               TransitionPath == transition?.Path &&
               TransitionTarget == transition?.TargetValue &&
               TransitionSources.SequenceEqual(transition?.AllowedSourceValues ?? []);
    }
}

public sealed class PhysicalMutationHandlerCertification
{
    private readonly PhysicalQueryHandlerCertification predicate;
    private readonly IReadOnlyList<string> allowedSourceValues;
    private readonly string? transitionPath;
    private readonly string? transitionTarget;

    public PhysicalMutationHandlerCertification(
        PhysicalMutationPlan plan,
        PhysicalMutationExecutionCertification? execution = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        MutationIdentity = plan.MutationIdentity;
        ActionKind = plan.Action.Kind;
        var transition = plan.Action as PhysicalTransitionMutationAction;
        transitionPath = transition?.Path;
        transitionTarget = transition?.TargetValue;
        allowedSourceValues = Array.AsReadOnly(transition?.AllowedSourceValues.ToArray() ?? []);
        var fields = plan.Predicate.RequiredFields
            .GroupBy(field => field.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal);
        predicate = new PhysicalQueryHandlerCertification(
            plan.Predicate.Provider,
            plan.Predicate.StorageUnit,
            plan.Predicate.QueryIdentity,
            plan.Predicate.LogicalIndexIdentity,
            plan.Predicate.LogicalIndexPaths,
            plan.Predicate.AccessKind,
            plan.Predicate.Scope.Field.Target,
            plan.Predicate.LookupObject,
            plan.Predicate.PrimaryObject,
            plan.Predicate.IndexName,
            fields,
            plan.Predicate.RouteFingerprint);
        if (execution is not null && !execution.Certifies(plan))
            throw new ArgumentException("Executable mutation certification does not match the compiled mutation plan.", nameof(execution));
        Execution = execution;
    }

    public string MutationIdentity { get; }

    public BoundedMutationActionKind ActionKind { get; }

    public PhysicalMutationExecutionCertification? Execution { get; }

    internal bool Certifies(PhysicalMutationPlan plan)
    {
        if (MutationIdentity != plan.MutationIdentity ||
            ActionKind != plan.Action.Kind ||
            !predicate.Certifies(plan.Predicate) ||
            (Execution is not null && !Execution.Certifies(plan)))
        {
            return false;
        }

        var transition = plan.Action as PhysicalTransitionMutationAction;
        return transitionPath == transition?.Path &&
               transitionTarget == transition?.TargetValue &&
               allowedSourceValues.SequenceEqual(transition?.AllowedSourceValues ?? []);
    }
}

/// <summary>One provider-owned executor for certified bounded mutation plans.</summary>
public interface IPhysicalDocumentMutationHandler
{
    string Identity { get; }
    PhysicalQuerySourceKind Source { get; }
    IReadOnlySet<PortableQueryOperation> SupportedOperations { get; }
    IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; }
    IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }
    IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; }
    bool SupportsCompoundPredicates { get; }
    bool SupportsDisjunction { get; }
    Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves and validates the complete named mutation before dispatching to a provider handler.
/// Unknown names, shapes, effects, routes, and capability claims fail before provider I/O.
/// </summary>
public sealed class PhysicalMutationDocumentStore : IBoundedDocumentMutationStore
{
    private readonly IReadOnlyDictionary<string, PhysicalMutationPlan> plans;
    private readonly IReadOnlyDictionary<string, IPhysicalDocumentMutationHandler> handlers;

    public PhysicalMutationDocumentStore(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities predicateCapabilities,
        IReadOnlyList<IPhysicalDocumentMutationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(predicateCapabilities);
        ArgumentNullException.ThrowIfNull(handlers);

        var byIdentity = handlers
            .GroupBy(handler => handler.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (byIdentity.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Value.Length != 1))
            throw new ArgumentException("Physical mutation handler identities must be non-empty and unique.", nameof(handlers));

        foreach (var registration in predicateCapabilities.HandlerIdentities)
        {
            if (!byIdentity.TryGetValue(registration.Value, out var matches) ||
                matches[0].Source != registration.Key ||
                !SupportsProfile(matches[0], predicateCapabilities))
            {
                throw new ArgumentException(
                    $"Mutation capability source '{registration.Key}' must reference one registered executable handler '{registration.Value}'.",
                    nameof(handlers));
            }
        }

        var compilation = PhysicalMutationPlanCompiler.Compile(route, storage, predicateCapabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        foreach (var plan in compilation.Plans)
        {
            var handler = byIdentity[plan.HandlerIdentity][0];
            if (!handler.SupportedActions.Contains(plan.Action.Kind) ||
                handler.Certifications is null ||
                !handler.Certifications.Any(certification => certification.Certifies(plan)))
            {
                throw new ArgumentException(
                    $"Physical mutation handler '{handler.Identity}' does not certify bounded mutation '{plan.MutationIdentity}'.",
                    nameof(handlers));
            }
        }

        plans = compilation.Plans.ToFrozenDictionary(plan => plan.MutationIdentity, StringComparer.Ordinal);
        this.handlers = byIdentity.ToFrozenDictionary(group => group.Key, group => group.Value[0], StringComparer.Ordinal);
    }

    public Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var plan = Admit(mutation);
        return handlers[plan.HandlerIdentity].ExecuteAsync(mutation, plan, cancellationToken);
    }

    /// <summary>
    /// Resolves and validates one complete closed mutation request without invoking a provider.
    /// Execution and provider-native explain paths use this same admission boundary.
    /// </summary>
    public PhysicalMutationPlan Admit(DocumentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!plans.TryGetValue(mutation.MutationIdentity, out var plan) ||
            plan.Predicate.StorageUnit.Value != mutation.DocumentKind)
        {
            throw new InvalidOperationException(
                $"Document mutation '{mutation.MutationIdentity}' is not bound to storage unit '{mutation.DocumentKind}'.");
        }

        ValidateRuntimeShape(mutation, plan);
        return plan;
    }

    private static bool SupportsProfile(
        IPhysicalDocumentMutationHandler handler,
        PhysicalQueryPlannerCapabilities capabilities) =>
        capabilities.SupportedOperations.IsSubsetOf(handler.SupportedOperations) &&
        (!capabilities.SupportsCompoundPredicates || handler.SupportsCompoundPredicates) &&
        (!capabilities.SupportsDisjunction || handler.SupportsDisjunction) &&
        (handler.Source != PhysicalQuerySourceKind.NativeDocumentFields ||
         capabilities.NativeFieldIdentifiers.All(field =>
             handler.NativeFieldIdentifiers.TryGetValue(field.Key, out var identifier) && identifier == field.Value));

    private static void ValidateRuntimeShape(DocumentMutation mutation, PhysicalMutationPlan plan)
    {
        var transitionPath = (plan.Action as PhysicalTransitionMutationAction)?.Path;
        var comparisons = mutation.Clauses.SelectMany(clause => clause.Comparisons).ToArray();
        foreach (var clause in mutation.Clauses)
        {
            if (clause.Comparisons.Count == 0)
                throw new InvalidOperationException($"Document mutation '{mutation.MutationIdentity}' cannot use a match-none clause.");
            if (clause.Comparisons.Count > 1 && !plan.Predicate.SupportsDisjunction)
                throw new InvalidOperationException($"Document mutation '{mutation.MutationIdentity}' does not declare disjunction.");
            if (clause.Comparisons.Select(comparison => comparison.Path).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidOperationException(
                    $"Document mutation '{mutation.MutationIdentity}' permits disjunction only within one declared predicate path.");
            }

            foreach (var comparison in clause.Comparisons)
            {
                if (comparison.Path == transitionPath)
                {
                    throw new InvalidOperationException(
                        $"Transition path '{transitionPath}' is fixed by mutation '{mutation.MutationIdentity}' and is not caller supplied.");
                }
                var operation = DocumentQueryOperations.ToPortable(comparison.Operator);
                if (plan.Predicate.Predicates.All(predicate =>
                        predicate.Path != comparison.Path || !predicate.Operations.Contains(operation)))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation}' on path '{comparison.Path}' is not bound to mutation '{mutation.MutationIdentity}'.");
                }
            }
        }

        foreach (var predicate in plan.Predicate.Predicates.Where(predicate => predicate.Path != transitionPath))
        {
            if (mutation.Clauses.Count(clause => clause.Comparisons.Any(comparison => comparison.Path == predicate.Path)) != 1)
            {
                throw new InvalidOperationException(
                    $"Document mutation '{mutation.MutationIdentity}' requires exactly one clause for path '{predicate.Path}'.");
            }
        }

        foreach (var path in plan.Predicate.RequiredEqualityPrefixPaths.Where(path => path != transitionPath))
        {
            var clause = mutation.Clauses.Single(item =>
                item.Comparisons.Any(comparison => comparison.Path == path));
            if (clause.Comparisons.Count != 1 ||
                clause.Comparisons[0].Operator != QueryComparisonOperator.Equal)
            {
                throw new InvalidOperationException(
                    $"Bounded mutation '{mutation.MutationIdentity}' requires one standalone equality comparison for prefix path '{path}'.");
            }
        }
    }

}
