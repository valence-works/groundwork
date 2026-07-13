using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Immutable provider-neutral execution evidence for one named bounded mutation. Predicate
/// selection is delegated to the same closed physical plan used by bounded document queries.
/// </summary>
public abstract record PhysicalMutationAction(BoundedMutationActionKind Kind);

public sealed record PhysicalDeleteMutationAction() :
    PhysicalMutationAction(BoundedMutationActionKind.Delete);

public sealed record PhysicalTransitionMutationAction(
    string Path,
    IReadOnlyList<string> AllowedSourceValues,
    string TargetValue,
    PhysicalQueryField Field) :
    PhysicalMutationAction(BoundedMutationActionKind.Transition);

public sealed record PhysicalMutationPlan(
    string MutationIdentity,
    PhysicalMutationAction Action,
    PhysicalQueryPlan Predicate)
{
    public string HandlerIdentity => Predicate.HandlerIdentity;

    public string RouteFingerprint => Predicate.RouteFingerprint;
}

public sealed class PhysicalMutationPlanCompilationResult
{
    public PhysicalMutationPlanCompilationResult(
        IReadOnlyList<PhysicalMutationPlan> plans,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        Plans = Array.AsReadOnly((plans ?? throw new ArgumentNullException(nameof(plans))).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public IReadOnlyList<PhysicalMutationPlan> Plans { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);
}

/// <summary>
/// Compiles declared mutation names and effects against closed physical query plans. It never
/// produces an unbounded or client-evaluated selector.
/// </summary>
public static class PhysicalMutationPlanCompiler
{
    public static PhysicalMutationPlanCompilationResult Compile(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities predicateCapabilities)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(predicateCapabilities);

        var queryCompilation = PhysicalQueryPlanCompiler.Compile(route, storage, predicateCapabilities);
        var diagnostics = queryCompilation.Diagnostics.ToList();
        if (!queryCompilation.IsValid)
            return new([], diagnostics);

        var duplicateIdentities = storage.BoundedMutations
            .GroupBy(mutation => mutation.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateIdentities.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-001",
                $"Bounded mutation identities must be unique: {string.Join(", ", duplicateIdentities)}.",
                $"physicalMutations.{route.StorageUnit.Value}"));
            return new([], diagnostics);
        }

        var plans = new List<PhysicalMutationPlan>();
        foreach (var mutation in storage.BoundedMutations)
        {
            var predicate = queryCompilation.Plans.SingleOrDefault(plan =>
                plan.QueryIdentity == mutation.PredicateQueryIdentity);
            if (predicate is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-002",
                    $"Bounded mutation '{mutation.Identity}' must reference one compiled bounded predicate query '{mutation.PredicateQueryIdentity}'.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }
            if (!predicate.IsScaleBearing || predicate.IndexName is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-MUTATION-004",
                    $"Bounded mutation '{mutation.Identity}' requires a scale-bearing indexed server-side predicate.",
                    $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}"));
                continue;
            }

            var action = CompileAction(mutation, predicate, route, diagnostics);
            if (action is not null)
                plans.Add(new PhysicalMutationPlan(mutation.Identity, action, predicate));
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError)
            ? new([], diagnostics)
            : new(plans, diagnostics);
    }

    private static PhysicalMutationAction? CompileAction(
        BoundedMutationDeclaration mutation,
        PhysicalQueryPlan predicate,
        ExecutableStorageRoute route,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (mutation.Action is BoundedDeleteMutationAction)
            return new PhysicalDeleteMutationAction();

        var transition = (BoundedTransitionMutationAction)mutation.Action;
        var field = predicate.Predicates.SingleOrDefault(candidate => candidate.Path == transition.Path);
        if (field is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' must be part of bounded predicate query '{predicate.QueryIdentity}'.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (!field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.Equal) &&
            !field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.In))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' must support exact source-value matching.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (transition.AllowedSourceValues.Count > 1 &&
            !field.Operations.Contains(Groundwork.Core.Indexing.PortableQueryOperation.In) &&
            !predicate.SupportsDisjunction)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' requires IN or provider-certified disjunction for multiple source values.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }
        if (transition.AllowedSourceValues.Count > 1 &&
            predicate.RequiredEqualityPrefixPaths.Contains(transition.Path, StringComparer.Ordinal))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MUTATION-003",
                $"Transition path '{transition.Path}' is an equality prefix and therefore requires exactly one source value.",
                $"physicalMutations.{route.StorageUnit.Value}.{mutation.Identity}.action"));
            return null;
        }

        return new PhysicalTransitionMutationAction(
            transition.Path,
            transition.AllowedSourceValues,
            transition.TargetValue,
            field.Field);
    }
}
