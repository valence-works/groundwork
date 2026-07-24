using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

public sealed class DiagnosticGroupedReductionContractTests
{
    [Fact]
    public void Stream_definition_rejects_incompatible_duplicate_and_unbounded_group_reducers()
    {
        var profile = Profile() with
        {
            MaxTake = 0,
            MaxUnionValues = 0,
            Reducers =
            [
                new("start", DiagnosticGroupReducerKind.SumInt64, "start"),
                new("start", DiagnosticGroupReducerKind.MaxTimestamp, "end")
            ]
        };

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(Definition(profile)));

        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.take.unbounded");
        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.union.unbounded");
        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.alias.duplicate");
        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.reducer.incompatible");
    }

    [Fact]
    public void FirstBy_requires_explicit_direction_and_cursor_tie_break_and_other_reducers_reject_them()
    {
        var profile = Profile() with
        {
            Reducers =
            [
                new("rootName", DiagnosticGroupReducerKind.FirstBy, "name", "start"),
                new(
                    "status",
                    DiagnosticGroupReducerKind.MaxInt64,
                    "status",
                    OrderDirection: DiagnosticSortDirection.Descending,
                    TieBreak: DiagnosticGroupFirstByTieBreak.CursorAscending)
            ]
        };

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(Definition(profile)));

        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.first_by.invalid");
        Assert.Contains(exception.Errors, error => error.Code == "definition.group_profile.reducer.order.unexpected");
    }

    [Fact]
    public void Group_query_rejects_undeclared_reduction_and_provider_capability_before_execution()
    {
        var query = Query() with { Profile = "missing", Take = 0 };
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQueryValidator.Validate(query, Definition(Profile()), UnsupportedDiagnosticGroupedQueryHandler.Instance));

        Assert.Contains(exception.Errors, error => error.Code == "group_query.profile.undeclared");
        Assert.Contains(exception.Errors, error => error.Code == "group_query.capability.unsupported");
    }

    [Fact]
    public async Task Default_group_handler_fails_with_the_stable_capability_error_without_a_provider_executor()
    {
        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            UnsupportedDiagnosticGroupedQueryHandler.Instance.QueryGroupsAsync(Query()).AsTask());

        Assert.Contains(exception.Errors, error => error.Code == "group_query.capability.unsupported");
    }

    [Fact]
    public void Group_continuation_is_bound_to_the_complete_profiled_query_shape()
    {
        var definition = Definition(Profile());
        var query = Query();
        var continuation = new DiagnosticRecordGroupContinuation(
            new("42"),
            DiagnosticFieldValue.Timestamp(DateTimeOffset.Parse("2026-07-24T12:00:00Z")),
            "trace-a",
            DiagnosticRequestFingerprint.ForGroupQuery(query, definition));
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQueryValidator.Validate(
                query with { Take = query.Take - 1, Continuation = continuation },
                definition,
                SupportedHandler.Instance));

        Assert.Contains(exception.Errors, error => error.Code == "group_query.continuation.query_mismatch");
    }

    [Fact]
    public void Group_continuation_rejects_same_version_field_semantic_drift()
    {
        var definition = Definition(Profile());
        var query = Query();
        var continuation = new DiagnosticRecordGroupContinuation(
            new("42"),
            DiagnosticFieldValue.Timestamp(DateTimeOffset.Parse("2026-07-24T12:00:00Z")),
            "trace-a",
            DiagnosticRequestFingerprint.ForGroupQuery(query, definition));
        var changed = definition with
        {
            Fields = definition.Fields.Select(field =>
                field.Name == "status" ? field with { IsRequired = true } : field).ToArray()
        };

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQueryValidator.Validate(
                query with { Continuation = continuation },
                changed,
                SupportedHandler.Instance));

        Assert.Equal(definition.SchemaVersion, changed.SchemaVersion);
        Assert.Contains(exception.Errors, error => error.Code == "group_query.continuation.query_mismatch");
    }

    [Fact]
    public void Group_predicate_snapshot_enforces_the_iterative_node_bound()
    {
        DiagnosticRecordGroupPredicate predicate = Predicate(DiagnosticPredicateOperator.Equal, DiagnosticFieldValue.Int64(1));
        for (var index = 0; index < 64; index++)
            predicate = new DiagnosticRecordGroupPredicate.All([predicate]);

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQuerySnapshot.Capture(Query() with { Predicate = predicate }, 64));

        Assert.Contains(exception.Errors, error => error.Code == "group_query.predicate.too_complex");
    }

    [Fact]
    public void Group_predicates_enforce_value_bounds_arity_and_null_children()
    {
        var tooMany = Query() with
        {
            Predicate = Predicate(
                DiagnosticPredicateOperator.In,
                Enumerable.Range(0, 257).Select(value => DiagnosticFieldValue.Int64(value)).ToArray())
        };
        var tooManyException = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQueryValidator.Validate(tooMany, Definition(Profile()), SupportedHandler.Instance));

        var malformed = Query() with
        {
            Predicate = new DiagnosticRecordGroupPredicate.All(
            [
                null!,
                Predicate(
                    DiagnosticPredicateOperator.Equal,
                    DiagnosticFieldValue.Int64(1),
                    DiagnosticFieldValue.Int64(2))
            ])
        };
        var malformedException = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordGroupQueryValidator.Validate(malformed, Definition(Profile()), SupportedHandler.Instance));

        Assert.Contains(tooManyException.Errors, error => error.Code == "group_query.predicate.values.too_many");
        Assert.Contains(malformedException.Errors, error => error.Code == "group_query.predicate.child_null");
        Assert.Contains(malformedException.Errors, error => error.Code == "group_query.predicate.values.invalid");
    }

    [Fact]
    public void Group_query_snapshot_detaches_caller_owned_predicate_and_value_collections()
    {
        var values = new List<DiagnosticFieldValue> { DiagnosticFieldValue.Int64(2) };
        var children = new List<DiagnosticRecordGroupPredicate>
        {
            new DiagnosticRecordGroupPredicate.Comparison("status", DiagnosticPredicateOperator.Equal, values)
        };
        var captured = DiagnosticRecordGroupQuerySnapshot.Capture(
            Query() with { Predicate = new DiagnosticRecordGroupPredicate.All(children) },
            64);

        values[0] = DiagnosticFieldValue.Int64(99);
        children.Clear();

        var all = Assert.IsType<DiagnosticRecordGroupPredicate.All>(captured.Predicate);
        var comparison = Assert.IsType<DiagnosticRecordGroupPredicate.Comparison>(Assert.Single(all.Predicates));
        Assert.Equal(DiagnosticFieldValue.Int64(2), Assert.Single(comparison.Values));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DiagnosticRecordGroupPredicate>)all.Predicates).Add(comparison));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DiagnosticFieldValue>)comparison.Values).Add(DiagnosticFieldValue.Int64(3)));
    }

    [Fact]
    public void Group_query_fingerprint_is_deterministic_for_semantically_unordered_profile_declarations()
    {
        var first = Definition(Profile());
        var profile = Profile() with
        {
            Reducers = Profile().Reducers.Reverse().ToArray(),
            AllowedPredicates = Profile().AllowedPredicates.Reverse().ToArray(),
            OrderableAliases = new HashSet<string>(Profile().OrderableAliases.Reverse(), StringComparer.Ordinal)
        };
        var second = Definition(profile);

        Assert.Equal(
            DiagnosticRequestFingerprint.ForGroupQuery(Query(), first),
            DiagnosticRequestFingerprint.ForGroupQuery(Query(), second));
    }

    private static DiagnosticRecordGroupQuery Query() => new(
        new("tenant-a", "scope-a"),
        new("traces"),
        "trace-summary",
        25,
        new("start"),
        new DiagnosticRecordGroupPredicate.Comparison(
            "status",
            DiagnosticPredicateOperator.Equal,
            [DiagnosticFieldValue.Int64(2)]));

    private static DiagnosticRecordGroupPredicate.Comparison Predicate(
        DiagnosticPredicateOperator operation,
        params DiagnosticFieldValue[] values) =>
        new("status", operation, values);

    private static DiagnosticRecordStreamDefinition Definition(DiagnosticGroupReductionProfile profile) => new(
        new("traces"),
        1,
        "traces",
        [
            Field("traceId", DiagnosticFieldType.String, maxStringBytes: 128),
            Field("start", DiagnosticFieldType.Timestamp, orderable: true),
            Field("end", DiagnosticFieldType.Timestamp, orderable: true),
            Field("spanCount", DiagnosticFieldType.Int64),
            Field("status", DiagnosticFieldType.Int64),
            Field("tags", DiagnosticFieldType.String, DiagnosticFieldCardinality.Multiple, maxStringBytes: 64),
            Field("name", DiagnosticFieldType.String, maxStringBytes: 128)
        ],
        new(),
        TimeSpan.Zero,
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(1),
        GroupReductionProfiles: [profile]);

    private static DiagnosticFieldDefinition Field(
        string name,
        DiagnosticFieldType type,
        DiagnosticFieldCardinality cardinality = DiagnosticFieldCardinality.Scalar,
        bool orderable = false,
        int? maxStringBytes = null) => new(
        name,
        type,
        cardinality,
        type == DiagnosticFieldType.String
            ? new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.Contains }
            : new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.RangeInclusive },
        IsOrderable: orderable,
        MaxValues: cardinality == DiagnosticFieldCardinality.Multiple ? 16 : 1,
        MaxStringBytes: maxStringBytes);

    private static DiagnosticGroupReductionProfile Profile() => new(
        "trace-summary",
        "traceId",
        [
            new("start", DiagnosticGroupReducerKind.MinTimestamp, "start"),
            new("end", DiagnosticGroupReducerKind.MaxTimestamp, "end"),
            new("spanCount", DiagnosticGroupReducerKind.SumInt64, "spanCount"),
            new("tags", DiagnosticGroupReducerKind.SetUnionString, "tags"),
            new("status", DiagnosticGroupReducerKind.MaxInt64, "status"),
            new(
                "rootName",
                DiagnosticGroupReducerKind.FirstBy,
                "name",
                "start",
                DiagnosticSortDirection.Ascending,
                DiagnosticGroupFirstByTieBreak.CursorAscending)
        ],
        [
            new("status", new HashSet<DiagnosticPredicateOperator>
            {
                DiagnosticPredicateOperator.Equal,
                DiagnosticPredicateOperator.In,
                DiagnosticPredicateOperator.RangeInclusive
            }),
            new("tags", new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Contains })
        ],
        new HashSet<string> { "start", "end", "status" },
        100,
        64);

    private sealed class SupportedHandler : IDiagnosticGroupedQueryHandler
    {
        public static SupportedHandler Instance { get; } = new();
        public DiagnosticGroupedQueryHandlerCapabilities Capabilities { get; } = new(
            true,
            Enum.GetValues<DiagnosticGroupReducerKind>().ToHashSet(),
            Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(),
            true);

        public ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(DiagnosticRecordGroupQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
