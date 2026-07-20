using Groundwork.Core.Text;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

public abstract class DiagnosticRecordContractTests
{
    [Fact]
    public void Deployment_manifest_snapshots_streams_and_has_a_deterministic_combined_fingerprint()
    {
        var streams = new List<DiagnosticRecordStreamDefinition>
        {
            Definition() with { Stream = new("z") },
            Definition() with { Stream = new("a") }
        };

        var first = new DiagnosticRecordDeploymentManifest(StorageManifest(), streams);
        streams[0] = streams[0] with { LogicalStorageName = "mutated-after-capture" };
        var second = new DiagnosticRecordDeploymentManifest(StorageManifest(), streams.AsEnumerable().Reverse().ToArray());

        Assert.Equal(["a", "z"], first.Streams.Select(stream => stream.Stream.Value));
        Assert.NotEqual(first.DiagnosticFingerprint, second.DiagnosticFingerprint);
        Assert.Equal(first.DiagnosticFingerprint, new DiagnosticRecordDeploymentManifest(
            StorageManifest(),
            [Definition() with { Stream = new("a") }, Definition() with { Stream = new("z") }]).DiagnosticFingerprint);
    }

    [Fact]
    public void Deployment_manifest_rejects_duplicate_stream_identity()
    {
        var exception = Assert.Throws<DiagnosticRecordDeploymentManifestException>(() =>
            new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition(), Definition()]));

        Assert.Equal("deployment.stream.duplicate", exception.Code);
    }

    [Fact]
    public async Task Session_factory_admits_only_declared_streams_and_enforces_scope_isolation()
    {
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            ReadyInspector(),
            (_, _) => ValueTask.FromResult(new DiagnosticRecordStoreLease(new NonCooperativeStore())));
        await using var session = await factory.OpenAsync(deployment, new("tenant-a", "shell-a"));

        var store = await session.OpenStoreAsync(Definition().Stream);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.InspectAsync(new(new("tenant-b", "shell-a"), Definition().Stream)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.Handlers.Inspect.InspectAsync(new(new("tenant-b", "shell-a"), Definition().Stream)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.Handlers.Inspect.InspectAsync(new(session.Scope, new("other-stream"))));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.OpenStoreAsync(new("undeclared")));
    }

    [Fact]
    public async Task Admitting_session_factory_rejects_a_missing_deployment_before_the_store_delegate_can_run()
    {
        var opens = 0;
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            new StubDeploymentInspector(
                DiagnosticRecordDeploymentInspection.Missing("test", deployment, [Definition().Stream.Value])),
            (_, _) =>
            {
                Interlocked.Increment(ref opens);
                return ValueTask.FromResult(new DiagnosticRecordStoreLease(new NonCooperativeStore()));
            });

        var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
            await factory.OpenAsync(deployment, new("tenant-a", "shell-a")));

        Assert.Equal("GW-DIAG-DEPLOY-001", exception.Code);
        Assert.Equal(0, Volatile.Read(ref opens));
    }

    [Fact]
    public async Task Session_reinspects_before_first_store_open_and_never_invokes_the_open_delegate_after_drift()
    {
        var opens = 0;
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var inspector = new SequencedDeploymentInspector(
            DiagnosticRecordDeploymentInspection.Ready("test"),
            DiagnosticRecordDeploymentInspection.Drifted("test", [Definition().Stream.Value]));
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            inspector,
            (_, _) =>
            {
                Interlocked.Increment(ref opens);
                return ValueTask.FromResult(new DiagnosticRecordStoreLease(new NonCooperativeStore()));
            });
        await using var session = await factory.OpenAsync(deployment, new("tenant-a", "shell-a"));

        var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
            await session.OpenStoreAsync(Definition().Stream));

        Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        Assert.Equal(2, inspector.Calls);
        Assert.Equal(0, Volatile.Read(ref opens));
    }

    [Fact]
    public async Task Session_factory_classifies_inspector_failure_and_preserves_the_provider_exception()
    {
        var opens = 0;
        var failure = new InvalidOperationException("provider unavailable");
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            new ThrowingDeploymentInspector("test", failure),
            (_, _) =>
            {
                Interlocked.Increment(ref opens);
                return ValueTask.FromResult(new DiagnosticRecordStoreLease(new NonCooperativeStore()));
            });

        var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
            await factory.OpenAsync(deployment, new("tenant-a", "shell-a")));

        Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed, exception.Code);
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(0, Volatile.Read(ref opens));
    }

    [Fact]
    public void Deployment_inspection_snapshots_and_orders_stream_identities()
    {
        var streams = new List<string> { "z", "a", "z" };

        var inspection = new DiagnosticRecordDeploymentInspection(
            "test",
            DiagnosticRecordDeploymentAdmissionStatus.Missing,
            streams);
        streams.Clear();

        Assert.Equal(["a", "z"], inspection.MissingStreams);
    }

    [Fact]
    public async Task Session_factory_opens_each_stream_once_under_concurrency_and_disposes_one_lease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var resource = new CountingResource();
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            ReadyInspector(),
            async (_, _) =>
            {
                Interlocked.Increment(ref opens);
                entered.SetResult();
                await release.Task;
                return new DiagnosticRecordStoreLease(new NonCooperativeStore(), resource);
            });
        var session = await factory.OpenAsync(deployment, new("tenant-a", "shell-a"));

        var stores = Enumerable.Range(0, 16)
            .Select(_ => session.OpenStoreAsync(Definition().Stream).AsTask())
            .ToArray();
        await entered.Task;
        release.SetResult();
        await Task.WhenAll(stores);
        await session.DisposeAsync();

        Assert.Equal(1, opens);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Session_disposal_waits_for_an_in_flight_open_and_disposes_its_late_lease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new CountingResource();
        var deployment = new DiagnosticRecordDeploymentManifest(StorageManifest(), [Definition()]);
        var factory = new DelegatingDiagnosticRecordStoreSessionFactory(
            ReadyInspector(),
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return new DiagnosticRecordStoreLease(new NonCooperativeStore(), resource);
            });
        var session = await factory.OpenAsync(deployment, new("tenant-a", "shell-a"));
        var open = session.OpenStoreAsync(Definition().Stream).AsTask();
        await entered.Task;

        var dispose = session.DisposeAsync().AsTask();
        release.SetResult();
        await dispose;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => open);
        Assert.Equal(1, resource.DisposeCount);
    }

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
        var supplementary = DiagnosticFieldValue.String("\U00010000");
        var bmp = DiagnosticFieldValue.String("\uE000");
        Assert.True(supplementary.CompareTo(bmp, DiagnosticStringCasePolicy.Ordinal) < 0);
        Assert.Equal("D800DC00", DiagnosticStringComparisonKey.CreateOrdinal(supplementary.CanonicalValue));
        Assert.Throws<ArgumentException>(() => DiagnosticFieldValue.String("\uD800"));
        Assert.Equal("contains\0nul", DiagnosticFieldValue.String("contains\0nul").CanonicalValue);
    }

    [Theory]
    [InlineData(DiagnosticStringCasePolicy.Ordinal, PortableStringComparisonPolicy.Ordinal)]
    [InlineData(DiagnosticStringCasePolicy.AsciiIgnoreCase, PortableStringComparisonPolicy.AsciiIgnoreCase)]
    [InlineData(DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase, PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase)]
    public void Diagnostic_case_policy_is_a_thin_mapping_to_the_core_algorithm(
        DiagnosticStringCasePolicy diagnosticPolicy,
        PortableStringComparisonPolicy corePolicy)
    {
        const string value = "Groundwork";

        Assert.Equal(
            PortableStringComparison.Create(value, corePolicy),
            DiagnosticStringComparisonKey.Create(value, diagnosticPolicy));
        Assert.Equal(
            PortableStringComparison.ProjectIdentity(value, corePolicy).ComparisonKeyHash,
            DiagnosticStringComparisonKey.Project(value, diagnosticPolicy).ComparisonKeyHash);
    }

    [Fact]
    public void Diagnostic_search_key_preserves_unicode_scalar_boundaries()
    {
        var search = DiagnosticStringComparisonKey.CreateSearchKey(
            "xÅ😀y",
            DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase);

        Assert.Equal("groundwork-boundary-delimited-search-key-v1", DiagnosticStringComparisonKey.SearchKeyAlgorithmId);
        Assert.Contains("|0000C5|01F600", search, StringComparison.Ordinal);
    }

    [Fact]
    public void Maximum_diagnostic_unicode_projection_stays_within_the_declared_allocation_factor()
    {
        var value = new string('a', DiagnosticStringProjectionLimits.MaxInputUtf8Bytes);
        _ = DiagnosticStringComparisonKey.Project("warmup", DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var projection = DiagnosticStringComparisonKey.Project(
            value,
            DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(DiagnosticStringComparisonKey.CreateUnicodeOrdinalIgnoreCase(value), projection.ComparisonKey);
        Assert.Equal(
            DiagnosticStringComparisonKey.CreateSearchKey(value, DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase),
            projection.SearchKey);
        Assert.True(
            allocated <= (long)DiagnosticStringProjectionLimits.MaxInputUtf8Bytes * 48,
            $"Maximum projection allocated {allocated} bytes.");
    }

    [Fact]
    public void Physical_schema_state_is_deterministic_and_exposes_comparison_algorithm_drift()
    {
        var definition = Definition() with
        {
            Fields =
            [
                new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal },
                    CasePolicy: DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
                    MaxStringBytes: 8_192)
            ],
            Limits = Definition().Limits with { MaxPredicateValues = 16 }
        };
        var first = DiagnosticRecordPhysicalSchemaState.Capture(definition);
        var reorderedPredicates = definition with
        {
            Fields = [definition.Fields[0] with { SupportedPredicates = new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal } }]
        };
        var second = DiagnosticRecordPhysicalSchemaState.Capture(reorderedPredicates);

        Assert.Equal(first, second);
        Assert.Contains(DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId, first.ComparisonAlgorithmManifest, StringComparison.Ordinal);
        Assert.Contains(DiagnosticStringComparisonKey.LookupHashAlgorithmId, first.ComparisonAlgorithmManifest, StringComparison.Ordinal);
        Assert.Contains(DiagnosticStringComparisonKey.SearchKeyAlgorithmId, first.ComparisonAlgorithmManifest, StringComparison.Ordinal);
        Assert.Contains("\"comparisonAlgorithms\"", first.CanonicalDefinition, StringComparison.Ordinal);
        Assert.Equal(64, first.DefinitionFingerprint.Length);
        Assert.Equal(64, first.ComparisonAlgorithmManifestFingerprint.Length);
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
        definition = Definition() with { Limits = new(MaxPredicateValues: 2) };
        handler = new StubQueryHandler(new(
            Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(), true, true, true, true, true));
        query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.In(
                "message",
                DiagnosticFieldValue.String("one"),
                DiagnosticFieldValue.String("two"),
                DiagnosticFieldValue.String("three")));

        exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(query, definition, handler));

        Assert.Contains(exception.Errors, x => x.Code == "query.predicate.values.too_many");
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

        var snapshot = DiagnosticRecordQuerySnapshot.Capture(query, Definition().Limits.MaxPredicateNodes);
        values[0] = DiagnosticFieldValue.String("mutated");
        children.Clear();

        var all = Assert.IsType<DiagnosticRecordPredicate.All>(snapshot.Predicate);
        var comparison = Assert.IsType<DiagnosticRecordPredicate.Comparison>(Assert.Single(all.Predicates));
        Assert.Equal(DiagnosticFieldValue.String("original"), Assert.Single(comparison.Values));
    }

    [Fact]
    public void Deep_and_wide_over_limit_predicates_are_rejected_before_recursive_work()
    {
        var definition = Definition();
        var handler = QueryHandler(DiagnosticPredicateOperator.Equal);
        DiagnosticRecordPredicate deep = DiagnosticRecordPredicate.Equal(
            "message",
            DiagnosticFieldValue.String("value"));
        for (var index = 0; index < 10_000; index++)
            deep = new DiagnosticRecordPredicate.All([deep]);
        var wide = new DiagnosticRecordPredicate.All(
            Enumerable.Range(0, definition.Limits.MaxPredicateNodes + 1)
                .Select(_ => DiagnosticRecordPredicate.Equal(
                    "message",
                    DiagnosticFieldValue.String("value")))
                .ToArray());
        DiagnosticRecordQuery Query(DiagnosticRecordPredicate predicate) => new(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: predicate);

        foreach (var query in new[] { Query(deep), Query(wide) })
        {
            var validationException = Assert.Throws<DiagnosticRecordValidationException>(() =>
                DiagnosticRecordQueryValidator.Validate(query, definition, handler));
            var snapshotException = Assert.Throws<DiagnosticRecordValidationException>(() =>
                DiagnosticRecordQuerySnapshot.Capture(query, definition.Limits.MaxPredicateNodes));

            Assert.Contains(validationException.Errors, error => error.Code == "query.predicate.too_complex");
            Assert.Contains(snapshotException.Errors, error => error.Code == "query.predicate.too_complex");
        }
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

    [Fact]
    public void Definition_projection_budgets_accept_the_boundary_and_reject_cap_plus_one_and_overflow()
    {
        var boundary = Definition() with
        {
            Fields =
            [
                Definition().Fields[0] with
                {
                    MaxStringBytes = DiagnosticStringProjectionLimits.MaxInputUtf8Bytes
                }
            ],
            Limits = Definition().Limits with { MaxPredicateValues = 9 }
        };

        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(boundary);
        var overQuery = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(
                boundary with { Limits = boundary.Limits with { MaxPredicateValues = 10 } }));
        var overflow = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(boundary with
            {
                Fields =
                [
                    boundary.Fields[0] with
                    {
                        Cardinality = DiagnosticFieldCardinality.Multiple,
                        MaxValues = int.MaxValue
                    }
                ]
            }));

        Assert.Contains(overQuery.Errors, error => error.Code == "definition.projection.query_budget.exceeded");
        Assert.Contains(overflow.Errors, error => error.Code == "definition.projection.record_budget.exceeded");
    }

    [Fact]
    public void Append_projection_budget_rejects_many_individually_bounded_values_before_provider_io()
    {
        const int valueBytes = 1_024;
        var definition = Definition() with
        {
            Fields = [Definition().Fields[0] with { MaxStringBytes = valueBytes }],
            Limits = Definition().Limits with { MaxBatchRecords = 683 }
        };
        var value = DiagnosticFieldValue.String(new string('a', valueBytes));
        DiagnosticRecordBatch Batch(int count) => DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            definition.Stream,
            new(TimeProvider.System.GetUtcNow(), $"aggregate-{count}"),
            Enumerable.Range(0, count).Select(index => new DiagnosticRecordInput(
                $"record-{index}",
                TimeProvider.System.GetUtcNow(),
                "{}",
                new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["message"] = [value] })).ToArray());

        DiagnosticRecordRequestValidator.Validate(Batch(682), definition);
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordRequestValidator.Validate(Batch(683), definition));

        Assert.Contains(exception.Errors, error => error.Code == "append.projection_budget.exceeded");
    }

    [Fact]
    public void Query_projection_budget_rejects_many_individually_bounded_values_before_provider_io()
    {
        const int valueBytes = 1_024;
        var predicates = new HashSet<DiagnosticPredicateOperator>
        {
            DiagnosticPredicateOperator.Equal,
            DiagnosticPredicateOperator.In,
            DiagnosticPredicateOperator.Contains
        };
        var definition = Definition() with
        {
            Fields = [Definition().Fields[0] with { SupportedPredicates = predicates, MaxStringBytes = valueBytes }],
            Limits = Definition().Limits with { MaxPredicateValues = 683 }
        };
        var value = DiagnosticFieldValue.String(new string('a', valueBytes));
        var handler = new StubQueryHandler(new(
            predicates,
            SupportsCursorOrder: true,
            SupportsFieldOrder: true,
            SupportsSnapshotContinuation: true,
            SupportsExactCount: true,
            SupportsLatestPerKey: true));
        DiagnosticRecordQuery Query(int count) => new(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.In("message", Enumerable.Repeat(value, count).ToArray()));

        DiagnosticRecordQueryValidator.Validate(Query(682), definition, handler);
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(Query(683), definition, handler));

        Assert.Contains(exception.Errors, error => error.Code == "query.projection_budget.exceeded");
    }

    [Fact]
    public void Query_and_continuation_string_bounds_accept_the_boundary_and_reject_cap_plus_one()
    {
        var definition = Definition() with
        {
            Fields =
            [
                Definition().Fields[0] with
                {
                    IsOrderable = true,
                    MaxStringBytes = 4
                }
            ]
        };
        var handler = QueryHandler(DiagnosticPredicateOperator.Equal);
        var boundary = DiagnosticFieldValue.String("😀");
        var overBoundary = DiagnosticFieldValue.String("😀a");
        var predicateQuery = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Equal("message", boundary));

        DiagnosticRecordQueryValidator.Validate(predicateQuery, definition, handler);
        var predicateException = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(
                predicateQuery with { Predicate = DiagnosticRecordPredicate.Equal("message", overBoundary) },
                definition,
                handler));

        var orderedQuery = predicateQuery with { Predicate = null, Order = new("message") };
        var fingerprint = DiagnosticRequestFingerprint.ForQuery(orderedQuery, definition);
        DiagnosticRecordQuery WithContinuation(DiagnosticFieldValue value) => orderedQuery with
        {
            Continuation = new(new("1"), new("1"), fingerprint, value)
        };
        DiagnosticRecordQueryValidator.Validate(WithContinuation(boundary), definition, handler);
        var continuationException = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(WithContinuation(overBoundary), definition, handler));

        Assert.Contains(predicateException.Errors, error => error.Code == "query.predicate.string_too_large");
        Assert.Contains(continuationException.Errors, error => error.Code == "query.continuation.string_too_large");
    }

    [Fact]
    public void Oversized_unicode_range_is_budgeted_before_comparison_keys_are_created()
    {
        const int declaredBytes = 200_000;
        var definition = Definition() with
        {
            Fields =
            [
                Definition().Fields[0] with
                {
                    SupportedPredicates = new HashSet<DiagnosticPredicateOperator>
                    {
                        DiagnosticPredicateOperator.RangeInclusive
                    },
                    CasePolicy = DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
                    MaxStringBytes = declaredBytes
                }
            ],
            Limits = Definition().Limits with { MaxPredicateValues = 2 }
        };
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.RangeInclusive(
                "message",
                DiagnosticFieldValue.String(new string('z', 350_000)),
                DiagnosticFieldValue.String(new string('a', 350_000))));

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(
                query,
                definition,
                QueryHandler(DiagnosticPredicateOperator.RangeInclusive)));

        Assert.Contains(exception.Errors, error => error.Code == "query.projection_budget.exceeded");
        Assert.Contains(exception.Errors, error => error.Code == "query.predicate.string_too_large");
        Assert.DoesNotContain(exception.Errors, error => error.Code == "query.predicate.range_reversed");
    }

    [Fact]
    public void Uninitialized_field_order_continuation_is_a_validation_error()
    {
        var definition = Definition() with
        {
            Fields = [Definition().Fields[0] with { IsOrderable = true }]
        };
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            definition.Stream,
            10,
            Order: new("message"));
        query = query with
        {
            Continuation = new(
                new("1"),
                new("1"),
                DiagnosticRequestFingerprint.ForQuery(query, definition),
                default(DiagnosticFieldValue))
        };

        var exception = Assert.Throws<DiagnosticRecordValidationException>(() =>
            DiagnosticRecordQueryValidator.Validate(
                query,
                definition,
                QueryHandler(DiagnosticPredicateOperator.Equal)));

        Assert.Contains(exception.Errors, error => error.Code == "query.continuation.order_value.invalid");
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

    private static StorageManifest StorageManifest() => new(
        new("diagnostic-deployment-tests"),
        new("tests"),
        new("1"),
        [StorageUnit.Create(
            new("documents"),
            "Documents",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default()))],
        new HashSet<string>(),
        []);

    private static StubQueryHandler QueryHandler(params DiagnosticPredicateOperator[] predicates) => new(new(
        predicates.ToHashSet(),
        SupportsCursorOrder: true,
        SupportsFieldOrder: true,
        SupportsSnapshotContinuation: true,
        SupportsExactCount: true,
        SupportsLatestPerKey: true));

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

    private sealed class CountingResource : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubDeploymentInspector(DiagnosticRecordDeploymentInspection inspection)
        : IDiagnosticRecordDeploymentInspector
    {
        public string Provider => inspection.Provider;

        public ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
            DiagnosticRecordDeploymentManifest deployment,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(inspection);
    }

    private sealed class ThrowingDeploymentInspector(string provider, Exception failure)
        : IDiagnosticRecordDeploymentInspector
    {
        public string Provider { get; } = provider;

        public ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
            DiagnosticRecordDeploymentManifest deployment,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DiagnosticRecordDeploymentInspection>(failure);
    }

    private sealed class SequencedDeploymentInspector(
        params DiagnosticRecordDeploymentInspection[] inspections)
        : IDiagnosticRecordDeploymentInspector
    {
        private int calls;

        public string Provider => inspections[0].Provider;
        public int Calls => Volatile.Read(ref calls);

        public ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
            DiagnosticRecordDeploymentManifest deployment,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return ValueTask.FromResult(inspections[Math.Min(index, inspections.Length - 1)]);
        }
    }

    private static IDiagnosticRecordDeploymentInspector ReadyInspector() =>
        new StubDeploymentInspector(DiagnosticRecordDeploymentInspection.Ready("test"));
}
