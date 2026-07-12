using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Groundwork.DiagnosticRecords;
using Xunit;

namespace Groundwork.DiagnosticRecords.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticRecordInstrumentationCollection
{
    public const string Name = "Diagnostic record instrumentation";
}

[Collection(DiagnosticRecordInstrumentationCollection.Name)]
public sealed class DiagnosticRecordInstrumentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
    private static readonly DiagnosticStorageScope Scope = new("tenant-secret", "scope-secret");
    private static readonly DiagnosticStreamId Stream = new("audit-stream");
    private static readonly DiagnosticRecordTelemetryIdentity Identity = new("test-provider", "diagnostic-records");

    [Fact]
    public async Task Successful_operations_publish_the_versioned_bounded_contract()
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(new StubStore { CompleteAsynchronously = true }, Identity);

        await store.AppendAsync(Batch());
        await store.QueryAsync(new(Scope, Stream, 25, IncludeExactCount: true, LatestPerKeyField: "latest-key-secret"));
        await store.InspectAsync(new(Scope, Stream));
        await store.TrimAsync(Trim());

        Assert.Equal("Groundwork.DiagnosticRecords", DiagnosticRecordTelemetry.ActivitySourceName);
        Assert.Equal("Groundwork.DiagnosticRecords", DiagnosticRecordTelemetry.MeterName);
        Assert.Equal("1.0.0", DiagnosticRecordTelemetry.Version);
        Assert.Equal(
            ["groundwork.diagnostic_records.append", "groundwork.diagnostic_records.query", "groundwork.diagnostic_records.inspect", "groundwork.diagnostic_records.trim"],
            capture.Activities.Select(x => x.OperationName));
        Assert.All(capture.Activities, activity =>
        {
            Assert.Equal(ActivityStatusCode.Ok, activity.Status);
            Assert.Equal("test-provider", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Provider));
            Assert.Equal("diagnostic-records", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Store));
            Assert.Equal("audit-stream", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Stream));
            Assert.Equal("tenant_scope", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.ScopeKind));
            Assert.Equal(true, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.ScopePresent));
        });
        AssertActivityOutcome(capture.Activities[0], "committed", "success");
        AssertActivityOutcome(capture.Activities[1], "success", "success");
        AssertActivityOutcome(capture.Activities[2], "success", "success");
        AssertActivityOutcome(capture.Activities[3], "completed", "success");

        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.AppendBatches, 1L,
            (DiagnosticRecordTelemetry.Tags.Disposition, DiagnosticRecordTelemetry.Dispositions.Accepted));
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.RecordsAppended, 2L);
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.ExactCountRequests, 1L);
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.LatestPerKeyRequests, 1L);
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.TrimExaminedRecords, 9L);
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.TrimDeletedRecords, 4L);
        Assert.Equal(2, capture.Measurements.Count(x =>
            x.Name == DiagnosticRecordTelemetry.Instruments.RetainedRecords && Equals(x.Value, 5L)));
        Assert.Equal(4, capture.Measurements.Count(x => x.Name == DiagnosticRecordTelemetry.Instruments.OperationDuration));
        Assert.All(capture.Measurements, measurement =>
        {
            Assert.Equal("test-provider", measurement.Tags[DiagnosticRecordTelemetry.Tags.Provider]);
            Assert.Equal("diagnostic-records", measurement.Tags[DiagnosticRecordTelemetry.Tags.Store]);
            Assert.DoesNotContain(DiagnosticRecordTelemetry.Tags.Stream, measurement.Tags.Keys);
        });

        var serializedTelemetry = string.Join('|', capture.Activities.SelectMany(x => x.TagObjects)
            .Concat(capture.Measurements.SelectMany(x => x.Tags))
            .Select(x => $"{x.Key}={x.Value}"));
        Assert.DoesNotContain("tenant-secret", serializedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("scope-secret", serializedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-secret", serializedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("record-secret", serializedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce-secret", serializedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("latest-key-secret", serializedTelemetry, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Append_failures_have_deterministic_outcomes(
        Exception failure,
        string outcome,
        string classification)
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(new StubStore { Failure = failure }, Identity);

        await Assert.ThrowsAsync(failure.GetType(), async () => await store.AppendAsync(Batch()));

        var activity = Assert.Single(capture.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        AssertActivityOutcome(activity, outcome, classification);
        var disposition = classification is "rejection" or "conflict"
            ? DiagnosticRecordTelemetry.Dispositions.Rejected
            : DiagnosticRecordTelemetry.Dispositions.Indeterminate;
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.AppendBatches, 1L,
            (DiagnosticRecordTelemetry.Tags.Disposition, disposition));
        AssertMeasurement(capture, DiagnosticRecordTelemetry.Instruments.OperationOutcomes, 1L,
            (DiagnosticRecordTelemetry.Tags.Outcome, outcome),
            (DiagnosticRecordTelemetry.Tags.Classification, classification));
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == "exception.message");
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Every_operation_classifies_provider_failures(string operation)
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { Failure = new IOException("payload-secret"), CompleteAsynchronously = true },
            Identity);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            switch (operation)
            {
                case "append": await store.AppendAsync(Batch()); break;
                case "query": await store.QueryAsync(new(Scope, Stream, 1)); break;
                case "inspect": await store.InspectAsync(new(Scope, Stream)); break;
                case "trim": await store.TrimAsync(Trim()); break;
            }
        });

        var activity = Assert.Single(capture.Activities);
        AssertActivityOutcome(activity, "provider_failure", "provider_failure");
        Assert.DoesNotContain("payload-secret", string.Join('|', activity.TagObjects.Select(x => x.Value)));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public void Enabled_listeners_preserve_synchronous_provider_throws(string operation)
    {
        using var capture = new TelemetryCapture();
        var failure = new IOException("payload-secret");
        var store = new InstrumentedDiagnosticRecordStore(new StubStore { Failure = failure }, Identity);
        using var parent = new Activity("parent").Start();

        var observed = Record.Exception(() => InvokeWithoutAwait(store, operation));

        Assert.Same(failure, observed);
        Assert.Same(parent, Activity.Current);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(parent.Id, activity.ParentId);
        AssertActivityOutcome(activity, DiagnosticRecordTelemetry.Outcomes.ProviderFailure,
            DiagnosticRecordTelemetry.Classifications.ProviderFailure);
        Assert.Single(capture.Measurements, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public void Disabled_listeners_preserve_synchronous_provider_throws(string operation)
    {
        var failure = new IOException("payload-secret");
        var store = new InstrumentedDiagnosticRecordStore(new StubStore { Failure = failure }, Identity);

        var observed = Record.Exception(() => InvokeWithoutAwait(store, operation));

        Assert.Same(failure, observed);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public void Enabled_listeners_preserve_synchronous_null_request_validation(string operation)
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(new StubStore(), Identity);
        using var parent = new Activity("parent").Start();

        var observed = Record.Exception(() => InvokeWithoutAwait(store, operation, useNullRequest: true));

        Assert.IsType<ArgumentNullException>(observed);
        Assert.Same(parent, Activity.Current);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(parent.Id, activity.ParentId);
        AssertActivityOutcome(activity, DiagnosticRecordTelemetry.Outcomes.Rejected,
            DiagnosticRecordTelemetry.Classifications.Rejection);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == DiagnosticRecordTelemetry.Tags.Stream);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public void Disabled_listeners_preserve_synchronous_null_request_validation(string operation)
    {
        var store = new InstrumentedDiagnosticRecordStore(new StubStore(), Identity);

        var observed = Record.Exception(() => InvokeWithoutAwait(store, operation, useNullRequest: true));

        Assert.IsType<ArgumentNullException>(observed);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Completed_failed_ValueTasks_remain_awaitable_instead_of_throwing_synchronously(string operation)
    {
        using var capture = new TelemetryCapture();
        var failure = new IOException("payload-secret");
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { Failure = failure, ReturnCompletedFailure = true },
            Identity);
        using var parent = new Activity("parent").Start();
        Task? pending = null;

        var synchronous = Record.Exception(() => { pending = InvokeAsTask(store, operation); });
        Assert.Same(parent, Activity.Current);
        var observed = await Record.ExceptionAsync(() => pending!);

        Assert.Null(synchronous);
        Assert.Same(failure, observed);
        Assert.Same(parent, Activity.Current);
        Assert.Equal(parent.Id, Assert.Single(capture.Activities).ParentId);
        Assert.Single(capture.Measurements, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Completed_canceled_ValueTasks_remain_awaitable_and_canceled(string operation)
    {
        using var capture = new TelemetryCapture();
        var cancellationToken = new CancellationToken(canceled: true);
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { CompletedCancellationToken = cancellationToken },
            Identity);
        using var parent = new Activity("parent").Start();
        Task? pending = null;

        var synchronous = Record.Exception(() => { pending = InvokeAsTask(store, operation); });
        Assert.Same(parent, Activity.Current);
        var observed = await Record.ExceptionAsync(() => pending!);

        Assert.Null(synchronous);
        Assert.True(pending!.IsCanceled);
        Assert.Equal(cancellationToken, Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Same(parent, Activity.Current);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(parent.Id, activity.ParentId);
        AssertActivityOutcome(activity, DiagnosticRecordTelemetry.Outcomes.Cancelled,
            DiagnosticRecordTelemetry.Classifications.Cancellation);
        Assert.Single(capture.Measurements, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Completed_canceled_ValueTasks_without_a_requested_token_preserve_canceled_status(string operation)
    {
        var withoutTelemetry = await ObserveCompletedCancellationAsync(operation);
        using var capture = new TelemetryCapture();

        var withTelemetry = await ObserveCompletedCancellationAsync(operation);

        Assert.Equal(withoutTelemetry, withTelemetry);
        Assert.True(withTelemetry.IsCanceled);
        Assert.False(withTelemetry.Synchronous);
        Assert.True(typeof(OperationCanceledException).IsAssignableFrom(withTelemetry.ExceptionType));
        var activity = Assert.Single(capture.Activities);
        AssertActivityOutcome(activity, DiagnosticRecordTelemetry.Outcomes.Cancelled,
            DiagnosticRecordTelemetry.Classifications.Cancellation);
        Assert.Single(capture.Measurements, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Completed_operations_restore_the_callers_ambient_activity(string operation)
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(new StubStore(), Identity);
        using var parent = new Activity("parent").Start();

        var pending = InvokeAsTask(store, operation);

        Assert.Same(parent, Activity.Current);
        await pending;
        Assert.Same(parent, Activity.Current);
        Assert.Equal(parent.Id, Assert.Single(capture.Activities).ParentId);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("query")]
    [InlineData("inspect")]
    [InlineData("trim")]
    public async Task Incomplete_operations_restore_the_callers_ambient_activity(string operation)
    {
        using var capture = new TelemetryCapture();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { Completion = completion.Task },
            Identity);
        using var parent = new Activity("parent").Start();

        var pending = InvokeAsTask(store, operation);

        Assert.Same(parent, Activity.Current);
        using (var sibling = new Activity("sibling").Start())
            Assert.Equal(parent.Id, sibling.ParentId);

        completion.SetResult();
        await pending;

        Assert.Same(parent, Activity.Current);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(parent.Id, activity.ParentId);
    }

    [Fact]
    public async Task Replays_are_observable_without_double_counting_committed_records()
    {
        using var capture = new TelemetryCapture();
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { AppendStatus = DiagnosticAppendStatus.Replayed, TrimStatus = DiagnosticTrimStatus.Replayed },
            Identity);

        await store.AppendAsync(Batch());
        await store.TrimAsync(Trim());

        Assert.DoesNotContain(capture.Measurements, x => x.Name == DiagnosticRecordTelemetry.Instruments.RecordsAppended);
        Assert.DoesNotContain(capture.Measurements, x => x.Name is
            DiagnosticRecordTelemetry.Instruments.TrimExaminedRecords or
            DiagnosticRecordTelemetry.Instruments.TrimDeletedRecords or
            DiagnosticRecordTelemetry.Instruments.RetainedRecords);
        Assert.Equal(2, capture.Measurements.Count(x =>
            x.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes &&
            Equals(x.Tags[DiagnosticRecordTelemetry.Tags.Classification], "replay")));
    }

    [Fact]
    public async Task Disabled_listeners_preserve_the_completed_ValueTask_fast_path()
    {
        var inner = new StubStore();
        var store = new InstrumentedDiagnosticRecordStore(inner, Identity);

        var operation = store.AppendAsync(Batch());

        Assert.True(operation.IsCompletedSuccessfully);
        Assert.Equal(DiagnosticAppendStatus.Committed, (await operation).Status);
        Assert.Equal(1, inner.AppendCalls);
        Assert.All(
            [nameof(store.AppendAsync), nameof(store.QueryAsync), nameof(store.InspectAsync), nameof(store.TrimAsync)],
            methodName => Assert.Null(typeof(InstrumentedDiagnosticRecordStore).GetMethod(methodName)!
                .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("TENANT/value")]
    public void Telemetry_identity_rejects_unbounded_or_unsafe_values(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DiagnosticRecordTelemetryIdentity(value, "store"));
        Assert.ThrowsAny<ArgumentException>(() => new DiagnosticRecordTelemetryIdentity("provider", value));
    }

    public static TheoryData<Exception, string, string> Failures => new()
    {
        { new DiagnosticRecordValidationException([new("invalid", "payload-secret", "payload")]), "rejected", "rejection" },
        { new DiagnosticRequestFingerprintMismatchException(DiagnosticOperationKind.Append), "rejected", "rejection" },
        { new DiagnosticOperationExpiredException(DiagnosticOperationKind.Append, OperationId()), "rejected", "rejection" },
        { new DiagnosticOperationClockSkewException(DiagnosticOperationKind.Append, OperationId(), TimeSpan.FromMinutes(1)), "rejected", "rejection" },
        { new DiagnosticOperationConflictException(DiagnosticOperationKind.Append, OperationId()), "conflict", "conflict" },
        { new DiagnosticAcknowledgementLostException(DiagnosticOperationKind.Append, Stream, OperationId()), "acknowledgement_lost", "acknowledgement_loss" },
        { new OperationCanceledException(), "cancelled", "cancellation" },
        { new IOException("payload-secret"), "provider_failure", "provider_failure" }
    };

    private static DiagnosticRecordBatch Batch() => DiagnosticRecordBatch.Create(
        Scope,
        Stream,
        OperationId(),
        [
            new("record-secret-1", Now, "{\"value\":\"payload-secret\"}"),
            new("record-secret-2", Now, "{\"value\":\"payload-secret\"}")
        ]);

    private static DiagnosticTrimRequest Trim() => DiagnosticTrimRequest.Create(Scope, Stream, OperationId(), 5);

    private static DiagnosticOperationId OperationId() => new(Now, "nonce-secret");

    private static void InvokeWithoutAwait(
        InstrumentedDiagnosticRecordStore store,
        string operation,
        bool useNullRequest = false)
    {
        switch (operation)
        {
            case "append":
                _ = store.AppendAsync(useNullRequest ? null! : Batch());
                break;
            case "query":
                _ = store.QueryAsync(useNullRequest ? null! : new(Scope, Stream, 1));
                break;
            case "inspect":
                _ = store.InspectAsync(useNullRequest ? null! : new(Scope, Stream));
                break;
            case "trim":
                _ = store.TrimAsync(useNullRequest ? null! : Trim());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static Task InvokeAsTask(InstrumentedDiagnosticRecordStore store, string operation) => operation switch
    {
        "append" => store.AppendAsync(Batch()).AsTask(),
        "query" => store.QueryAsync(new(Scope, Stream, 1)).AsTask(),
        "inspect" => store.InspectAsync(new(Scope, Stream)).AsTask(),
        "trim" => store.TrimAsync(Trim()).AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task<CancellationObservation> ObserveCompletedCancellationAsync(string operation)
    {
        var store = new InstrumentedDiagnosticRecordStore(
            new StubStore { ReturnCompletedCancellationWithoutToken = true },
            Identity);
        Task? pending = null;
        var synchronous = Record.Exception(() => { pending = InvokeAsTask(store, operation); });
        var observed = synchronous ?? await Record.ExceptionAsync(() => pending!);
        var cancellationToken = observed is OperationCanceledException canceled
            ? canceled.CancellationToken
            : default;
        return new(observed!.GetType(), pending?.IsCanceled == true, synchronous is not null, cancellationToken);
    }

    private static void AssertActivityOutcome(Activity activity, string outcome, string classification)
    {
        Assert.Equal(outcome, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Outcome));
        Assert.Equal(classification, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Classification));
    }

    private static void AssertMeasurement(
        TelemetryCapture capture,
        string name,
        object value,
        params (string Key, object Value)[] tags)
    {
        Assert.Contains(capture.Measurements, measurement =>
            measurement.Name == name &&
            Equals(measurement.Value, value) &&
            tags.All(tag => measurement.Tags.TryGetValue(tag.Key, out var actual) && Equals(actual, tag.Value)));
    }

    private sealed class StubStore :
        IDiagnosticRecordStore,
        IDiagnosticAppendHandler,
        IDiagnosticQueryHandler,
        IDiagnosticInspectHandler,
        IDiagnosticTrimHandler
    {
        public StubStore() => Handlers = new(this, this, this, this);

        public Exception? Failure { get; init; }
        public DiagnosticAppendStatus AppendStatus { get; init; } = DiagnosticAppendStatus.Committed;
        public DiagnosticTrimStatus TrimStatus { get; init; } = DiagnosticTrimStatus.Completed;
        public bool CompleteAsynchronously { get; init; }
        public bool ReturnCompletedFailure { get; init; }
        public CancellationToken? CompletedCancellationToken { get; init; }
        public bool ReturnCompletedCancellationWithoutToken { get; init; }
        public Task? Completion { get; init; }
        public int AppendCalls { get; private set; }
        public DiagnosticRecordStoreHandlers Handlers { get; }
        public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
            Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(), true, true, true, true, true);

        public ValueTask<DiagnosticAppendResult> AppendAsync(DiagnosticRecordBatch batch, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            AppendCalls++;
            if (ReturnCompletedCancellationWithoutToken)
                return CompleteCanceledWithoutToken<DiagnosticAppendResult>();
            if (CompletedCancellationToken is { } configuredCancellation)
                return ValueTask.FromCanceled<DiagnosticAppendResult>(configuredCancellation);
            if (ReturnCompletedFailure)
                return ValueTask.FromException<DiagnosticAppendResult>(Failure!);
            if (CompleteAsynchronously && Failure is not null)
                return FailAsync<DiagnosticAppendResult>(Failure);
            ThrowIfConfigured();
            var records = batch.Records.Select((x, index) => new DiagnosticRecord(
                x.RecordId, x.OccurredAt, x.Payload, new((index + 1).ToString()), x.Fields)).ToArray();
            var result = new DiagnosticAppendResult(AppendStatus, records, new("2"), null);
            if (Completion is not null)
                return CompleteAfterAsync(Completion, result);
            return CompleteAsynchronously ? CompleteAsync(result) : ValueTask.FromResult(result);
        }

        public ValueTask<DiagnosticRecordPage> QueryAsync(DiagnosticRecordQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (ReturnCompletedCancellationWithoutToken)
                return CompleteCanceledWithoutToken<DiagnosticRecordPage>();
            if (CompletedCancellationToken is { } configuredCancellation)
                return ValueTask.FromCanceled<DiagnosticRecordPage>(configuredCancellation);
            if (ReturnCompletedFailure)
                return ValueTask.FromException<DiagnosticRecordPage>(Failure!);
            if (CompleteAsynchronously && Failure is not null)
                return FailAsync<DiagnosticRecordPage>(Failure);
            ThrowIfConfigured();
            var result = new DiagnosticRecordPage([], null, query.IncludeExactCount ? 5 : null);
            if (Completion is not null)
                return CompleteAfterAsync(Completion, result);
            return CompleteAsynchronously ? CompleteAsync(result) : ValueTask.FromResult(result);
        }

        public ValueTask<DiagnosticStreamStatistics> InspectAsync(DiagnosticStreamInspectionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (ReturnCompletedCancellationWithoutToken)
                return CompleteCanceledWithoutToken<DiagnosticStreamStatistics>();
            if (CompletedCancellationToken is { } configuredCancellation)
                return ValueTask.FromCanceled<DiagnosticStreamStatistics>(configuredCancellation);
            if (ReturnCompletedFailure)
                return ValueTask.FromException<DiagnosticStreamStatistics>(Failure!);
            if (CompleteAsynchronously && Failure is not null)
                return FailAsync<DiagnosticStreamStatistics>(Failure);
            ThrowIfConfigured();
            var result = Statistics();
            if (Completion is not null)
                return CompleteAfterAsync(Completion, result);
            return CompleteAsynchronously ? CompleteAsync(result) : ValueTask.FromResult(result);
        }

        public ValueTask<DiagnosticTrimResult> TrimAsync(DiagnosticTrimRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (ReturnCompletedCancellationWithoutToken)
                return CompleteCanceledWithoutToken<DiagnosticTrimResult>();
            if (CompletedCancellationToken is { } configuredCancellation)
                return ValueTask.FromCanceled<DiagnosticTrimResult>(configuredCancellation);
            if (ReturnCompletedFailure)
                return ValueTask.FromException<DiagnosticTrimResult>(Failure!);
            if (CompleteAsynchronously && Failure is not null)
                return FailAsync<DiagnosticTrimResult>(Failure);
            ThrowIfConfigured();
            var result = new DiagnosticTrimResult(TrimStatus, new(9), new(4), Statistics());
            if (Completion is not null)
                return CompleteAfterAsync(Completion, result);
            return CompleteAsynchronously ? CompleteAsync(result) : ValueTask.FromResult(result);
        }

        private void ThrowIfConfigured()
        {
            if (Failure is not null)
                throw Failure;
        }

        private static async ValueTask<T> CompleteAsync<T>(T result)
        {
            await Task.Yield();
            return result;
        }

        private static async ValueTask<T> CompleteAfterAsync<T>(Task completion, T result)
        {
            await completion;
            return result;
        }

        private static ValueTask<T> CompleteCanceledWithoutToken<T>()
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetCanceled();
            return new(completion.Task);
        }

        private static async ValueTask<T> FailAsync<T>(Exception failure)
        {
            await Task.Yield();
            throw failure;
        }

        private static DiagnosticStreamStatistics Statistics() => new(new(5), new("9"), new("9"), null);
    }

    private sealed record CancellationObservation(
        Type ExceptionType,
        bool IsCanceled,
        bool Synchronous,
        CancellationToken CancellationToken);

}

internal sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public TelemetryCapture()
    {
        _activityListener = new()
        {
            ShouldListenTo = source => source.Name == DiagnosticRecordTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DiagnosticRecordTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _meterListener.Start();
    }

    public List<Activity> Activities { get; } = [];
    public List<MetricMeasurement> Measurements { get; } = [];

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
    }

    private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
    {
        var snapshot = tags.ToArray().ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        Measurements.Add(new(instrument.Name, value, snapshot));
    }
}

internal sealed record MetricMeasurement(string Name, object Value, IReadOnlyDictionary<string, object?> Tags);

public sealed record DiagnosticRecordDirectRoutes(
    Func<DiagnosticRecordBatch, ValueTask<DiagnosticAppendResult>> Append,
    Func<DiagnosticRecordQuery, ValueTask<DiagnosticRecordPage>> Query,
    Func<DiagnosticStreamInspectionRequest, ValueTask<DiagnosticStreamStatistics>> Inspect,
    Func<DiagnosticTrimRequest, ValueTask<DiagnosticTrimResult>> Trim);

public static class DiagnosticRecordInstrumentationAssertions
{
    public static void AssertProviderWiring(IDiagnosticRecordStore store, string provider)
    {
        var instrumented = Assert.IsType<InstrumentedDiagnosticRecordStore>(store.Handlers.Append);

        Assert.Same(instrumented, store.Handlers.Query);
        Assert.Same(instrumented, store.Handlers.Inspect);
        Assert.Same(instrumented, store.Handlers.Trim);
        Assert.Equal(new DiagnosticRecordTelemetryIdentity(provider, "diagnostic-records"), instrumented.Identity);
    }

    public static async Task AssertProviderRoutesAsync(
        IDiagnosticRecordStore store,
        string provider,
        DiagnosticRecordDirectRoutes direct)
    {
        AssertProviderWiring(store, provider);
        var scope = new DiagnosticStorageScope("tenant-secret", "scope-secret");
        var stream = new DiagnosticStreamId("invalid-stream");
        var operationId = new DiagnosticOperationId(
            DateTimeOffset.Parse("2026-07-12T12:00:00Z"),
            "operation-secret");
        var batch = DiagnosticRecordBatch.Create(
            scope,
            stream,
            operationId,
            [new("record-secret", operationId.IssuedAt, "{\"value\":\"payload-secret\"}")]);
        var query = new DiagnosticRecordQuery(scope, stream, Limit: 0);
        var inspection = new DiagnosticStreamInspectionRequest(scope, stream);
        var trim = DiagnosticTrimRequest.Create(scope, stream, operationId, keepNewest: -1);

        await AssertRoutesAsync(
            provider,
            DiagnosticRecordTelemetry.Operations.Append,
            batch,
            direct.Append,
            request => store.AppendAsync(request),
            request => store.Handlers.Append.AppendAsync(request),
            expectedMetricCount: 3);
        await AssertRoutesAsync(
            provider,
            DiagnosticRecordTelemetry.Operations.Query,
            query,
            direct.Query,
            request => store.QueryAsync(request),
            request => store.Handlers.Query.QueryAsync(request),
            expectedMetricCount: 2);
        await AssertRoutesAsync(
            provider,
            DiagnosticRecordTelemetry.Operations.Inspect,
            inspection,
            direct.Inspect,
            request => store.InspectAsync(request),
            request => store.Handlers.Inspect.InspectAsync(request),
            expectedMetricCount: 2);
        await AssertRoutesAsync(
            provider,
            DiagnosticRecordTelemetry.Operations.Trim,
            trim,
            direct.Trim,
            request => store.TrimAsync(request),
            request => store.Handlers.Trim.TrimAsync(request),
            expectedMetricCount: 2);
    }

    private static async Task AssertRoutesAsync<TRequest, TResult>(
        string provider,
        string operation,
        TRequest request,
        Func<TRequest, ValueTask<TResult>> direct,
        Func<TRequest, ValueTask<TResult>> storeInterface,
        Func<TRequest, ValueTask<TResult>> handlerInterface,
        int expectedMetricCount)
        where TRequest : class
    {
        foreach (var route in new[] { direct, storeInterface, handlerInterface })
        {
            {
                using var capture = new TelemetryCapture();

                await Assert.ThrowsAsync<DiagnosticRecordValidationException>(async () => await route(request));

                var activity = Assert.Single(capture.Activities);
                Assert.Equal(operation, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Operation));
                Assert.Equal(provider, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Provider));
                Assert.Equal("diagnostic-records", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Store));
                Assert.Equal("invalid-stream", activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Stream));
                Assert.Equal(DiagnosticRecordTelemetry.Outcomes.Rejected,
                    activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Outcome));
                Assert.Equal(DiagnosticRecordTelemetry.Classifications.Rejection,
                    activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Classification));
                Assert.Equal(ActivityStatusCode.Error, activity.Status);

                var operationMetrics = OperationMetrics(capture, provider, operation);
                Assert.Equal(expectedMetricCount, operationMetrics.Length);
                Assert.Single(operationMetrics, measurement =>
                    measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
                Assert.Single(operationMetrics, measurement =>
                    measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationDuration);
                Assert.All(operationMetrics, measurement =>
                    Assert.DoesNotContain(DiagnosticRecordTelemetry.Tags.Stream, measurement.Tags.Keys));
                if (operation == DiagnosticRecordTelemetry.Operations.Append)
                    Assert.Single(operationMetrics, measurement =>
                        measurement.Name == DiagnosticRecordTelemetry.Instruments.AppendBatches &&
                        Equals(DiagnosticRecordTelemetry.Dispositions.Rejected,
                            measurement.Tags[DiagnosticRecordTelemetry.Tags.Disposition]));

                AssertNoLeakage(activity, operationMetrics);
            }

            await AssertNullRequestSemanticsAsync(route, provider, operation, expectedMetricCount);
        }
    }

    private static async Task AssertNullRequestSemanticsAsync<TRequest, TResult>(
        Func<TRequest, ValueTask<TResult>> route,
        string provider,
        string operation,
        int expectedMetricCount)
        where TRequest : class
    {
        var withoutTelemetry = await ObserveNullRequestAsync(route);
        using var capture = new TelemetryCapture();

        var withTelemetry = await ObserveNullRequestAsync(route);

        Assert.Equal(withoutTelemetry, withTelemetry);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(operation, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Operation));
        Assert.Equal(provider, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Provider));
        Assert.Equal(DiagnosticRecordTelemetry.Outcomes.Rejected,
            activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Outcome));
        Assert.Equal(DiagnosticRecordTelemetry.Classifications.Rejection,
            activity.GetTagItem(DiagnosticRecordTelemetry.Tags.Classification));
        Assert.Equal(false, activity.GetTagItem(DiagnosticRecordTelemetry.Tags.ScopePresent));
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == DiagnosticRecordTelemetry.Tags.Stream);

        var operationMetrics = OperationMetrics(capture, provider, operation);
        Assert.Equal(expectedMetricCount, operationMetrics.Length);
        Assert.Single(operationMetrics, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationOutcomes);
        Assert.Single(operationMetrics, measurement =>
            measurement.Name == DiagnosticRecordTelemetry.Instruments.OperationDuration);
        AssertNoLeakage(activity, operationMetrics);
    }

    private static async Task<NullRequestObservation> ObserveNullRequestAsync<TRequest, TResult>(
        Func<TRequest, ValueTask<TResult>> route)
        where TRequest : class
    {
        ValueTask<TResult> pending = default;
        var synchronous = Record.Exception(() => pending = route(null!));
        if (synchronous is not null)
            return new(synchronous.GetType(), Synchronous: true);

        var asynchronous = await Record.ExceptionAsync(async () => await pending);
        return new(Assert.IsAssignableFrom<Exception>(asynchronous).GetType(), Synchronous: false);
    }

    private static MetricMeasurement[] OperationMetrics(
        TelemetryCapture capture,
        string provider,
        string operation) => capture.Measurements.Where(measurement =>
        measurement.Tags.TryGetValue(DiagnosticRecordTelemetry.Tags.Provider, out var measuredProvider) &&
        Equals(provider, measuredProvider) &&
        measurement.Tags.TryGetValue(DiagnosticRecordTelemetry.Tags.Operation, out var measuredOperation) &&
        Equals(operation, measuredOperation)).ToArray();

    private static void AssertNoLeakage(Activity activity, IEnumerable<MetricMeasurement> operationMetrics)
    {
        var serialized = string.Join('|', activity.TagObjects
            .Concat(operationMetrics.SelectMany(measurement => measurement.Tags))
            .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("tenant-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("scope-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("record-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("operation-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-secret", serialized, StringComparison.Ordinal);
    }

    private sealed record NullRequestObservation(Type ExceptionType, bool Synchronous);
}
