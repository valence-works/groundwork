using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

public abstract class DiagnosticRecordContractTests
{
    [Fact]
    public void Scale_bearing_query_rejects_an_operator_the_executable_handler_cannot_run()
    {
        var definition = Definition();
        var handler = new StubQueryHandler(new(
            new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal },
            SupportsCursorOrder: true,
            SupportsFieldOrder: false,
            SupportsSnapshotContinuation: true,
            SupportsExactCount: true,
            SupportsLatestPerKey: false));
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            Limit: 10,
            Predicate: DiagnosticRecordPredicate.Contains("message", "timeout"));

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(query, definition, handler));

        Assert.Contains(exception.Errors, x => x.Code == "query.predicate.unsupported");
    }

    [Fact]
    public void Stream_definition_rejects_unbounded_string_fields()
    {
        var definition = Definition() with
        {
            Fields =
            [
                new DiagnosticFieldDefinition(
                    "message",
                    DiagnosticFieldType.String,
                    DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Contains })
            ]
        };

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition));

        Assert.Contains(exception.Errors, x => x.Code == "definition.field.string_bound.required");
    }

    [Fact]
    public void Portable_field_values_reject_invalid_canonical_forms_and_normalize_equivalent_values()
    {
        var normalized = new DiagnosticFieldValue(DiagnosticFieldType.Int64, "00042");

        Assert.Equal(DiagnosticFieldValue.Int64(42), normalized);
        Assert.Throws<ArgumentException>(() => new DiagnosticFieldValue(DiagnosticFieldType.Int64, "forty-two"));
        Assert.Throws<ArgumentException>(() => new DiagnosticFieldValue(DiagnosticFieldType.Timestamp, "2026-07-12T12:00:00"));
        Assert.True(DiagnosticFieldValue.Decimal(0.0000000000000000000000000001m)
            .CompareTo(DiagnosticFieldValue.Decimal(0.0000000000000000000000000002m), DiagnosticStringCasePolicy.Ordinal) < 0);
    }

    [Theory]
    [InlineData("API-Z9", "api-z9")]
    [InlineData("already lower", "already lower")]
    [InlineData("[]_@", "[]_@")]
    public void Ascii_ignore_case_uses_a_versioned_culture_independent_comparison_key(string value, string expected)
    {
        Assert.Equal("groundwork-ascii-lower-v1", DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId);
        Assert.Equal(expected, DiagnosticStringComparisonKey.CreateAsciiIgnoreCase(value));
    }

    [Theory]
    [InlineData("Å")]
    [InlineData("İ")]
    [InlineData("ß")]
    [InlineData("é")]
    [InlineData("line\nbreak")]
    public void Ascii_ignore_case_rejects_non_portable_unicode_and_control_values(string value)
    {
        Assert.False(DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(value));
        Assert.Throws<ArgumentException>(() => DiagnosticStringComparisonKey.CreateAsciiIgnoreCase(value));
    }

    [Fact]
    public void Diagnostic_record_contract_does_not_depend_on_document_or_operational_contracts()
    {
        var references = typeof(IDiagnosticRecordStore).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();

        Assert.DoesNotContain("Groundwork.Documents", references);
        Assert.DoesNotContain("Groundwork.Operational", references);
    }

    [Fact]
    public void Inclusive_range_rejects_reversed_bounds_during_validation()
    {
        var definition = Definition() with
        {
            Fields =
            [
                new DiagnosticFieldDefinition(
                    "sequence",
                    DiagnosticFieldType.Int64,
                    DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.RangeInclusive })
            ]
        };
        var handler = new StubQueryHandler(new(
            new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.RangeInclusive },
            true, true, true, true, true));
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.RangeInclusive("sequence", DiagnosticFieldValue.Int64(2), DiagnosticFieldValue.Int64(1)));

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(query, definition, handler));

        Assert.Contains(exception.Errors, x => x.Code == "query.predicate.range_reversed");
    }

    [Fact]
    public void Append_fingerprint_is_dictionary_order_independent_but_record_order_sensitive()
    {
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var stream = new DiagnosticStreamId("logs");
        var occurredAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var firstFields = new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
        {
            ["b"] = [DiagnosticFieldValue.String("two")],
            ["a"] = [DiagnosticFieldValue.String("one")]
        };
        var secondFields = new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
        {
            ["a"] = [DiagnosticFieldValue.String("one")],
            ["b"] = [DiagnosticFieldValue.String("two")]
        };
        var first = new DiagnosticRecordInput("one", occurredAt, "{}", firstFields);
        var equivalent = new DiagnosticRecordInput("one", occurredAt, "{}", secondFields);
        var second = new DiagnosticRecordInput("two", occurredAt, "{}", secondFields);

        Assert.Equal(
            DiagnosticRequestFingerprint.ForAppend(scope, stream, [first]),
            DiagnosticRequestFingerprint.ForAppend(scope, stream, [equivalent]));
        Assert.NotEqual(
            DiagnosticRequestFingerprint.ForAppend(scope, stream, [first, second]),
            DiagnosticRequestFingerprint.ForAppend(scope, stream, [second, first]));
    }

    [Fact]
    public void Operation_ids_beyond_the_declared_future_clock_skew_are_rejected()
    {
        var now = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var timeProvider = new TestTimeProvider(now);
        var definition = Definition();
        var batch = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            definition.Stream,
            new(now + definition.MaxOperationClockSkew + TimeSpan.FromTicks(1), "future-operation"),
            [new("record-1", now, "{}")]);

        Assert.Throws<DiagnosticOperationClockSkewException>(() =>
            DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(batch, definition, timeProvider.GetUtcNow()));
    }

    [Fact]
    public void Batch_factory_snapshots_mutable_record_and_field_collections()
    {
        var values = new List<DiagnosticFieldValue> { DiagnosticFieldValue.String("original") };
        var fields = new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["message"] = values };
        var records = new List<DiagnosticRecordInput>
        {
            new("record-1", DateTimeOffset.Parse("2026-07-12T12:00:00Z"), "{}", fields)
        };
        var batch = DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            new("logs"),
            new(TimeProvider.System.GetUtcNow(), "snapshot"),
            records);

        values[0] = DiagnosticFieldValue.String("mutated");
        fields["message"] = [DiagnosticFieldValue.String("replaced")];
        records.Clear();

        var record = Assert.Single(batch.Records);
        Assert.Equal(DiagnosticFieldValue.String("original"), Assert.Single(record.Fields!["message"]));
        Assert.Equal(batch.RequestFingerprint, DiagnosticRequestFingerprint.ForAppend(batch.Scope, batch.Stream, batch.Records));
    }

    [Fact]
    public void Stream_definition_snapshot_freezes_field_and_predicate_collections()
    {
        var predicates = new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal };
        var fields = new List<DiagnosticFieldDefinition>
        {
            new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar, predicates, MaxStringBytes: 32)
        };
        var definition = Definition() with { Fields = fields };

        var snapshot = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        predicates.Add(DiagnosticPredicateOperator.Contains);
        fields.Clear();

        var field = Assert.Single(snapshot.Fields);
        Assert.Equal([DiagnosticPredicateOperator.Equal], field.SupportedPredicates);
    }

    [Fact]
    public void Query_snapshot_freezes_nested_predicate_and_value_collections()
    {
        var values = new List<DiagnosticFieldValue> { DiagnosticFieldValue.String("original") };
        var children = new List<DiagnosticRecordPredicate>
        {
            new DiagnosticRecordPredicate.Comparison("message", DiagnosticPredicateOperator.Equal, values)
        };
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            new("logs"),
            10,
            Predicate: new DiagnosticRecordPredicate.All(children));

        var snapshot = DiagnosticRecordQuerySnapshot.Capture(query);
        values[0] = DiagnosticFieldValue.String("mutated");
        children.Clear();

        var all = Assert.IsType<DiagnosticRecordPredicate.All>(snapshot.Predicate);
        var comparison = Assert.IsType<DiagnosticRecordPredicate.Comparison>(Assert.Single(all.Predicates));
        Assert.Equal(DiagnosticFieldValue.String("original"), Assert.Single(comparison.Values));
    }

    [Fact]
    public async Task Conformance_deadline_fails_a_non_cooperative_handler_instead_of_hanging_the_runner()
    {
        var store = new BoundedDiagnosticRecordStore(
            new NonCooperativeStore(),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await store.QueryAsync(new(new("tenant-a", "shell-a"), new("logs"), 1)));
    }

    private static DiagnosticRecordStreamDefinition Definition() => new(
        new("logs"),
        SchemaVersion: 1,
        LogicalStorageName: "diagnostic_logs",
        Fields:
        [
            new(
                "message",
                DiagnosticFieldType.String,
                DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.Contains },
                MaxStringBytes: 256)
        ],
        Limits: new(),
        MaxOperationClockSkew: TimeSpan.FromMinutes(5),
        AppendIdempotencyWindow: TimeSpan.FromMinutes(10),
        TrimIdempotencyWindow: TimeSpan.FromMinutes(10));

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubQueryHandler(DiagnosticQueryHandlerCapabilities capabilities) : IDiagnosticQueryHandler
    {
        public DiagnosticQueryHandlerCapabilities Capabilities { get; } = capabilities;

        public ValueTask<DiagnosticRecordPage> QueryAsync(
            DiagnosticRecordQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This handler exists only to bind executable capability metadata in validator tests.");
    }

    private sealed class NonCooperativeStore :
        IDiagnosticRecordStore,
        IDiagnosticAppendHandler,
        IDiagnosticQueryHandler,
        IDiagnosticInspectHandler,
        IDiagnosticTrimHandler
    {
        public NonCooperativeStore()
        {
            Handlers = new(this, this, this, this);
        }

        public DiagnosticRecordStoreHandlers Handlers { get; }
        public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
            new HashSet<DiagnosticPredicateOperator>(), true, true, true, true, true);

        public ValueTask<DiagnosticAppendResult> AppendAsync(DiagnosticRecordBatch batch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DiagnosticRecordPage> QueryAsync(DiagnosticRecordQuery query, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource<DiagnosticRecordPage>(TaskCreationOptions.RunContinuationsAsynchronously).Task);

        public ValueTask<DiagnosticStreamStatistics> InspectAsync(DiagnosticStreamInspectionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DiagnosticTrimResult> TrimAsync(DiagnosticTrimRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
