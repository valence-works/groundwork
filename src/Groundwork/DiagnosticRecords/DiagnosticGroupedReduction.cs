using System.Collections.Frozen;
using System.Text;

namespace Groundwork.DiagnosticRecords;

/// <summary>Closed portable reductions admitted by a diagnostic stream profile.</summary>
public enum DiagnosticGroupReducerKind
{
    MinTimestamp,
    MaxTimestamp,
    SumInt64,
    MaxInt64,
    SetUnionString,
    FirstBy
}

/// <summary>
/// Closed secondary order for <see cref="DiagnosticGroupReducerKind.FirstBy"/>. The provider first
/// applies the declared field order, then selects the smallest raw diagnostic cursor among ties.
/// </summary>
public enum DiagnosticGroupFirstByTieBreak
{
    CursorAscending
}

public sealed record DiagnosticGroupReducerDefinition(
    string Alias,
    DiagnosticGroupReducerKind Kind,
    string Field,
    string? OrderField = null,
    DiagnosticSortDirection? OrderDirection = null,
    DiagnosticGroupFirstByTieBreak? TieBreak = null);

/// <summary>One output alias and the closed predicate operators a profile admits after reduction.</summary>
public sealed record DiagnosticGroupPredicateAllowance(
    string Alias,
    IReadOnlySet<DiagnosticPredicateOperator> SupportedPredicates);

/// <summary>
/// A named, bounded grouped-reduction surface. Callers select this declaration by name; they never
/// submit aggregation expressions or change its reducers.
/// </summary>
public sealed record DiagnosticGroupReductionProfile(
    string Name,
    string GroupKeyField,
    IReadOnlyList<DiagnosticGroupReducerDefinition> Reducers,
    IReadOnlyList<DiagnosticGroupPredicateAllowance> AllowedPredicates,
    IReadOnlySet<string> OrderableAliases,
    int MaxTake,
    int MaxUnionValues);

public sealed record DiagnosticRecordGroupOrder(
    string Alias,
    DiagnosticSortDirection Direction = DiagnosticSortDirection.Ascending);

public abstract record DiagnosticRecordGroupPredicate
{
    private DiagnosticRecordGroupPredicate() { }

    public sealed record All(IReadOnlyList<DiagnosticRecordGroupPredicate> Predicates) : DiagnosticRecordGroupPredicate;
    public sealed record Any(IReadOnlyList<DiagnosticRecordGroupPredicate> Predicates) : DiagnosticRecordGroupPredicate;
    public sealed record Comparison(
        string Alias,
        DiagnosticPredicateOperator Operator,
        IReadOnlyList<DiagnosticFieldValue> Values) : DiagnosticRecordGroupPredicate;
}

public static class DiagnosticRecordGroupQuerySnapshot
{
    public static DiagnosticRecordGroupQuery Capture(
        DiagnosticRecordGroupQuery query,
        int maximumPredicateNodes)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query with { Predicate = Capture(query.Predicate, maximumPredicateNodes) };
    }

    private static DiagnosticRecordGroupPredicate? Capture(
        DiagnosticRecordGroupPredicate? predicate,
        int maximumPredicateNodes)
    {
        if (predicate is null)
            return null;
        if (!DiagnosticRecordGroupPredicateTraversal.TryCollect(predicate, maximumPredicateNodes, out _))
            throw new DiagnosticRecordValidationException([
                new("group_query.predicate.too_complex", "The grouped predicate exceeds the declared node bound.", "predicate")
            ]);

        var frames = new Stack<SnapshotFrame>();
        frames.Push(new(predicate));
        DiagnosticRecordGroupPredicate? result = null;
        while (frames.Count > 0)
        {
            var frame = frames.Peek();
            if (frame.Source is DiagnosticRecordGroupPredicate.Comparison comparison)
            {
                Attach(comparison with
                {
                    Values = comparison.Values is null
                        ? null!
                        : Array.AsReadOnly(comparison.Values.ToArray())
                });
                continue;
            }

            var children = frame.Source switch
            {
                DiagnosticRecordGroupPredicate.All all => all.Predicates,
                DiagnosticRecordGroupPredicate.Any any => any.Predicates,
                _ => throw new ArgumentOutOfRangeException(nameof(predicate))
            };
            if (children is not null && frame.NextChild < children.Count)
            {
                var child = children[frame.NextChild++];
                if (child is null)
                    frame.Children.Add(null!);
                else
                    frames.Push(new(child));
                continue;
            }

            DiagnosticRecordGroupPredicate snapshot = frame.Source switch
            {
                DiagnosticRecordGroupPredicate.All all => all with
                {
                    Predicates = children is null ? null! : Array.AsReadOnly(frame.Children.ToArray())
                },
                DiagnosticRecordGroupPredicate.Any any => any with
                {
                    Predicates = children is null ? null! : Array.AsReadOnly(frame.Children.ToArray())
                },
                _ => throw new ArgumentOutOfRangeException(nameof(predicate))
            };
            Attach(snapshot);
        }

        return result!;

        void Attach(DiagnosticRecordGroupPredicate snapshot)
        {
            frames.Pop();
            if (frames.TryPeek(out var parent))
                parent.Children.Add(snapshot);
            else
                result = snapshot;
        }
    }

    private sealed class SnapshotFrame(DiagnosticRecordGroupPredicate source)
    {
        public DiagnosticRecordGroupPredicate Source { get; } = source;
        public List<DiagnosticRecordGroupPredicate> Children { get; } = [];
        public int NextChild { get; set; }
    }
}

internal static class DiagnosticRecordGroupPredicateTraversal
{
    public static bool TryCollect(
        DiagnosticRecordGroupPredicate predicate,
        int maximumPredicateNodes,
        out IReadOnlyList<DiagnosticRecordGroupPredicate> nodes)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPredicateNodes);
        var collected = new List<DiagnosticRecordGroupPredicate>(Math.Min(maximumPredicateNodes, 256));
        var pending = new Stack<DiagnosticRecordGroupPredicate>();
        pending.Push(predicate);
        while (pending.Count > 0)
        {
            if (collected.Count >= maximumPredicateNodes)
            {
                nodes = collected;
                return false;
            }

            var current = pending.Pop();
            collected.Add(current);
            var children = current switch
            {
                DiagnosticRecordGroupPredicate.All all => all.Predicates,
                DiagnosticRecordGroupPredicate.Any any => any.Predicates,
                _ => null
            };
            if (children is null)
                continue;
            if (children.Count > maximumPredicateNodes - collected.Count - pending.Count)
            {
                nodes = collected;
                return false;
            }
            for (var index = children.Count - 1; index >= 0; index--)
                if (children[index] is { } child)
                    pending.Push(child);
        }

        nodes = collected;
        return true;
    }
}

/// <summary>Query for one profile's provider-side reduced groups. Predicates run after reduction.</summary>
public sealed record DiagnosticRecordGroupQuery(
    DiagnosticStorageScope Scope,
    DiagnosticStreamId Stream,
    string Profile,
    int Take,
    DiagnosticRecordGroupOrder Order,
    DiagnosticRecordGroupPredicate? Predicate = null,
    DiagnosticRecordGroupContinuation? Continuation = null);

/// <summary>
/// Snapshot-bound keyset boundary for reduced groups. The sort value and group key are both part
/// of the order; the raw cursor high-water excludes later and backdated appends.
/// </summary>
public sealed record DiagnosticRecordGroupContinuation(
    DiagnosticCursor SnapshotHighWater,
    DiagnosticFieldValue LastOrderValue,
    string LastGroupKey,
    DiagnosticRequestFingerprint QueryFingerprint);

/// <summary>Provider-derived metadata only. It does not identify a raw record as the group result.</summary>
public sealed record DiagnosticRecordGroupRepresentative(DiagnosticCursor Cursor);

/// <summary>One reduced group; it deliberately has no payload or raw record identity.</summary>
public sealed record DiagnosticRecordGroup(
    string GroupKey,
    IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>> Fields,
    DiagnosticRecordGroupRepresentative Representative);

public sealed record DiagnosticRecordGroupPage(
    IReadOnlyList<DiagnosticRecordGroup> Groups,
    DiagnosticRecordGroupContinuation? Continuation);

public sealed record DiagnosticGroupedQueryHandlerCapabilities(
    bool SupportsGroupedReduction,
    IReadOnlySet<DiagnosticGroupReducerKind> SupportedReducers,
    IReadOnlySet<DiagnosticPredicateOperator> SupportedPredicates,
    bool SupportsSnapshotContinuation)
{
    public static DiagnosticGroupedQueryHandlerCapabilities Unsupported { get; } = new(
        false,
        FrozenSet<DiagnosticGroupReducerKind>.Empty,
        FrozenSet<DiagnosticPredicateOperator>.Empty,
        false);
}

public interface IDiagnosticGroupedQueryHandler
{
    DiagnosticGroupedQueryHandlerCapabilities Capabilities { get; }

    /// <summary>
    /// Providers must deep-capture the mutable request with
    /// <see cref="DiagnosticRecordGroupQuerySnapshot.Capture"/> before validation or provider I/O.
    /// </summary>
    ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>Default until a provider installs a native grouped-reduction executor.</summary>
public sealed class UnsupportedDiagnosticGroupedQueryHandler : IDiagnosticGroupedQueryHandler
{
    public static UnsupportedDiagnosticGroupedQueryHandler Instance { get; } = new();
    private UnsupportedDiagnosticGroupedQueryHandler() { }
    public DiagnosticGroupedQueryHandlerCapabilities Capabilities => DiagnosticGroupedQueryHandlerCapabilities.Unsupported;

    public ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DiagnosticRecordValidationException([
            new("group_query.capability.unsupported", "The bound handler does not support provider-side grouped reduction.", "capabilities")
        ]);
    }
}

public static class DiagnosticGroupReductionProfileResolver
{
    public static DiagnosticGroupReductionProfile? Resolve(DiagnosticRecordStreamDefinition definition, string name) =>
        definition.GroupReductionProfiles?.SingleOrDefault(profile => StringComparer.Ordinal.Equals(profile.Name, name));
}

public static class DiagnosticGroupReductionProfileValidator
{
    public static void Validate(DiagnosticRecordStreamDefinition definition, List<DiagnosticValidationError> errors)
    {
        var profiles = definition.GroupReductionProfiles ?? [];
        if (definition.GroupReductionProfiles is not null)
        {
            foreach (var duplicate in profiles.GroupBy(profile => profile.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                errors.Add(new("definition.group_profile.duplicate", $"Grouped-reduction profile '{duplicate.Key}' is declared more than once.", "groupReductionProfiles"));
        }

        foreach (var profile in profiles)
            ValidateProfile(definition, profile, errors);
    }

    private static void ValidateProfile(DiagnosticRecordStreamDefinition definition, DiagnosticGroupReductionProfile profile, List<DiagnosticValidationError> errors)
    {
        var path = $"groupReductionProfiles.{profile.Name}";
        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add(new("definition.group_profile.name.required", "A grouped-reduction profile name is required.", path));
        if (profile.MaxTake <= 0)
            errors.Add(new("definition.group_profile.take.unbounded", "A grouped-reduction profile requires a positive maximum take.", $"{path}.maxTake"));
        if (profile.MaxUnionValues <= 0)
            errors.Add(new("definition.group_profile.union.unbounded", "A grouped-reduction profile requires a positive set-union value bound.", $"{path}.maxUnionValues"));
        var groupKey = DiagnosticRecordFieldResolver.Resolve(definition, profile.GroupKeyField);
        if (groupKey is null || groupKey.Type != DiagnosticFieldType.String || groupKey.Cardinality != DiagnosticFieldCardinality.Scalar)
            errors.Add(new("definition.group_profile.key.invalid", "The group key must be a declared scalar String field.", $"{path}.groupKeyField"));

        var reducers = profile.Reducers ?? [];
        if (profile.Reducers is null || reducers.Count == 0)
            errors.Add(new("definition.group_profile.reducers.required", "A grouped-reduction profile requires at least one reducer.", $"{path}.reducers"));
        foreach (var duplicate in reducers.GroupBy(reducer => reducer.Alias, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add(new("definition.group_profile.alias.duplicate", $"Reducer alias '{duplicate.Key}' is declared more than once.", $"{path}.reducers"));
        foreach (var reducer in reducers)
            ValidateReducer(definition, profile, reducer, errors, path);

        var aliases = reducers.Select(reducer => reducer.Alias).ToHashSet(StringComparer.Ordinal);
        var allowances = profile.AllowedPredicates ?? [];
        if (profile.AllowedPredicates is null)
            errors.Add(new("definition.group_profile.predicates.required", "Grouped predicate allowances must be declared, even when empty.", $"{path}.allowedPredicates"));
        foreach (var duplicate in allowances.GroupBy(allowance => allowance.Alias, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add(new("definition.group_profile.predicate_alias.duplicate", $"Predicate allowance for '{duplicate.Key}' is declared more than once.", $"{path}.allowedPredicates"));
        foreach (var allowance in allowances)
        {
            if (!aliases.Contains(allowance.Alias))
                errors.Add(new("definition.group_profile.predicate_alias.unknown", $"Predicate allowance '{allowance.Alias}' is not a reducer output.", $"{path}.allowedPredicates"));
            if (allowance.SupportedPredicates is null || allowance.SupportedPredicates.Count == 0)
                errors.Add(new("definition.group_profile.predicate.empty", $"Predicate allowance '{allowance.Alias}' must name at least one operator.", $"{path}.allowedPredicates"));
            else if (aliases.Contains(allowance.Alias))
            {
                var reducer = reducers.First(item => StringComparer.Ordinal.Equals(item.Alias, allowance.Alias));
                var output = DiagnosticRecordFieldResolver.Resolve(definition, reducer.Field);
                if (output is not null && output.Type != DiagnosticFieldType.String &&
                    allowance.SupportedPredicates.Contains(DiagnosticPredicateOperator.Contains))
                    errors.Add(new("definition.group_profile.predicate.contains.invalid", $"Non-string output '{allowance.Alias}' cannot declare Contains.", $"{path}.allowedPredicates"));
            }
        }
        if (profile.OrderableAliases is null || profile.OrderableAliases.Count == 0)
            errors.Add(new("definition.group_profile.order.required", "A grouped-reduction profile requires at least one orderable output alias.", $"{path}.orderableAliases"));
        else
            foreach (var alias in profile.OrderableAliases)
            {
                if (!aliases.Contains(alias))
                    errors.Add(new("definition.group_profile.order_alias.unknown", $"Order alias '{alias}' is not a reducer output.", $"{path}.orderableAliases"));
                else if (reducers.First(reducer => StringComparer.Ordinal.Equals(reducer.Alias, alias)).Kind == DiagnosticGroupReducerKind.SetUnionString)
                    errors.Add(new("definition.group_profile.order_alias.multi_value", $"Set-union output '{alias}' cannot define a single reduced order.", $"{path}.orderableAliases"));
            }
    }

    private static void ValidateReducer(DiagnosticRecordStreamDefinition definition, DiagnosticGroupReductionProfile profile, DiagnosticGroupReducerDefinition reducer, List<DiagnosticValidationError> errors, string path)
    {
        if (string.IsNullOrWhiteSpace(reducer.Alias))
            errors.Add(new("definition.group_profile.alias.required", "Reducer aliases are required.", $"{path}.reducers"));
        if (!Enum.IsDefined(reducer.Kind))
            errors.Add(new("definition.group_profile.reducer.unknown", $"Reducer '{reducer.Alias}' has an unknown kind.", $"{path}.reducers.{reducer.Alias}"));
        var field = DiagnosticRecordFieldResolver.Resolve(definition, reducer.Field);
        var requiredType = reducer.Kind switch
        {
            DiagnosticGroupReducerKind.MinTimestamp or DiagnosticGroupReducerKind.MaxTimestamp => DiagnosticFieldType.Timestamp,
            DiagnosticGroupReducerKind.SumInt64 or DiagnosticGroupReducerKind.MaxInt64 => DiagnosticFieldType.Int64,
            DiagnosticGroupReducerKind.SetUnionString => DiagnosticFieldType.String,
            _ => (DiagnosticFieldType?)null
        };
        if (field is null || field.Cardinality != DiagnosticFieldCardinality.Scalar && reducer.Kind != DiagnosticGroupReducerKind.SetUnionString || requiredType is not null && field?.Type != requiredType)
            errors.Add(new("definition.group_profile.reducer.incompatible", $"Reducer '{reducer.Alias}' is incompatible with field '{reducer.Field}'.", $"{path}.reducers.{reducer.Alias}"));
        if (reducer.Kind == DiagnosticGroupReducerKind.SetUnionString && profile.MaxUnionValues <= 0)
            errors.Add(new("definition.group_profile.union.unbounded", "Set-union requires a positive profile union bound.", $"{path}.maxUnionValues"));
        if (reducer.Kind == DiagnosticGroupReducerKind.FirstBy)
        {
            var order = reducer.OrderField is null ? null : DiagnosticRecordFieldResolver.Resolve(definition, reducer.OrderField);
            if (field is null || field.Cardinality != DiagnosticFieldCardinality.Scalar ||
                order is null || order.Cardinality != DiagnosticFieldCardinality.Scalar || !order.IsOrderable ||
                reducer.OrderDirection is null || !Enum.IsDefined(reducer.OrderDirection.Value) ||
                reducer.TieBreak != DiagnosticGroupFirstByTieBreak.CursorAscending)
                errors.Add(new(
                    "definition.group_profile.first_by.invalid",
                    $"FirstBy reducer '{reducer.Alias}' requires scalar output and order fields, an explicit direction, and the ascending raw-cursor secondary tie-break.",
                    $"{path}.reducers.{reducer.Alias}"));
        }
        else if (reducer.OrderField is not null || reducer.OrderDirection is not null || reducer.TieBreak is not null)
            errors.Add(new("definition.group_profile.reducer.order.unexpected", $"Only FirstBy may declare an order field, direction, or cursor tie-break.", $"{path}.reducers.{reducer.Alias}"));
    }

    public static DiagnosticFieldDefinition OutputField(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticGroupReductionProfile profile,
        string alias)
    {
        var reducer = profile.Reducers.Single(item => StringComparer.Ordinal.Equals(item.Alias, alias));
        return DiagnosticRecordFieldResolver.Resolve(definition, reducer.Field)!;
    }

    public static DiagnosticFieldType OutputType(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticGroupReductionProfile profile,
        string alias) =>
        OutputField(definition, profile, alias).Type;
}

public static class DiagnosticRecordGroupQueryValidator
{
    public static void Validate(DiagnosticRecordGroupQuery query, DiagnosticRecordStreamDefinition definition, IDiagnosticGroupedQueryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        var errors = new List<DiagnosticValidationError>();
        if (string.IsNullOrWhiteSpace(query.Scope.TenantId) || string.IsNullOrWhiteSpace(query.Scope.ScopeId))
            errors.Add(new("group_query.scope.required", "An explicit tenant and storage scope are required.", "scope"));
        if (query.Stream != definition.Stream)
            errors.Add(new("group_query.stream.unknown", $"Stream '{query.Stream.Value}' is not declared.", "stream"));
        var profile = string.IsNullOrWhiteSpace(query.Profile) ? null : DiagnosticGroupReductionProfileResolver.Resolve(definition, query.Profile);
        if (profile is null)
            errors.Add(new("group_query.profile.undeclared", $"Grouped-reduction profile '{query.Profile}' is not declared.", "profile"));
        if (profile is not null && (query.Take <= 0 || query.Take > profile.MaxTake))
            errors.Add(new("group_query.take.invalid", $"Take must be between 1 and {profile.MaxTake} for profile '{profile.Name}'.", "take"));
        if (query.Order is null || profile is not null && !profile.OrderableAliases.Contains(query.Order.Alias))
            errors.Add(new("group_query.order.undeclared", "The reduced order alias is not declared by the selected profile.", "order"));
        var capabilities = handler.Capabilities ?? throw new ArgumentNullException(nameof(handler.Capabilities));
        if (!capabilities.SupportsGroupedReduction)
            errors.Add(new("group_query.capability.unsupported", "The bound handler does not support provider-side grouped reduction.", "capabilities"));
        if (profile is not null)
            foreach (var reducer in profile.Reducers)
                if (!capabilities.SupportedReducers.Contains(reducer.Kind))
                    errors.Add(new("group_query.reducer.unsupported", $"The bound handler cannot execute reducer '{reducer.Kind}'.", "profile"));
        if (query.Continuation is not null && !capabilities.SupportsSnapshotContinuation)
            errors.Add(new("group_query.continuation.unsupported", "The bound handler cannot execute snapshot continuation.", "continuation"));
        IReadOnlyList<DiagnosticRecordGroupPredicate> predicateNodes = [];
        var predicateTooComplex = query.Predicate is not null &&
                                  !DiagnosticRecordGroupPredicateTraversal.TryCollect(
                                      query.Predicate,
                                      definition.Limits.MaxPredicateNodes,
                                      out predicateNodes);
        if (predicateTooComplex)
            errors.Add(new("group_query.predicate.too_complex", "The grouped predicate exceeds the declared node bound.", "predicate"));
        if (profile is not null && query.Predicate is not null && !predicateTooComplex)
        {
            var valueCount = 0;
            foreach (var predicate in predicateNodes)
                ValidatePredicateNode(predicate, definition, profile, capabilities, errors, ref valueCount);
        }
        if (query.Continuation is { } continuation)
        {
            if (string.IsNullOrWhiteSpace(continuation.SnapshotHighWater.Value) || string.IsNullOrWhiteSpace(continuation.LastGroupKey))
                errors.Add(new("group_query.continuation.invalid", "Grouped continuation requires a snapshot high-water and last group key.", "continuation"));
            else if (!continuation.LastOrderValue.IsInitialized)
                errors.Add(new("group_query.continuation.order_value.invalid", "Grouped continuation requires an initialized reduced order value.", "continuation.lastOrderValue"));
            else if (profile is not null && query.Order is not null && continuation.LastOrderValue.Type != DiagnosticGroupReductionProfileValidator.OutputType(definition, profile, query.Order.Alias))
                errors.Add(new("group_query.continuation.order_type", "Grouped continuation has a different reduced order value type.", "continuation.lastOrderValue"));
            else if (continuation.QueryFingerprint != DiagnosticRequestFingerprint.ForGroupQuery(query with { Continuation = null }, definition))
                errors.Add(new("group_query.continuation.query_mismatch", "The continuation is bound to a different grouped query shape or stream definition.", "continuation"));
        }
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }

    private static void ValidatePredicateNode(
        DiagnosticRecordGroupPredicate predicate,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticGroupReductionProfile profile,
        DiagnosticGroupedQueryHandlerCapabilities capabilities,
        List<DiagnosticValidationError> errors,
        ref int valueCount)
    {
        switch (predicate)
        {
            case DiagnosticRecordGroupPredicate.All all:
                ValidateChildren(all.Predicates, errors);
                break;
            case DiagnosticRecordGroupPredicate.Any any:
                ValidateChildren(any.Predicates, errors);
                break;
            case DiagnosticRecordGroupPredicate.Comparison comparison:
                if (string.IsNullOrWhiteSpace(comparison.Alias))
                {
                    errors.Add(new("group_query.predicate.alias_required", "Reduced predicate output alias is required.", "predicate.alias"));
                    break;
                }
                var allowance = (profile.AllowedPredicates ?? []).SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Alias, comparison.Alias));
                if (allowance is null)
                {
                    errors.Add(new("group_query.predicate.undeclared", $"Predicate '{comparison.Operator}' is not declared for reduced output '{comparison.Alias}'.", "predicate"));
                    break;
                }
                if (!allowance.SupportedPredicates.Contains(comparison.Operator))
                    errors.Add(new("group_query.predicate.undeclared", $"Predicate '{comparison.Operator}' is not declared for reduced output '{comparison.Alias}'.", "predicate"));
                if (!capabilities.SupportedPredicates.Contains(comparison.Operator))
                    errors.Add(new("group_query.predicate.unsupported", $"The bound handler cannot execute {comparison.Operator} after reduction.", "predicate"));
                var requiredCount = comparison.Operator switch
                {
                    DiagnosticPredicateOperator.Equal or DiagnosticPredicateOperator.Contains => 1,
                    DiagnosticPredicateOperator.RangeInclusive => 2,
                    DiagnosticPredicateOperator.In => -1,
                    _ => 0
                };
                if (comparison.Values is null)
                {
                    errors.Add(new("group_query.predicate.values.invalid", $"{comparison.Operator} requires a value collection.", "predicate.values"));
                    break;
                }
                valueCount += comparison.Values.Count;
                if (valueCount > definition.Limits.MaxPredicateValues)
                    errors.Add(new(
                        "group_query.predicate.values.too_many",
                        $"The grouped predicate exceeds the declared value bound of {definition.Limits.MaxPredicateValues}.",
                        "predicate.values"));
                if (requiredCount >= 0 && comparison.Values.Count != requiredCount ||
                    requiredCount == -1 && comparison.Values.Count == 0)
                    errors.Add(new("group_query.predicate.values.invalid", $"{comparison.Operator} has an invalid value count.", "predicate.values"));

                var output = DiagnosticGroupReductionProfileValidator.OutputField(definition, profile, comparison.Alias);
                if (comparison.Values.Any(value => !value.IsInitialized))
                    errors.Add(new("group_query.predicate.value_invalid", $"Predicate values for '{comparison.Alias}' must be initialized portable values.", "predicate.values"));
                else if (comparison.Values.Any(value => value.Type != output.Type))
                    errors.Add(new("group_query.predicate.value_type", $"Predicate values for '{comparison.Alias}' must be {output.Type}.", "predicate.values"));
                else if (output.Type == DiagnosticFieldType.String &&
                         comparison.Values.Any(value => Encoding.UTF8.GetByteCount(value.CanonicalValue) > output.MaxStringBytes))
                    errors.Add(new("group_query.predicate.string_too_large", $"Predicate values for '{comparison.Alias}' exceed its declared byte bound.", "predicate.values"));
                else if (output.Type == DiagnosticFieldType.String &&
                         output.CasePolicy == DiagnosticStringCasePolicy.AsciiIgnoreCase &&
                         comparison.Values.Any(value => !DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(value.CanonicalValue)))
                    errors.Add(new(
                        "group_query.predicate.case_domain",
                        $"Predicate values for '{comparison.Alias}' use {DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId} and accept only U+0020 through U+007E.",
                        "predicate.values"));
                else if (comparison.Operator == DiagnosticPredicateOperator.RangeInclusive &&
                         comparison.Values.Count == 2 &&
                         comparison.Values[0].CompareTo(comparison.Values[1], output.CasePolicy) > 0)
                    errors.Add(new("group_query.predicate.range_reversed", "Inclusive range lower bound cannot exceed its upper bound.", "predicate.values"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(predicate));
        }
    }

    private static void ValidateChildren(
        IReadOnlyList<DiagnosticRecordGroupPredicate> children,
        List<DiagnosticValidationError> errors)
    {
        if (children is null || children.Count == 0)
        {
            errors.Add(new("group_query.predicate.empty", "A grouped logical predicate must contain at least one child.", "predicate"));
            return;
        }
        foreach (var child in children)
            if (child is null)
                errors.Add(new("group_query.predicate.child_null", "Grouped predicate children cannot be null.", "predicate"));
    }
}
