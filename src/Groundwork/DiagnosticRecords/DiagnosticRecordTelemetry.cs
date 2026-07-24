using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Groundwork.DiagnosticRecords;

/// <summary>
/// Stable names for the version 1 diagnostic-record telemetry contract.
/// </summary>
public static class DiagnosticRecordTelemetry
{
    public const string Version = "1.0.0";
    public const string ActivitySourceName = "Groundwork.DiagnosticRecords";
    public const string MeterName = "Groundwork.DiagnosticRecords";

    public static class Activities
    {
        public const string Append = "groundwork.diagnostic_records.append";
        public const string Query = "groundwork.diagnostic_records.query";
        public const string QueryGroups = "groundwork.diagnostic_records.query_groups";
        public const string Inspect = "groundwork.diagnostic_records.inspect";
        public const string Trim = "groundwork.diagnostic_records.trim";
    }

    public static class Instruments
    {
        public const string OperationDuration = "groundwork.diagnostic_records.operation.duration";
        public const string OperationOutcomes = "groundwork.diagnostic_records.operation.outcomes";
        public const string AppendBatches = "groundwork.diagnostic_records.append.batches";
        public const string RecordsAppended = "groundwork.diagnostic_records.append.records";
        public const string ExactCountRequests = "groundwork.diagnostic_records.query.exact_count.requests";
        public const string LatestPerKeyRequests = "groundwork.diagnostic_records.query.latest_per_key.requests";
        public const string TrimExaminedRecords = "groundwork.diagnostic_records.trim.records.examined";
        public const string TrimDeletedRecords = "groundwork.diagnostic_records.trim.records.deleted";
        public const string RetainedRecords = "groundwork.diagnostic_records.retained_records";
    }

    public static class Tags
    {
        public const string Operation = "groundwork.diagnostic_records.operation";
        public const string Provider = "groundwork.diagnostic_records.provider";
        public const string Store = "groundwork.diagnostic_records.store";
        public const string Stream = "groundwork.diagnostic_records.stream";
        public const string Outcome = "groundwork.diagnostic_records.outcome";
        public const string Classification = "groundwork.diagnostic_records.classification";
        public const string Disposition = "groundwork.diagnostic_records.disposition";
        public const string ScopeKind = "groundwork.diagnostic_records.scope.kind";
        public const string ScopePresent = "groundwork.diagnostic_records.scope.present";
        public const string BatchSize = "groundwork.diagnostic_records.append.batch_size";
        public const string QueryLimit = "groundwork.diagnostic_records.query.limit";
        public const string ExactCountRequested = "groundwork.diagnostic_records.query.exact_count_requested";
        public const string LatestPerKeyRequested = "groundwork.diagnostic_records.query.latest_per_key_requested";
        public const string ContinuationPresent = "groundwork.diagnostic_records.query.continuation_present";
        public const string GroupTake = "groundwork.diagnostic_records.query_groups.take";
        public const string GroupPredicatePresent = "groundwork.diagnostic_records.query_groups.predicate_present";
        public const string GroupContinuationPresent = "groundwork.diagnostic_records.query_groups.continuation_present";
        public const string KeepNewest = "groundwork.diagnostic_records.trim.keep_newest";
    }

    public static class Operations
    {
        public const string Append = "append";
        public const string Query = "query";
        public const string QueryGroups = "query_groups";
        public const string Inspect = "inspect";
        public const string Trim = "trim";
    }

    public static class Outcomes
    {
        public const string Success = "success";
        public const string Committed = "committed";
        public const string Completed = "completed";
        public const string Replayed = "replayed";
        public const string Conflict = "conflict";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";
        public const string AcknowledgementLost = "acknowledgement_lost";
        public const string ProviderFailure = "provider_failure";
    }

    public static class Classifications
    {
        public const string Success = "success";
        public const string Replay = "replay";
        public const string Conflict = "conflict";
        public const string Rejection = "rejection";
        public const string Cancellation = "cancellation";
        public const string AcknowledgementLoss = "acknowledgement_loss";
        public const string ProviderFailure = "provider_failure";
    }

    public static class Dispositions
    {
        public const string Accepted = "accepted";
        public const string Rejected = "rejected";
        public const string Indeterminate = "indeterminate";
    }

    public static class ScopeKinds
    {
        public const string TenantScope = "tenant_scope";
    }
}

/// <summary>
/// Low-cardinality identity attached to diagnostic-record telemetry.
/// Values are restricted to 64 lowercase ASCII letters, digits, periods, underscores, or hyphens.
/// </summary>
public sealed record DiagnosticRecordTelemetryIdentity
{
    public DiagnosticRecordTelemetryIdentity(string provider, string store)
    {
        Provider = Validate(provider, nameof(provider));
        Store = Validate(store, nameof(store));
    }

    public string Provider { get; }
    public string Store { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 64 || value.Any(character =>
                character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new ArgumentException("Telemetry identity must be at most 64 lowercase ASCII letters, digits, periods, underscores, or hyphens.", parameterName);
        return value;
    }
}

/// <summary>
/// Adds provider-neutral traces and metrics around a diagnostic-record handler set.
/// </summary>
public sealed class InstrumentedDiagnosticRecordStore :
    IDiagnosticRecordStore,
    IDiagnosticAppendHandler,
    IDiagnosticQueryHandler,
    IDiagnosticGroupedQueryHandler,
    IDiagnosticInspectHandler,
    IDiagnosticTrimHandler
{
    private static readonly ActivitySource ActivitySource = new(
        DiagnosticRecordTelemetry.ActivitySourceName,
        DiagnosticRecordTelemetry.Version);

    private static readonly Meter Meter = new(
        DiagnosticRecordTelemetry.MeterName,
        DiagnosticRecordTelemetry.Version);

    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        DiagnosticRecordTelemetry.Instruments.OperationDuration,
        unit: "s",
        description: "Elapsed provider boundary time for a diagnostic-record operation.");

    private static readonly Counter<long> OperationOutcomes = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.OperationOutcomes,
        unit: "{operation}",
        description: "Diagnostic-record operation outcomes, including replay and retry-relevant failures.");

    private static readonly Counter<long> AppendBatches = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.AppendBatches,
        unit: "{batch}",
        description: "Accepted, rejected, and indeterminate diagnostic-record append batches.");

    private static readonly Counter<long> RecordsAppended = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.RecordsAppended,
        unit: "{record}",
        description: "Records committed by non-replayed append operations.");

    private static readonly Counter<long> ExactCountRequests = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.ExactCountRequests,
        unit: "{request}",
        description: "Queries requesting an exact count.");

    private static readonly Counter<long> LatestPerKeyRequests = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.LatestPerKeyRequests,
        unit: "{request}",
        description: "Queries requesting latest-per-key projection.");

    private static readonly Counter<long> TrimExaminedRecords = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.TrimExaminedRecords,
        unit: "{record}",
        description: "Records examined by non-replayed completed trims.");

    private static readonly Counter<long> TrimDeletedRecords = Meter.CreateCounter<long>(
        DiagnosticRecordTelemetry.Instruments.TrimDeletedRecords,
        unit: "{record}",
        description: "Records deleted by non-replayed completed trims.");

    private static readonly Histogram<long> RetainedRecords = Meter.CreateHistogram<long>(
        DiagnosticRecordTelemetry.Instruments.RetainedRecords,
        unit: "{record}",
        description: "Retained-record count observed in inspect and non-replayed completed trim results.");

    private readonly DiagnosticRecordStoreHandlers _inner;

    public InstrumentedDiagnosticRecordStore(
        IDiagnosticRecordStore inner,
        DiagnosticRecordTelemetryIdentity identity)
        : this(inner?.Handlers ?? throw new ArgumentNullException(nameof(inner)), identity)
    {
    }

    public InstrumentedDiagnosticRecordStore(
        DiagnosticRecordStoreHandlers handlers,
        DiagnosticRecordTelemetryIdentity identity)
    {
        _inner = handlers ?? throw new ArgumentNullException(nameof(handlers));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Handlers = new(this, this, this, this) { GroupedQuery = this };
    }

    public DiagnosticRecordTelemetryIdentity Identity { get; }
    public DiagnosticRecordStoreHandlers Handlers { get; }
    public DiagnosticQueryHandlerCapabilities Capabilities => _inner.Query.Capabilities;
    DiagnosticGroupedQueryHandlerCapabilities IDiagnosticGroupedQueryHandler.Capabilities => _inner.GroupedQuery.Capabilities;

    public ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default) =>
        IsAppendEnabled
            ? AppendInstrumented(batch, cancellationToken)
            : _inner.Append.AppendAsync(batch, cancellationToken);

    public ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        IsQueryEnabled
            ? QueryInstrumented(query, cancellationToken)
            : _inner.Query.QueryAsync(query, cancellationToken);

    public ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default) =>
        IsQueryGroupsEnabled
            ? QueryGroupsInstrumented(query, cancellationToken)
            : _inner.GroupedQuery.QueryGroupsAsync(query, cancellationToken);

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        IsInspectEnabled
            ? InspectInstrumented(request, cancellationToken)
            : _inner.Inspect.InspectAsync(request, cancellationToken);

    public ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        IsTrimEnabled
            ? TrimInstrumented(request, cancellationToken)
            : _inner.Trim.TrimAsync(request, cancellationToken);

    private static bool IsAppendEnabled =>
        ActivitySource.HasListeners() || OperationDuration.Enabled || OperationOutcomes.Enabled ||
        AppendBatches.Enabled || RecordsAppended.Enabled;

    private static bool IsQueryEnabled =>
        ActivitySource.HasListeners() || OperationDuration.Enabled || OperationOutcomes.Enabled ||
        ExactCountRequests.Enabled || LatestPerKeyRequests.Enabled;

    private static bool IsQueryGroupsEnabled =>
        ActivitySource.HasListeners() || OperationDuration.Enabled || OperationOutcomes.Enabled;

    private static bool IsInspectEnabled =>
        ActivitySource.HasListeners() || OperationDuration.Enabled || OperationOutcomes.Enabled || RetainedRecords.Enabled;

    private static bool IsTrimEnabled =>
        ActivitySource.HasListeners() || OperationDuration.Enabled || OperationOutcomes.Enabled ||
        TrimExaminedRecords.Enabled || TrimDeletedRecords.Enabled || RetainedRecords.Enabled;

    private ValueTask<DiagnosticAppendResult> AppendInstrumented(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var callerActivity = Activity.Current;
        Activity? activity = null;
        var nullRequest = batch is null;
        try
        {
            activity = StartActivity(
                DiagnosticRecordTelemetry.Activities.Append,
                DiagnosticRecordTelemetry.Operations.Append);
            if (batch is { } validBatch)
            {
                SetRequestContext(activity, validBatch.Stream);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.BatchSize, validBatch.Records?.Count ?? 0);
            }
            var pending = _inner.Append.AppendAsync(batch!, cancellationToken);
            if (!pending.IsCompleted)
            {
                try
                {
                    return AwaitAppendAsync(pending, activity, startedAt, nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            if (!pending.IsCompletedSuccessfully)
            {
                try
                {
                    return CompleteFailedValueTask(
                        pending,
                        activity,
                        DiagnosticRecordTelemetry.Operations.Append,
                        startedAt,
                        nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            var result = pending.Result;
            CompleteAppendSuccess(activity, result, startedAt);
            DisposeAndRestore(activity, callerActivity);
            return ValueTask.FromResult(result);
        }
        catch (Exception exception)
        {
            CompleteAppendFailure(activity, exception, startedAt, nullRequest);
            DisposeAndRestore(activity, callerActivity);
            throw;
        }
    }

    private async ValueTask<DiagnosticAppendResult> AwaitAppendAsync(
        ValueTask<DiagnosticAppendResult> pending,
        Activity? activity,
        long startedAt,
        bool nullRequest)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            CompleteAppendSuccess(activity, result, startedAt);
            return result;
        }
        catch (Exception exception)
        {
            CompleteAppendFailure(activity, exception, startedAt, nullRequest);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private ValueTask<DiagnosticRecordPage> QueryInstrumented(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken) =>
        QueryOperationInstrumented(
            DiagnosticRecordTelemetry.Activities.Query,
            DiagnosticRecordTelemetry.Operations.Query,
            query is null,
            activity =>
            {
                if (query is not { } validQuery)
                    return;
                SetRequestContext(activity, validQuery.Stream);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.QueryLimit, validQuery.Limit);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.ExactCountRequested, validQuery.IncludeExactCount);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.LatestPerKeyRequested, validQuery.LatestPerKeyField is not null);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.ContinuationPresent, validQuery.Continuation is not null);
                var requestTags = CreateBaseMetricTags(DiagnosticRecordTelemetry.Operations.Query);
                if (validQuery.IncludeExactCount)
                    ExactCountRequests.Add(1, requestTags);
                if (validQuery.LatestPerKeyField is not null)
                    LatestPerKeyRequests.Add(1, requestTags);
            },
            () => _inner.Query.QueryAsync(query!, cancellationToken));

    private ValueTask<DiagnosticRecordGroupPage> QueryGroupsInstrumented(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken) =>
        QueryOperationInstrumented(
            DiagnosticRecordTelemetry.Activities.QueryGroups,
            DiagnosticRecordTelemetry.Operations.QueryGroups,
            query is null,
            activity =>
            {
                if (query is not { } validQuery)
                    return;
                SetRequestContext(activity, validQuery.Stream);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.GroupTake, validQuery.Take);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.GroupPredicatePresent, validQuery.Predicate is not null);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.GroupContinuationPresent, validQuery.Continuation is not null);
            },
            () => _inner.GroupedQuery.QueryGroupsAsync(query!, cancellationToken));

    private ValueTask<TResult> QueryOperationInstrumented<TResult>(
        string activityName,
        string operation,
        bool nullRequest,
        Action<Activity?> setRequestTags,
        Func<ValueTask<TResult>> invoke)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var callerActivity = Activity.Current;
        Activity? activity = null;
        try
        {
            activity = StartActivity(activityName, operation);
            setRequestTags(activity);
            var pending = invoke();
            if (!pending.IsCompleted)
            {
                try
                {
                    return AwaitQueryOperationAsync(pending, activity, operation, startedAt, nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            if (!pending.IsCompletedSuccessfully)
            {
                try
                {
                    return CompleteFailedValueTask(pending, activity, operation, startedAt, nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            var result = pending.Result;
            Complete(activity, operation, OperationOutcome.Success, startedAt);
            DisposeAndRestore(activity, callerActivity);
            return ValueTask.FromResult(result);
        }
        catch (Exception exception)
        {
            Complete(activity, operation, Classify(exception, nullRequest), startedAt);
            DisposeAndRestore(activity, callerActivity);
            throw;
        }
    }

    private async ValueTask<TResult> AwaitQueryOperationAsync<TResult>(
        ValueTask<TResult> pending,
        Activity? activity,
        string operation,
        long startedAt,
        bool nullRequest)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            Complete(activity, operation, OperationOutcome.Success, startedAt);
            return result;
        }
        catch (Exception exception)
        {
            Complete(activity, operation, Classify(exception, nullRequest), startedAt);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private ValueTask<DiagnosticStreamStatistics> InspectInstrumented(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var callerActivity = Activity.Current;
        Activity? activity = null;
        var nullRequest = request is null;
        try
        {
            activity = StartActivity(
                DiagnosticRecordTelemetry.Activities.Inspect,
                DiagnosticRecordTelemetry.Operations.Inspect);
            if (request is { } validRequest)
                SetRequestContext(activity, validRequest.Stream);
            var pending = _inner.Inspect.InspectAsync(request!, cancellationToken);
            if (!pending.IsCompleted)
            {
                try
                {
                    return AwaitInspectAsync(pending, activity, startedAt, nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            if (!pending.IsCompletedSuccessfully)
            {
                try
                {
                    return CompleteFailedValueTask(
                        pending,
                        activity,
                        DiagnosticRecordTelemetry.Operations.Inspect,
                        startedAt,
                        nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            var result = pending.Result;
            Complete(activity, DiagnosticRecordTelemetry.Operations.Inspect, OperationOutcome.Success, startedAt);
            RetainedRecords.Record(
                result.RetainedCount.Value,
                CreateBaseMetricTags(DiagnosticRecordTelemetry.Operations.Inspect));
            DisposeAndRestore(activity, callerActivity);
            return ValueTask.FromResult(result);
        }
        catch (Exception exception)
        {
            Complete(activity, DiagnosticRecordTelemetry.Operations.Inspect, Classify(exception, nullRequest), startedAt);
            DisposeAndRestore(activity, callerActivity);
            throw;
        }
    }

    private async ValueTask<DiagnosticStreamStatistics> AwaitInspectAsync(
        ValueTask<DiagnosticStreamStatistics> pending,
        Activity? activity,
        long startedAt,
        bool nullRequest)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            Complete(activity, DiagnosticRecordTelemetry.Operations.Inspect, OperationOutcome.Success, startedAt);
            RetainedRecords.Record(
                result.RetainedCount.Value,
                CreateBaseMetricTags(DiagnosticRecordTelemetry.Operations.Inspect));
            return result;
        }
        catch (Exception exception)
        {
            Complete(activity, DiagnosticRecordTelemetry.Operations.Inspect, Classify(exception, nullRequest), startedAt);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private ValueTask<DiagnosticTrimResult> TrimInstrumented(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var callerActivity = Activity.Current;
        Activity? activity = null;
        var nullRequest = request is null;
        try
        {
            activity = StartActivity(
                DiagnosticRecordTelemetry.Activities.Trim,
                DiagnosticRecordTelemetry.Operations.Trim);
            if (request is { } validRequest)
            {
                SetRequestContext(activity, validRequest.Stream);
                activity?.SetTag(DiagnosticRecordTelemetry.Tags.KeepNewest, validRequest.KeepNewest);
            }
            var pending = _inner.Trim.TrimAsync(request!, cancellationToken);
            if (!pending.IsCompleted)
            {
                try
                {
                    return AwaitTrimAsync(pending, activity, startedAt, nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            if (!pending.IsCompletedSuccessfully)
            {
                try
                {
                    return CompleteFailedValueTask(
                        pending,
                        activity,
                        DiagnosticRecordTelemetry.Operations.Trim,
                        startedAt,
                        nullRequest);
                }
                finally
                {
                    Activity.Current = callerActivity;
                }
            }
            var result = pending.Result;
            CompleteTrimSuccess(activity, result, startedAt);
            DisposeAndRestore(activity, callerActivity);
            return ValueTask.FromResult(result);
        }
        catch (Exception exception)
        {
            Complete(activity, DiagnosticRecordTelemetry.Operations.Trim, Classify(exception, nullRequest), startedAt);
            DisposeAndRestore(activity, callerActivity);
            throw;
        }
    }

    private async ValueTask<DiagnosticTrimResult> AwaitTrimAsync(
        ValueTask<DiagnosticTrimResult> pending,
        Activity? activity,
        long startedAt,
        bool nullRequest)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            CompleteTrimSuccess(activity, result, startedAt);
            return result;
        }
        catch (Exception exception)
        {
            Complete(activity, DiagnosticRecordTelemetry.Operations.Trim, Classify(exception, nullRequest), startedAt);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private Activity? StartActivity(string name, string operation)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        if (activity is null)
            return null;
        activity.SetTag(DiagnosticRecordTelemetry.Tags.Operation, operation);
        activity.SetTag(DiagnosticRecordTelemetry.Tags.Provider, Identity.Provider);
        activity.SetTag(DiagnosticRecordTelemetry.Tags.Store, Identity.Store);
        activity.SetTag(DiagnosticRecordTelemetry.Tags.ScopeKind, DiagnosticRecordTelemetry.ScopeKinds.TenantScope);
        activity.SetTag(DiagnosticRecordTelemetry.Tags.ScopePresent, false);
        return activity;
    }

    private static void SetRequestContext(Activity? activity, DiagnosticStreamId stream)
    {
        activity?.SetTag(DiagnosticRecordTelemetry.Tags.Stream, stream.Value);
        activity?.SetTag(DiagnosticRecordTelemetry.Tags.ScopePresent, true);
    }

    private void CompleteAppendSuccess(Activity? activity, DiagnosticAppendResult result, long startedAt)
    {
        var outcome = result.Status == DiagnosticAppendStatus.Replayed
            ? OperationOutcome.Replay
            : OperationOutcome.Committed;
        Complete(activity, DiagnosticRecordTelemetry.Operations.Append, outcome, startedAt);
        RecordAppendBatch(DiagnosticRecordTelemetry.Dispositions.Accepted, outcome);
        if (result.Status == DiagnosticAppendStatus.Committed)
            RecordsAppended.Add(result.Records.Count, CreateMetricTags(DiagnosticRecordTelemetry.Operations.Append, outcome));
    }

    private void CompleteAppendFailure(
        Activity? activity,
        Exception exception,
        long startedAt,
        bool nullRequest = false)
    {
        var outcome = Classify(exception, nullRequest);
        Complete(activity, DiagnosticRecordTelemetry.Operations.Append, outcome, startedAt);
        RecordAppendBatch(IsDefiniteRejection(outcome)
            ? DiagnosticRecordTelemetry.Dispositions.Rejected
            : DiagnosticRecordTelemetry.Dispositions.Indeterminate, outcome);
    }

    private void CompleteTrimSuccess(Activity? activity, DiagnosticTrimResult result, long startedAt)
    {
        var outcome = result.Status == DiagnosticTrimStatus.Replayed
            ? OperationOutcome.Replay
            : OperationOutcome.Completed;
        Complete(activity, DiagnosticRecordTelemetry.Operations.Trim, outcome, startedAt);
        if (result.Status != DiagnosticTrimStatus.Completed)
            return;
        var tags = CreateMetricTags(DiagnosticRecordTelemetry.Operations.Trim, outcome);
        TrimExaminedRecords.Add(result.ExaminedCount.Value, tags);
        TrimDeletedRecords.Add(result.DeletedCount.Value, tags);
        RetainedRecords.Record(result.Statistics.RetainedCount.Value, tags);
    }

    private ValueTask<T> CompleteFailedValueTask<T>(
        ValueTask<T> pending,
        Activity? activity,
        string operation,
        long startedAt,
        bool nullRequest)
    {
        var completion = pending.AsTask();
        try
        {
            _ = completion.GetAwaiter().GetResult();
            throw new UnreachableException("A non-successful completed ValueTask returned a result.");
        }
        catch (Exception exception)
        {
            if (operation == DiagnosticRecordTelemetry.Operations.Append)
                CompleteAppendFailure(activity, exception, startedAt, nullRequest);
            else
                Complete(activity, operation, Classify(exception, nullRequest), startedAt);
            activity?.Dispose();
            return new(completion);
        }
    }

    private void Complete(Activity? activity, string operation, OperationOutcome outcome, long startedAt)
    {
        activity?.SetTag(DiagnosticRecordTelemetry.Tags.Outcome, outcome.Name);
        activity?.SetTag(DiagnosticRecordTelemetry.Tags.Classification, outcome.Classification);
        activity?.SetStatus(outcome.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        var tags = CreateMetricTags(operation, outcome);
        OperationOutcomes.Add(1, tags);
        OperationDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
    }

    private void RecordAppendBatch(string disposition, OperationOutcome outcome)
    {
        var tags = CreateMetricTags(DiagnosticRecordTelemetry.Operations.Append, outcome);
        tags.Add(DiagnosticRecordTelemetry.Tags.Disposition, disposition);
        AppendBatches.Add(1, tags);
    }

    private TagList CreateBaseMetricTags(string operation) => new()
    {
        { DiagnosticRecordTelemetry.Tags.Operation, operation },
        { DiagnosticRecordTelemetry.Tags.Provider, Identity.Provider },
        { DiagnosticRecordTelemetry.Tags.Store, Identity.Store }
    };

    private TagList CreateMetricTags(string operation, OperationOutcome outcome)
    {
        var tags = CreateBaseMetricTags(operation);
        tags.Add(DiagnosticRecordTelemetry.Tags.Outcome, outcome.Name);
        tags.Add(DiagnosticRecordTelemetry.Tags.Classification, outcome.Classification);
        return tags;
    }

    private static OperationOutcome Classify(Exception exception, bool nullRequest = false) => nullRequest
        ? OperationOutcome.Rejected
        : exception switch
        {
            DiagnosticOperationConflictException => OperationOutcome.Conflict,
            DiagnosticRecordValidationException or
                ArgumentNullException or
                DiagnosticRequestFingerprintMismatchException or
                DiagnosticOperationExpiredException or
                DiagnosticOperationClockSkewException => OperationOutcome.Rejected,
            OperationCanceledException => OperationOutcome.Cancelled,
            DiagnosticAcknowledgementLostException => OperationOutcome.AcknowledgementLost,
            _ => OperationOutcome.ProviderFailure
        };

    private static void DisposeAndRestore(Activity? activity, Activity? callerActivity)
    {
        activity?.Dispose();
        Activity.Current = callerActivity;
    }

    private static bool IsDefiniteRejection(OperationOutcome outcome) =>
        outcome.Classification is DiagnosticRecordTelemetry.Classifications.Conflict or
            DiagnosticRecordTelemetry.Classifications.Rejection;

    private readonly record struct OperationOutcome(
        string Name,
        string Classification,
        bool IsSuccess)
    {
        public static OperationOutcome Success { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Success,
            DiagnosticRecordTelemetry.Classifications.Success,
            true);
        public static OperationOutcome Committed { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Committed,
            DiagnosticRecordTelemetry.Classifications.Success,
            true);
        public static OperationOutcome Completed { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Completed,
            DiagnosticRecordTelemetry.Classifications.Success,
            true);
        public static OperationOutcome Replay { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Replayed,
            DiagnosticRecordTelemetry.Classifications.Replay,
            true);
        public static OperationOutcome Conflict { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Conflict,
            DiagnosticRecordTelemetry.Classifications.Conflict,
            false);
        public static OperationOutcome Rejected { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Rejected,
            DiagnosticRecordTelemetry.Classifications.Rejection,
            false);
        public static OperationOutcome Cancelled { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.Cancelled,
            DiagnosticRecordTelemetry.Classifications.Cancellation,
            false);
        public static OperationOutcome AcknowledgementLost { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.AcknowledgementLost,
            DiagnosticRecordTelemetry.Classifications.AcknowledgementLoss,
            false);
        public static OperationOutcome ProviderFailure { get; } = new(
            DiagnosticRecordTelemetry.Outcomes.ProviderFailure,
            DiagnosticRecordTelemetry.Classifications.ProviderFailure,
            false);
    }
}
