namespace Groundwork.DiagnosticRecords;

public abstract record DiagnosticRecordPredicate
{
    private DiagnosticRecordPredicate() { }

    public sealed record All(IReadOnlyList<DiagnosticRecordPredicate> Predicates) : DiagnosticRecordPredicate;
    public sealed record Any(IReadOnlyList<DiagnosticRecordPredicate> Predicates) : DiagnosticRecordPredicate;
    public sealed record Comparison(
        string Field,
        DiagnosticPredicateOperator Operator,
        IReadOnlyList<DiagnosticFieldValue> Values) : DiagnosticRecordPredicate;

    public static DiagnosticRecordPredicate Equal(string field, DiagnosticFieldValue value) =>
        new Comparison(field, DiagnosticPredicateOperator.Equal, [value]);

    public static DiagnosticRecordPredicate In(string field, params DiagnosticFieldValue[] values) =>
        new Comparison(field, DiagnosticPredicateOperator.In, values);

    public static DiagnosticRecordPredicate RangeInclusive(
        string field,
        DiagnosticFieldValue lower,
        DiagnosticFieldValue upper) =>
        new Comparison(field, DiagnosticPredicateOperator.RangeInclusive, [lower, upper]);

    public static DiagnosticRecordPredicate Contains(string field, string value) =>
        new Comparison(field, DiagnosticPredicateOperator.Contains, [DiagnosticFieldValue.String(value)]);
}

public static class DiagnosticRecordQuerySnapshot
{
    public static DiagnosticRecordQuery Capture(DiagnosticRecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query with { Predicate = Capture(query.Predicate) };
    }

    private static DiagnosticRecordPredicate? Capture(DiagnosticRecordPredicate? predicate) => predicate switch
    {
        null => null,
        DiagnosticRecordPredicate.All all => all with
        {
            Predicates = Capture(all.Predicates)
        },
        DiagnosticRecordPredicate.Any any => any with
        {
            Predicates = Capture(any.Predicates)
        },
        DiagnosticRecordPredicate.Comparison comparison => comparison with
        {
            Values = comparison.Values is null
                ? null!
                : Array.AsReadOnly(comparison.Values.ToArray())
        },
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    private static IReadOnlyList<DiagnosticRecordPredicate> Capture(
        IReadOnlyList<DiagnosticRecordPredicate> predicates) =>
        predicates is null
            ? null!
            : Array.AsReadOnly(predicates.Select(x => x is null ? null! : Capture(x)!).ToArray());
}

public static class DiagnosticRecordQueryValidator
{
    public static void Validate(
        DiagnosticRecordQuery query,
        DiagnosticRecordStreamDefinition definition,
        IDiagnosticQueryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        var capabilities = handler.Capabilities;
        ArgumentNullException.ThrowIfNull(capabilities);
        var errors = new List<DiagnosticValidationError>();
        if (string.IsNullOrWhiteSpace(query.Scope.TenantId) || string.IsNullOrWhiteSpace(query.Scope.ScopeId))
            errors.Add(new("query.scope.required", "An explicit tenant and storage scope are required.", "scope"));
        if (query.Stream != definition.Stream)
            errors.Add(new("query.stream.unknown", $"Stream '{query.Stream.Value}' is not declared.", "stream"));
        if (query.Limit <= 0 || query.Limit > definition.Limits.MaxQueryLimit)
            errors.Add(new("query.limit.invalid", $"Query limit must be between 1 and {definition.Limits.MaxQueryLimit}.", "limit"));

        var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
        if (order.Field is null && !capabilities.SupportsCursorOrder)
            errors.Add(new("query.order.unsupported", "The bound query handler cannot execute cursor ordering.", "order"));
        if (order.Field is { } orderField)
        {
            var field = DiagnosticRecordFieldResolver.Resolve(definition, orderField);
            if (field is null || !field.IsOrderable)
                errors.Add(new("query.order.undeclared", $"Field '{orderField}' is not declared as orderable.", "order.field"));
            if (!capabilities.SupportsFieldOrder)
                errors.Add(new("query.order.unsupported", "The bound query handler cannot execute field-plus-cursor ordering.", "order"));
        }

        if (query.Continuation is not null && !capabilities.SupportsSnapshotContinuation)
            errors.Add(new("query.continuation.unsupported", "The bound query handler cannot execute snapshot continuation.", "continuation"));
        if (query.Continuation is { } continuation)
        {
            if (string.IsNullOrWhiteSpace(continuation.SnapshotHighWater.Value) || string.IsNullOrWhiteSpace(continuation.LastCursor.Value))
                errors.Add(new("query.continuation.cursor_invalid", "Continuation cursors must be non-empty provider cursor values.", "continuation"));
            if (order.Field is null && continuation.LastOrderValue is not null)
                errors.Add(new("query.continuation.order_value.unexpected", "Cursor-only continuation cannot carry a field order value.", "continuation.lastOrderValue"));
            if (order.Field is { } continuationOrderField)
            {
                var field = DiagnosticRecordFieldResolver.Resolve(definition, continuationOrderField);
                if (continuation.LastOrderValue is null || field is not null && continuation.LastOrderValue.Value.Type != field.Type)
                    errors.Add(new("query.continuation.order_value.invalid", "Field-ordered continuation must carry a matching last order value.", "continuation.lastOrderValue"));
                else if (field is { Type: DiagnosticFieldType.String, CasePolicy: DiagnosticStringCasePolicy.AsciiIgnoreCase } &&
                         !DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(continuation.LastOrderValue.Value.CanonicalValue))
                    errors.Add(new(
                        "query.continuation.case_domain",
                        $"Field-ordered continuation uses {DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId} and accepts only U+0020 through U+007E.",
                        "continuation.lastOrderValue"));
            }
        }
        if (query.IncludeExactCount && !capabilities.SupportsExactCount)
            errors.Add(new("query.count.unsupported", "The bound query handler cannot produce an exact count.", "includeExactCount"));
        if (query.LatestPerKeyField is { } latestField)
        {
            var field = DiagnosticRecordFieldResolver.Resolve(definition, latestField);
            if (field is null || !field.SupportsLatestPerKey || field.Cardinality != DiagnosticFieldCardinality.Scalar)
                errors.Add(new("query.latest_per_key.undeclared", $"Field '{latestField}' does not declare latest-per-key selection.", "latestPerKeyField"));
            if (!capabilities.SupportsLatestPerKey)
                errors.Add(new("query.latest_per_key.unsupported", "The bound query handler cannot execute latest-per-key selection.", "latestPerKeyField"));
        }

        if (query.Predicate is not null)
        {
            var valueCount = 0;
            ValidatePredicate(query.Predicate, definition, capabilities, errors, 1, ref valueCount);
        }

        if (errors.Count == 0 && query.Continuation is { } validContinuation &&
            validContinuation.QueryFingerprint != DiagnosticRequestFingerprint.ForQuery(query with { Continuation = null }, definition))
            errors.Add(new("query.continuation.query_mismatch", "The continuation is bound to a different query shape or stream definition.", "continuation"));

        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }

    private static int ValidatePredicate(
        DiagnosticRecordPredicate predicate,
        DiagnosticRecordStreamDefinition definition,
        DiagnosticQueryHandlerCapabilities capabilities,
        List<DiagnosticValidationError> errors,
        int count,
        ref int valueCount)
    {
        if (count > definition.Limits.MaxPredicateNodes)
        {
            errors.Add(new("query.predicate.too_complex", "The predicate exceeds the declared node bound.", "predicate"));
            return count;
        }

        switch (predicate)
        {
            case DiagnosticRecordPredicate.All all:
                if (all.Predicates is null || all.Predicates.Count == 0)
                {
                    errors.Add(new("query.predicate.empty", "An all-predicate must contain at least one child.", "predicate"));
                    break;
                }
                foreach (var child in all.Predicates)
                {
                    if (child is null)
                        errors.Add(new("query.predicate.child_null", "Predicate children cannot be null.", "predicate"));
                    else
                        count = ValidatePredicate(child, definition, capabilities, errors, count + 1, ref valueCount);
                }
                break;
            case DiagnosticRecordPredicate.Any any:
                if (any.Predicates is null || any.Predicates.Count == 0)
                {
                    errors.Add(new("query.predicate.empty", "An any-predicate must contain at least one child.", "predicate"));
                    break;
                }
                foreach (var child in any.Predicates)
                {
                    if (child is null)
                        errors.Add(new("query.predicate.child_null", "Predicate children cannot be null.", "predicate"));
                    else
                        count = ValidatePredicate(child, definition, capabilities, errors, count + 1, ref valueCount);
                }
                break;
            case DiagnosticRecordPredicate.Comparison comparison:
                if (string.IsNullOrWhiteSpace(comparison.Field))
                {
                    errors.Add(new("query.predicate.field_required", "Predicate field identity is required.", "predicate.field"));
                    break;
                }
                var field = DiagnosticRecordFieldResolver.Resolve(definition, comparison.Field);
                if (field is null)
                {
                    errors.Add(new("query.predicate.field_undeclared", $"Field '{comparison.Field}' is not declared.", "predicate.field"));
                    break;
                }
                if (!field.SupportedPredicates.Contains(comparison.Operator))
                    errors.Add(new("query.predicate.undeclared", $"{comparison.Operator} is not declared for field '{comparison.Field}'.", "predicate.operator"));
                if (!capabilities.SupportedPredicates.Contains(comparison.Operator))
                    errors.Add(new("query.predicate.unsupported", $"The bound query handler cannot execute {comparison.Operator}.", "predicate.operator"));
                var requiredCount = comparison.Operator switch
                {
                    DiagnosticPredicateOperator.Equal or DiagnosticPredicateOperator.Contains => 1,
                    DiagnosticPredicateOperator.RangeInclusive => 2,
                    DiagnosticPredicateOperator.In => -1,
                    _ => 0
                };
                if (comparison.Values is null)
                {
                    errors.Add(new("query.predicate.values.invalid", $"{comparison.Operator} requires a value collection.", "predicate.values"));
                    break;
                }
                valueCount += comparison.Values.Count;
                if (valueCount > definition.Limits.MaxPredicateValues)
                    errors.Add(new(
                        "query.predicate.values.too_many",
                        $"The predicate exceeds the declared value bound of {definition.Limits.MaxPredicateValues}.",
                        "predicate.values"));
                if (requiredCount >= 0 && comparison.Values.Count != requiredCount || requiredCount == -1 && comparison.Values.Count == 0)
                    errors.Add(new("query.predicate.values.invalid", $"{comparison.Operator} has an invalid value count.", "predicate.values"));
                if (comparison.Values.Any(x => !x.IsInitialized))
                    errors.Add(new("query.predicate.value_invalid", $"Predicate values for '{comparison.Field}' must be initialized portable values.", "predicate.values"));
                else if (comparison.Values.Any(x => x.Type != field.Type))
                    errors.Add(new("query.predicate.value_type", $"Predicate values for '{comparison.Field}' must be {field.Type}.", "predicate.values"));
                else if (field.Type == DiagnosticFieldType.String &&
                         field.CasePolicy == DiagnosticStringCasePolicy.AsciiIgnoreCase &&
                         comparison.Values.Any(x => !DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(x.CanonicalValue)))
                    errors.Add(new(
                        "query.predicate.case_domain",
                        $"Predicate values for '{comparison.Field}' use {DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId} and accept only U+0020 through U+007E.",
                        "predicate.values"));
                else if (comparison.Operator == DiagnosticPredicateOperator.RangeInclusive &&
                         comparison.Values.Count == 2 &&
                         comparison.Values[0].CompareTo(comparison.Values[1], field.CasePolicy) > 0)
                    errors.Add(new("query.predicate.range_reversed", "Inclusive range lower bound cannot exceed its upper bound.", "predicate.values"));
                if (comparison.Operator == DiagnosticPredicateOperator.Contains && field.Type != DiagnosticFieldType.String)
                    errors.Add(new("query.predicate.contains_type", "Contains is only valid for string fields.", "predicate.operator"));
                break;
        }

        return count;
    }
}
