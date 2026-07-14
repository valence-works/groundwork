using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Groundwork.DiagnosticRecords;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.DiagnosticRecords;

internal enum MongoDbDiagnosticRecordExecutionPoint
{
    AppendBeforeCommit,
    AppendAfterRecordStagedBeforeCommit,
    AppendAfterCommitBeforeAcknowledgement,
    TrimBeforeCommit,
    TrimAfterRecordDeletedBeforeCommit,
    TrimAfterCommitBeforeAcknowledgement,
    QueryAfterHighWaterRead,
    InspectAfterStreamStateRead,
    CommitResultUnknown
}

/// <summary>
/// MongoDB's transaction-backed diagnostic record store. A replica set or sharded deployment is
/// required because records, cursor state, and operation outcomes form one atomic commit.
/// </summary>
public sealed class MongoDbDiagnosticRecordStore :
    IDiagnosticRecordStore,
    IDiagnosticAppendHandler,
    IDiagnosticQueryHandler,
    IDiagnosticInspectHandler,
    IDiagnosticTrimHandler
{
    private const int CleanupBatchSize = 32;
    private readonly IMongoDatabase _database;
    private readonly DiagnosticRecordStreamDefinition _definition;
    private readonly TimeProvider _timeProvider;
    private readonly Func<MongoDbDiagnosticRecordExecutionPoint, CancellationToken, ValueTask> _interceptAsync;
    private readonly InstrumentedDiagnosticRecordStore _instrumented;

    internal MongoDbDiagnosticRecordStore(
        IMongoDatabase database,
        DiagnosticRecordStreamDefinition definition,
        TimeProvider? timeProvider = null,
        Func<MongoDbDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptor = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _definition = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(_definition);
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interceptAsync = interceptor ?? ((_, _) => ValueTask.CompletedTask);
        var core = new CoreHandlers(this);
        _instrumented = new(
            new DiagnosticRecordStoreHandlers(core, core, core, core),
            new("mongodb", "diagnostic-records"));
        Handlers = _instrumented.Handlers;
    }

    public bool RequiresMultiDocumentTransactions => true;
    public DiagnosticRecordStoreHandlers Handlers { get; }
    public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
        Enum.GetValues<DiagnosticPredicateOperator>().ToFrozenSet(), true, true, true, true, true);

    public ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default) =>
        _instrumented.AppendAsync(batch, cancellationToken);

    public ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        _instrumented.QueryAsync(query, cancellationToken);

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        _instrumented.InspectAsync(request, cancellationToken);

    public ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        _instrumented.TrimAsync(request, cancellationToken);

    private IMongoCollection<BsonDocument> Collection(string name) =>
        _database.GetCollection<BsonDocument>(name).WithWriteConcern(WriteConcern.WMajority);

    private IMongoCollection<BsonDocument> Records => Collection(MongoDbDiagnosticRecordNames.Records);
    private IMongoCollection<BsonDocument> Streams => Collection(MongoDbDiagnosticRecordNames.Streams);
    private IMongoCollection<BsonDocument> AppendOperations => Collection(MongoDbDiagnosticRecordNames.AppendOperations);
    private IMongoCollection<BsonDocument> AppendOutcomes => Collection(MongoDbDiagnosticRecordNames.AppendOutcomes);
    private IMongoCollection<BsonDocument> TrimOperations => Collection(MongoDbDiagnosticRecordNames.TrimOperations);
    private IMongoCollection<BsonDocument> ProviderState => Collection(MongoDbDiagnosticRecordNames.ProviderState);

    private sealed class CoreHandlers(MongoDbDiagnosticRecordStore owner) :
        IDiagnosticAppendHandler,
        IDiagnosticQueryHandler,
        IDiagnosticInspectHandler,
        IDiagnosticTrimHandler
    {
        public DiagnosticQueryHandlerCapabilities Capabilities => owner.Capabilities;

        public ValueTask<DiagnosticAppendResult> AppendAsync(
            DiagnosticRecordBatch batch,
            CancellationToken cancellationToken = default) =>
            owner.AppendCoreAsync(batch, cancellationToken);

        public ValueTask<DiagnosticRecordPage> QueryAsync(
            DiagnosticRecordQuery query,
            CancellationToken cancellationToken = default) =>
            owner.QueryCoreAsync(query, cancellationToken);

        public ValueTask<DiagnosticStreamStatistics> InspectAsync(
            DiagnosticStreamInspectionRequest request,
            CancellationToken cancellationToken = default) =>
            owner.InspectCoreAsync(request, cancellationToken);

        public ValueTask<DiagnosticTrimResult> TrimAsync(
            DiagnosticTrimRequest request,
            CancellationToken cancellationToken = default) =>
            owner.TrimCoreAsync(request, cancellationToken);
    }

    private async ValueTask<DiagnosticAppendResult> AppendCoreAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        batch = DiagnosticRecordRequestSnapshot.Capture(batch);
        DiagnosticRecordRequestValidator.Validate(batch, _definition);
        await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.AppendBeforeCommit, cancellationToken);

        var providerNow = await AdvanceProviderClockAsync(cancellationToken);
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        var result = await ExecuteTransactionAsync(session, DiagnosticOperationKind.Append, batch.Stream, batch.OperationId, async (transaction, token) =>
        {
            var operationId = OperationKey(batch.Scope, batch.Stream, batch.OperationId);
            var prior = await AppendOperations.Find(transaction, Builders<BsonDocument>.Filter.Eq("_id", operationId))
                .FirstOrDefaultAsync(token);
            if (prior is not null)
                return await ReplayAppendAsync(transaction, prior, batch, providerNow, token);

            DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(batch, _definition, providerNow);
            await CleanupExpiredOperationsAsync(transaction, AppendOperations, DiagnosticOperationKind.Append, providerNow, token);

            var scopeFilter = ScopeFilter(batch.Scope, batch.Stream);
            var ids = new BsonArray(batch.Records.Select(record => record.RecordId));
            var conflicts = await Records.Find(transaction, scopeFilter & Builders<BsonDocument>.Filter.In("record_id", ids))
                .Project(Builders<BsonDocument>.Projection.Include("record_id"))
                .ToListAsync(token);
            if (conflicts.Count > 0)
                throw ExistingRecordException(conflicts.Select(x => x["record_id"].AsString));

            var streamId = StreamKey(batch.Scope, batch.Stream);
            var streamUpdate = Builders<BsonDocument>.Update
                .SetOnInsert("tenant_id", batch.Scope.TenantId)
                .SetOnInsert("scope_id", batch.Scope.ScopeId)
                .SetOnInsert("stream_id", batch.Stream.Value)
                .Inc("next_cursor", batch.Records.Count);
            var stream = await Streams.FindOneAndUpdateAsync(
                transaction,
                Builders<BsonDocument>.Filter.Eq("_id", streamId),
                streamUpdate,
                new() { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                token);
            var lastCursor = stream["next_cursor"].ToInt64();
            var firstCursor = lastCursor - batch.Records.Count + 1;
            var committed = new List<DiagnosticRecord>(batch.Records.Count);
            for (var index = 0; index < batch.Records.Count; index++)
            {
                var record = new DiagnosticRecord(
                    batch.Records[index].RecordId,
                    batch.Records[index].OccurredAt,
                    batch.Records[index].Payload,
                    new((firstCursor + index).ToString(CultureInfo.InvariantCulture)),
                    batch.Records[index].Fields);
                committed.Add(record);
            }
            var recordDocuments = committed.Select(record => RecordDocument(batch.Scope, batch.Stream, record)).ToArray();
            await Records.InsertManyAsync(transaction, recordDocuments, new InsertManyOptions { IsOrdered = true }, token);
            await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit, token);

            var logicalHighWater = ReadLogicalHighWater(stream);
            logicalHighWater = MaxLogicalHighWater(logicalHighWater, committed);
            if (logicalHighWater is { } highWater)
                await Streams.UpdateOneAsync(transaction,
                    Builders<BsonDocument>.Filter.Eq("_id", streamId),
                    Builders<BsonDocument>.Update.Set("logical_high_water", FieldValueDocument(highWater)),
                    cancellationToken: token);

            var snapshot = DiagnosticRecordSnapshot.Capture(committed);
            var appendResult = new DiagnosticAppendResult(
                DiagnosticAppendStatus.Committed,
                snapshot,
                new(lastCursor.ToString(CultureInfo.InvariantCulture)),
                logicalHighWater);
            await AppendOperations.InsertOneAsync(transaction,
                AppendLedgerDocument(operationId, batch, appendResult, providerNow), cancellationToken: token);
            var outcomeDocuments = committed.Select((record, ordinal) =>
                AppendOutcomeDocument(operationId, ordinal, batch.Scope, batch.Stream, record)).ToArray();
            await AppendOutcomes.InsertManyAsync(transaction, outcomeDocuments, new InsertManyOptions { IsOrdered = true }, token);
            return appendResult;
        }, cancellationToken);

        await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.AppendAfterCommitBeforeAcknowledgement, cancellationToken);
        return result;
    }

    private async ValueTask<DiagnosticRecordPage> QueryCoreAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = DiagnosticRecordQuerySnapshot.Capture(query, _definition.Limits.MaxPredicateNodes);
        DiagnosticRecordQueryValidator.Validate(query, _definition, this);
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await ExecuteSnapshotReadAsync(session, async (transaction, token) =>
        {
            var stream = await Streams.Find(transaction, Builders<BsonDocument>.Filter.Eq("_id", StreamKey(query.Scope, query.Stream)))
                .FirstOrDefaultAsync(token);
            var snapshot = query.Continuation?.SnapshotHighWater ?? new((stream?.GetValue("next_cursor", 0).ToInt64() ?? 0).ToString(CultureInfo.InvariantCulture));
            await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.QueryAfterHighWaterRead, token);
            var pipeline = BuildQueryPipeline(query, ParseCursor(snapshot));
            var facet = await AggregateSingleAsync(transaction, pipeline, token, QueryIndexHint(query));
            var documents = facet["page"].AsBsonArray.Select(item => item.AsBsonDocument).ToList();
            var hasMore = documents.Count > query.Limit;
            if (hasMore)
                documents.RemoveAt(documents.Count - 1);
            var records = DiagnosticRecordSnapshot.Capture(documents.Select(ReadRecord).ToArray());
            var exactCount = query.IncludeExactCount && facet["count"].AsBsonArray.FirstOrDefault() is { } count
                ? count["value"].ToInt64()
                : (long?)null;
            DiagnosticRecordContinuation? next = null;
            var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
            if (hasMore && records.Count > 0)
            {
                var last = records[^1];
                next = new(
                    snapshot,
                    last.Cursor,
                    DiagnosticRequestFingerprint.ForQuery(query with { Continuation = null }, _definition),
                    order.Field is null ? null : Scalar(last, order.Field));
            }
            return new DiagnosticRecordPage(records, next, exactCount);
        }, cancellationToken);
    }

    /// <summary>Returns MongoDB's native query-planner evidence for the executable bounded query shape.</summary>
    internal async ValueTask<BsonDocument> ExplainQueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        query = DiagnosticRecordQuerySnapshot.Capture(query, _definition.Limits.MaxPredicateNodes);
        DiagnosticRecordQueryValidator.Validate(query, _definition, this);
        var stream = await Streams.Find(Builders<BsonDocument>.Filter.Eq("_id", StreamKey(query.Scope, query.Stream)))
            .FirstOrDefaultAsync(cancellationToken);
        var snapshot = query.Continuation?.SnapshotHighWater ?? new((stream?.GetValue("next_cursor", 0).ToInt64() ?? 0).ToString(CultureInfo.InvariantCulture));
        var aggregate = new BsonDocument
        {
            { "aggregate", MongoDbDiagnosticRecordNames.Records },
            { "pipeline", new BsonArray(BuildQueryPipeline(query, ParseCursor(snapshot))) },
            { "cursor", new BsonDocument() },
            { "collation", new BsonDocument("locale", "simple") }
        };
        if (QueryIndexHint(query) is { } hint)
            aggregate.Add("hint", hint);
        return await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "explain", aggregate }, { "verbosity", "queryPlanner" } },
            cancellationToken: cancellationToken);
    }

    /// <summary>Reads persisted string comparison keys for provider-native binary-semantics evidence.</summary>
    internal async ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", Render(ScopeFilter(scope, stream))),
            new BsonDocument("$unwind", "$query_values"),
            new BsonDocument("$match", new BsonDocument("query_values.name", field)),
            new BsonDocument("$sort", new BsonDocument { { "cursor", 1 }, { "query_values.comparison_key", 1 } }),
            new BsonDocument("$project", new BsonDocument { { "_id", 0 }, { "key", "$query_values.comparison_key" } })
        };
        var documents = await Records.Aggregate<BsonDocument>(pipeline).ToListAsync(cancellationToken);
        return Array.AsReadOnly(documents.Select(document => document["key"].AsString).ToArray());
    }

    internal async ValueTask<long> CountOperationRowsAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        await (kind == DiagnosticOperationKind.Append ? AppendOperations : TrimOperations)
            .CountDocumentsAsync(ScopeFilter(scope, stream), cancellationToken: cancellationToken);

    private async ValueTask<DiagnosticStreamStatistics> InspectCoreAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, _definition);
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await ExecuteSnapshotReadAsync(session,
            (transaction, token) => StatisticsAsync(transaction, request.Scope, request.Stream, token, invokeInspectHook: true),
            cancellationToken);
    }

    private async ValueTask<DiagnosticTrimResult> TrimCoreAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, _definition);
        await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.TrimBeforeCommit, cancellationToken);
        var providerNow = await AdvanceProviderClockAsync(cancellationToken);
        using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        var result = await ExecuteTransactionAsync(session, DiagnosticOperationKind.Trim, request.Stream, request.OperationId, async (transaction, token) =>
        {
            var operationId = OperationKey(request.Scope, request.Stream, request.OperationId);
            var prior = await TrimOperations.Find(transaction, Builders<BsonDocument>.Filter.Eq("_id", operationId))
                .FirstOrDefaultAsync(token);
            if (prior is not null)
                return ReplayTrim(prior, request, providerNow);

            DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(request, _definition, providerNow);
            await CleanupExpiredOperationsAsync(transaction, TrimOperations, DiagnosticOperationKind.Trim, providerNow, token);
            var filter = ScopeFilter(request.Scope, request.Stream);
            var selection = await AggregateSingleAsync(transaction, BuildTrimSelectionPipeline(request), token);
            var examined = selection["count"].AsBsonArray.FirstOrDefault() is { } count
                ? count["value"].ToInt64()
                : 0;
            var deleteCount = Math.Max(0, examined - request.KeepNewest);
            if (deleteCount > 0)
            {
                var boundary = selection["boundary"].AsBsonArray.Single().AsBsonDocument;
                await Records.DeleteManyAsync(transaction,
                    filter & Builders<BsonDocument>.Filter.Lte("cursor", boundary["cursor"].ToInt64()),
                    new DeleteOptions(), token);
                await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.TrimAfterRecordDeletedBeforeCommit, token);
            }
            var statistics = await StatisticsAsync(transaction, request.Scope, request.Stream, token);
            var trimResult = new DiagnosticTrimResult(
                DiagnosticTrimStatus.Completed,
                new(examined),
                new(deleteCount),
                statistics);
            await TrimOperations.InsertOneAsync(transaction,
                TrimLedgerDocument(operationId, request, trimResult, providerNow), cancellationToken: token);
            return trimResult;
        }, cancellationToken);

        await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.TrimAfterCommitBeforeAcknowledgement, cancellationToken);
        return result;
    }

    internal async ValueTask<BsonDocument> ExplainTrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        DiagnosticRecordRequestValidator.Validate(request, _definition);
        var aggregate = new BsonDocument
        {
            { "aggregate", MongoDbDiagnosticRecordNames.Records },
            { "pipeline", new BsonArray(BuildTrimSelectionPipeline(request)) },
            { "cursor", new BsonDocument() },
            { "collation", new BsonDocument("locale", "simple") }
        };
        return await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "explain", aggregate }, { "verbosity", "queryPlanner" } },
            cancellationToken: cancellationToken);
    }

    private IReadOnlyList<BsonDocument> BuildTrimSelectionPipeline(DiagnosticTrimRequest request) =>
    [
        new("$match", Render(ScopeFilter(request.Scope, request.Stream))),
        new("$facet", new BsonDocument
        {
            { "count", new BsonArray { new BsonDocument("$count", "value") } },
            { "boundary", new BsonArray
                {
                    new BsonDocument("$sort", new BsonDocument("cursor", -1)),
                    new BsonDocument("$skip", request.KeepNewest),
                    new BsonDocument("$limit", 1),
                    new BsonDocument("$project", new BsonDocument { { "_id", 0 }, { "cursor", 1 } })
                }
            }
        })
    ];

    private async Task<DateTimeOffset> AdvanceProviderClockAsync(CancellationToken cancellationToken)
    {
        var ticks = _timeProvider.GetUtcNow().UtcTicks;
        while (true)
        {
            try
            {
                var state = await ProviderState.FindOneAndUpdateAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", "provider-clock-v1"),
                    Builders<BsonDocument>.Update.Max("utc_ticks", ticks),
                    new() { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                    cancellationToken);
                return new(state["utc_ticks"].ToInt64(), TimeSpan.Zero);
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Another first writer inserted the singleton between the upsert match and insert.
            }
        }
    }

    private async Task<T> ExecuteTransactionAsync<T>(
        IClientSessionHandle session,
        DiagnosticOperationKind operationKind,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        Func<IClientSessionHandle, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                ReadPreference.Primary,
                WriteConcern.WMajority));
            var commitAmbiguous = false;
            try
            {
                var result = await operation(session, cancellationToken);
                while (true)
                {
                    try
                    {
                        await session.CommitTransactionAsync(cancellationToken);
                        return result;
                    }
                    catch (MongoException exception) when (exception.HasErrorLabel("UnknownTransactionCommitResult"))
                    {
                        commitAmbiguous = true;
                        await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.CommitResultUnknown, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                            throw new DiagnosticAcknowledgementLostException(operationKind, stream, operationId, exception);
                        // The callback must not run twice for an unknown commit result; retry only commit.
                    }
                }
            }
            catch (DiagnosticAcknowledgementLostException)
            {
                throw;
            }
            catch (Exception exception) when (commitAmbiguous)
            {
                throw new DiagnosticAcknowledgementLostException(operationKind, stream, operationId, exception);
            }
            catch (MongoException exception) when (
                exception.HasErrorLabel("TransientTransactionError") &&
                !cancellationToken.IsCancellationRequested)
            {
                await AbortTransactionIgnoringFailureAsync(session, CancellationToken.None);
                retryAttempt++;
                var maximumDelay = Math.Min(100, 2 << Math.Min(retryAttempt, 5));
                await Task.Delay(Random.Shared.Next(1, maximumDelay + 1), cancellationToken);
            }
            catch
            {
                await AbortTransactionIgnoringFailureAsync(session, CancellationToken.None);
                throw;
            }
        }
    }

    private static async Task AbortTransactionIgnoringFailureAsync(
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            return;
        try
        {
            await session.AbortTransactionAsync(cancellationToken);
        }
        catch (MongoException)
        {
            // The original operation error is authoritative.
        }
    }

    private static async Task<T> ExecuteSnapshotReadAsync<T>(
        IClientSessionHandle session,
        Func<IClientSessionHandle, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                ReadPreference.Primary,
                WriteConcern.WMajority));
            try
            {
                var result = await operation(session, cancellationToken);
                while (true)
                {
                    try
                    {
                        await session.CommitTransactionAsync(cancellationToken);
                        return result;
                    }
                    catch (MongoException exception) when (
                        exception.HasErrorLabel("UnknownTransactionCommitResult") &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        // A read result is returned only after MongoDB acknowledges the snapshot transaction.
                    }
                }
            }
            catch (MongoException exception) when (
                exception.HasErrorLabel("TransientTransactionError") &&
                !cancellationToken.IsCancellationRequested)
            {
                await AbortTransactionIgnoringFailureAsync(session, CancellationToken.None);
                retryAttempt++;
                await Task.Delay(Random.Shared.Next(1, Math.Min(100, 2 << Math.Min(retryAttempt, 5)) + 1), cancellationToken);
            }
            catch
            {
                await AbortTransactionIgnoringFailureAsync(session, CancellationToken.None);
                throw;
            }
        }
    }

    private async Task CleanupExpiredOperationsAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> collection,
        DiagnosticOperationKind kind,
        DateTimeOffset providerNow,
        CancellationToken cancellationToken)
    {
        var expiredOutcomes = await collection.Find(session,
                Builders<BsonDocument>.Filter.Eq("has_outcome", true) &
                Builders<BsonDocument>.Filter.Lte("outcome_expires_at_ticks", providerNow.UtcTicks))
            .Sort(Builders<BsonDocument>.Sort.Ascending("outcome_expires_at_ticks").Ascending("_id"))
            .Limit(CleanupBatchSize)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        if (expiredOutcomes.Count > 0)
        {
            var ids = expiredOutcomes.Select(document => document["_id"]).ToArray();
            if (kind == DiagnosticOperationKind.Append)
                await AppendOutcomes.DeleteManyAsync(session,
                    Builders<BsonDocument>.Filter.In("operation_id", ids), new DeleteOptions(), cancellationToken);
            var minimalTombstone = Builders<BsonDocument>.Update
                .Set("has_outcome", false)
                .Unset("fingerprint")
                .Unset("record_count")
                .Unset("cursor_high_water")
                .Unset("logical_high_water")
                .Unset("examined")
                .Unset("deleted")
                .Unset("statistics");
            await collection.UpdateManyAsync(session,
                Builders<BsonDocument>.Filter.In("_id", ids), minimalTombstone, cancellationToken: cancellationToken);
        }

        var expired = await collection.Find(session, Builders<BsonDocument>.Filter.Lt("tombstone_until_ticks", providerNow.UtcTicks))
            .Sort(Builders<BsonDocument>.Sort.Ascending("tombstone_until_ticks").Ascending("_id"))
            .Limit(CleanupBatchSize)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
            await collection.DeleteManyAsync(session,
                Builders<BsonDocument>.Filter.In("_id", expired.Select(x => x["_id"])), new DeleteOptions(), cancellationToken);
    }

    private async Task<DiagnosticStreamStatistics> StatisticsAsync(
        IClientSessionHandle session,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken,
        bool invokeInspectHook = false)
    {
        var filter = ScopeFilter(scope, stream);
        var streamFilter = Builders<BsonDocument>.Filter.Eq("_id", StreamKey(scope, stream));
        var state = await Streams.Find(session, streamFilter).FirstOrDefaultAsync(cancellationToken);
        if (invokeInspectHook)
            await _interceptAsync(MongoDbDiagnosticRecordExecutionPoint.InspectAfterStreamStateRead, cancellationToken);
        var count = await Records.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken);
        var max = await Records.Find(session, filter).Sort(Builders<BsonDocument>.Sort.Descending("cursor"))
            .Limit(1).FirstOrDefaultAsync(cancellationToken);
        var highWater = state?.GetValue("next_cursor", 0).ToInt64() ?? 0;
        return new(
            new(count),
            max is null ? null : new(max["cursor"].ToInt64().ToString(CultureInfo.InvariantCulture)),
            highWater == 0 ? null : new(highWater.ToString(CultureInfo.InvariantCulture)),
            state is not null && state.Contains("logical_high_water") ? ReadFieldValue(state["logical_high_water"].AsBsonDocument) : null);
    }

    private IReadOnlyList<BsonDocument> BuildQueryPipeline(DiagnosticRecordQuery query, long snapshotCursor)
    {
        var filter = ScopeFilter(query.Scope, query.Stream) & Builders<BsonDocument>.Filter.Lte("cursor", snapshotCursor);
        if (query.Predicate is not null)
            filter &= PredicateFilter(query.Predicate);
        if (query.Order?.Field is { } orderedField)
            filter &= Builders<BsonDocument>.Filter.Exists(SortPath(orderedField));
        if (query.LatestPerKeyField is { } latestField)
            filter &= Builders<BsonDocument>.Filter.Exists(SortPath(latestField));

        var pipeline = new List<BsonDocument> { new("$match", Render(filter)) };
        if (query.LatestPerKeyField is { } latest)
        {
            pipeline.Add(new("$sort", new BsonDocument
            {
                { SortPrefixPath(latest), 1 }, { SortPath(latest), 1 }, { "cursor", -1 }
            }));
            pipeline.Add(new("$group", new BsonDocument
            {
                { "_id", $"${SortPath(latest)}" },
                { "record", new BsonDocument("$first", "$$ROOT") }
            }));
            pipeline.Add(new("$replaceWith", "$record"));
        }

        var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
        pipeline.Add(new("$sort", SortDocument(order)));
        var page = new BsonArray();
        if (query.Continuation is { } continuation)
            page.Add(new BsonDocument("$match", Render(ContinuationFilter(continuation, order))));
        page.Add(new BsonDocument("$limit", query.Limit + 1));
        var count = query.IncludeExactCount
            ? new BsonArray { new BsonDocument("$count", "value") }
            : new BsonArray { new BsonDocument("$match", new BsonDocument("_id", new BsonDocument("$exists", false))) };
        pipeline.Add(new("$facet", new BsonDocument { { "page", page }, { "count", count } }));
        return pipeline;
    }

    private FilterDefinition<BsonDocument> PredicateFilter(DiagnosticRecordPredicate predicate) => predicate switch
    {
        DiagnosticRecordPredicate.All all => Builders<BsonDocument>.Filter.And(all.Predicates.Select(PredicateFilter)),
        DiagnosticRecordPredicate.Any any => Builders<BsonDocument>.Filter.Or(any.Predicates.Select(PredicateFilter)),
        DiagnosticRecordPredicate.Comparison comparison => ComparisonFilter(comparison),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    private FilterDefinition<BsonDocument> ComparisonFilter(DiagnosticRecordPredicate.Comparison comparison)
    {
        var field = DiagnosticRecordFieldResolver.Resolve(_definition, comparison.Field)!;
        if (StringComparer.Ordinal.Equals(comparison.Field, DiagnosticRecordFieldNames.OccurredAt))
            return ScalarComparison("occurred_at_ticks", field, comparison);
        if (field.Type == DiagnosticFieldType.String)
            return StringComparisonFilter(field, comparison);
        var element = new BsonDocument { { "name", comparison.Field }, { "type", (int)field.Type } };
        var values = comparison.Values.Select(value => QueryValue(value, field)).ToArray();
        element["native"] = comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => values[0],
            DiagnosticPredicateOperator.In => new BsonDocument("$in", new BsonArray(values)),
            DiagnosticPredicateOperator.RangeInclusive => new BsonDocument { { "$gte", values[0] }, { "$lte", values[1] } },
            _ => throw new ArgumentOutOfRangeException()
        };
        return new BsonDocument("query_values", new BsonDocument("$elemMatch", element));
    }

    private static FilterDefinition<BsonDocument> StringComparisonFilter(
        DiagnosticFieldDefinition field,
        DiagnosticRecordPredicate.Comparison comparison)
    {
        BsonDocument Element(string keyName, BsonValue condition, string? hash = null)
        {
            var element = new BsonDocument
            {
                { "name", comparison.Field },
                { "type", (int)field.Type },
                { keyName, condition }
            };
            if (hash is not null)
                element.InsertAt(2, new("comparison_key_hash", hash));
            return new("query_values", new BsonDocument("$elemMatch", element));
        }

        var projections = comparison.Values
            .Select(value => DiagnosticStringComparisonKey.Project(value.CanonicalValue, field.CasePolicy))
            .ToArray();
        return comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => Element(
                "comparison_key",
                projections[0].ComparisonKey,
                projections[0].ComparisonKeyHash),
            DiagnosticPredicateOperator.In => new BsonDocument(
                "$or",
                new BsonArray(projections.Select(projection => Element(
                    "comparison_key",
                    projection.ComparisonKey,
                    projection.ComparisonKeyHash)))),
            DiagnosticPredicateOperator.RangeInclusive => Element(
                "comparison_key",
                new BsonDocument { { "$gte", projections[0].ComparisonKey }, { "$lte", projections[1].ComparisonKey } }),
            DiagnosticPredicateOperator.Contains => Element(
                "search_key",
                new BsonRegularExpression(
                    Regex.Escape(projections[0].SearchKey),
                    "")),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static FilterDefinition<BsonDocument> ScalarComparison(
        string path,
        DiagnosticFieldDefinition field,
        DiagnosticRecordPredicate.Comparison comparison)
    {
        var values = comparison.Values.Select(value => QueryValue(value, field)).ToArray();
        return comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => Builders<BsonDocument>.Filter.Eq(path, values[0]),
            DiagnosticPredicateOperator.In => Builders<BsonDocument>.Filter.In(path, values),
            DiagnosticPredicateOperator.RangeInclusive => Builders<BsonDocument>.Filter.Gte(path, values[0]) & Builders<BsonDocument>.Filter.Lte(path, values[1]),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private FilterDefinition<BsonDocument> ContinuationFilter(DiagnosticRecordContinuation continuation, DiagnosticRecordOrder order)
    {
        var cursor = ParseCursor(continuation.LastCursor);
        if (order.Field is null)
            return order.Direction == DiagnosticSortDirection.Ascending
                ? Builders<BsonDocument>.Filter.Gt("cursor", cursor)
                : Builders<BsonDocument>.Filter.Lt("cursor", cursor);
        var field = DiagnosticRecordFieldResolver.Resolve(_definition, order.Field)!;
        var path = SortPath(order.Field);
        var prefixPath = SortPrefixPath(order.Field);
        BsonValue value;
        BsonValue prefix;
        if (field.Type == DiagnosticFieldType.String)
        {
            var projection = DiagnosticStringComparisonKey.Project(
                continuation.LastOrderValue!.Value.CanonicalValue,
                field.CasePolicy);
            value = projection.ComparisonKey;
            prefix = projection.ComparisonKeyPrefix;
        }
        else
        {
            value = NativeValue(continuation.LastOrderValue!.Value, field);
            prefix = value;
        }
        return order.Direction == DiagnosticSortDirection.Ascending
            ? Builders<BsonDocument>.Filter.Gt(prefixPath, prefix) |
              (Builders<BsonDocument>.Filter.Eq(prefixPath, prefix) &
               (Builders<BsonDocument>.Filter.Gt(path, value) |
                (Builders<BsonDocument>.Filter.Eq(path, value) & Builders<BsonDocument>.Filter.Gt("cursor", cursor))))
            : Builders<BsonDocument>.Filter.Lt(prefixPath, prefix) |
              (Builders<BsonDocument>.Filter.Eq(prefixPath, prefix) &
               (Builders<BsonDocument>.Filter.Lt(path, value) |
                (Builders<BsonDocument>.Filter.Eq(path, value) & Builders<BsonDocument>.Filter.Lt("cursor", cursor))));
    }

    private BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(Records.DocumentSerializer, Records.Settings.SerializerRegistry));

    private async Task<BsonDocument> AggregateSingleAsync(
        IClientSessionHandle session,
        IReadOnlyList<BsonDocument> stages,
        CancellationToken cancellationToken,
        string? hint = null)
    {
        PipelineDefinition<BsonDocument, BsonDocument> pipeline = stages.ToArray();
        using var cursor = await Records.AggregateAsync(
            session,
            pipeline,
            new AggregateOptions { Collation = Collation.Simple, Hint = hint },
            cancellationToken);
        return await cursor.SingleAsync(cancellationToken);
    }

    private static string? QueryIndexHint(DiagnosticRecordQuery query)
    {
        if (query.Predicate is not null && ContainsSubstringPredicate(query.Predicate))
            return "ix_groundwork_diagnostic_records_scope_fields";
        if (query.Predicate is not null)
            return null;
        if (query.LatestPerKeyField is { } latest)
            return $"ix_groundwork_diagnostic_records_latest_{SortKey(latest)}";
        return query.Order?.Field is { } order
            ? $"ix_groundwork_diagnostic_records_order_{SortKey(order)}"
            : null;
    }

    private static bool ContainsSubstringPredicate(DiagnosticRecordPredicate predicate) => predicate switch
    {
        DiagnosticRecordPredicate.Comparison { Operator: DiagnosticPredicateOperator.Contains } => true,
        DiagnosticRecordPredicate.All all => all.Predicates.Any(ContainsSubstringPredicate),
        DiagnosticRecordPredicate.Any any => any.Predicates.Any(ContainsSubstringPredicate),
        _ => false
    };

    private static BsonDocument SortDocument(DiagnosticRecordOrder order)
    {
        var direction = order.Direction == DiagnosticSortDirection.Ascending ? 1 : -1;
        return order.Field is null
            ? new("cursor", direction)
            : new()
            {
                { SortPrefixPath(order.Field), direction },
                { SortPath(order.Field), direction },
                { "cursor", direction }
            };
    }

    private static FilterDefinition<BsonDocument> ScopeFilter(DiagnosticStorageScope scope, DiagnosticStreamId stream) =>
        Builders<BsonDocument>.Filter.Eq("tenant_id", scope.TenantId) &
        Builders<BsonDocument>.Filter.Eq("scope_id", scope.ScopeId) &
        Builders<BsonDocument>.Filter.Eq("stream_id", stream.Value);

    private BsonDocument RecordDocument(DiagnosticStorageScope scope, DiagnosticStreamId stream, DiagnosticRecord record)
    {
        var fields = record.Fields ?? new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>();
        var queryValues = new BsonArray();
        var storedFields = new BsonArray();
        var sortKeys = new BsonDocument();
        foreach (var (name, values) in fields)
        {
            var definition = DiagnosticRecordFieldResolver.Resolve(_definition, name)!;
            var projections = definition.Type == DiagnosticFieldType.String
                ? values.Select(value => DiagnosticStringComparisonKey.Project(
                    value.CanonicalValue,
                    definition.CasePolicy)).ToArray()
                : null;
            storedFields.Add(new BsonDocument
            {
                { "name", name },
                { "values", new BsonArray(values.Select(FieldValueDocument)) }
            });
            for (var ordinal = 0; ordinal < values.Count; ordinal++)
            {
                var value = values[ordinal];
                var projection = projections is null ? default : projections[ordinal];
                var comparisonKey = projections is null
                    ? ComparisonKey(value, definition)
                    : projection.ComparisonKey;
                queryValues.Add(new BsonDocument
                {
                    { "name", name }, { "type", (int)value.Type },
                    { "comparison_key", comparisonKey },
                    { "comparison_key_prefix", projections is null
                        ? DiagnosticStringComparisonKey.CreateBoundedPrefix(comparisonKey)
                        : projection.ComparisonKeyPrefix },
                    { "comparison_key_hash", projections is null
                        ? DiagnosticStringComparisonKey.CreateHash(comparisonKey)
                        : projection.ComparisonKeyHash },
                    { "search_key", projections is null ? comparisonKey : projection.SearchKey },
                    { "native", NativeValue(value, definition) }
                });
            }
            if (definition.Cardinality == DiagnosticFieldCardinality.Scalar && values.Count > 0)
            {
                if (projections is not null)
                {
                    var projection = projections[0];
                    sortKeys[SortKey(name)] = projection.ComparisonKey;
                    sortKeys[SortPrefixKey(name)] = projection.ComparisonKeyPrefix;
                }
                else
                {
                    var native = NativeValue(values[0], definition);
                    sortKeys[SortKey(name)] = native;
                    sortKeys[SortPrefixKey(name)] = native;
                }
            }
        }
        sortKeys[SortKey(DiagnosticRecordFieldNames.OccurredAt)] = record.OccurredAt.UtcTicks;
        sortKeys[SortPrefixKey(DiagnosticRecordFieldNames.OccurredAt)] = record.OccurredAt.UtcTicks;
        return new()
        {
            { "tenant_id", scope.TenantId }, { "scope_id", scope.ScopeId }, { "stream_id", stream.Value },
            { "cursor", ParseCursor(record.Cursor) }, { "record_id", record.RecordId },
            { "occurred_at_ticks", record.OccurredAt.UtcTicks }, { "payload", record.Payload },
            { "fields_present", record.Fields is not null },
            { "fields", storedFields }, { "query_values", queryValues }, { "sort", sortKeys }
        };
    }

    private static DiagnosticRecord ReadRecord(BsonDocument document)
    {
        var fields = document["fields"].AsBsonArray.ToDictionary(
            item => item["name"].AsString,
            item => (IReadOnlyList<DiagnosticFieldValue>)Array.AsReadOnly(item["values"].AsBsonArray.Select(x => ReadFieldValue(x.AsBsonDocument)).ToArray()),
            StringComparer.Ordinal);
        return new(
            document["record_id"].AsString,
            new(document["occurred_at_ticks"].ToInt64(), TimeSpan.Zero),
            document["payload"].AsString,
            new(document["cursor"].ToInt64().ToString(CultureInfo.InvariantCulture)),
            document.GetValue("fields_present", fields.Count > 0).ToBoolean() ? fields : null);
    }

    private static BsonDocument FieldValueDocument(DiagnosticFieldValue value) => new()
    {
        { "type", (int)value.Type }, { "canonical", value.CanonicalValue }
    };

    private static DiagnosticFieldValue ReadFieldValue(BsonDocument document) =>
        new((DiagnosticFieldType)document["type"].ToInt32(), document["canonical"].AsString);

    private static BsonValue QueryValue(DiagnosticFieldValue value, DiagnosticFieldDefinition definition) =>
        definition.Type == DiagnosticFieldType.String
            ? ComparisonKey(value, definition)
            : NativeValue(value, definition);

    private static BsonValue NativeValue(DiagnosticFieldValue value, DiagnosticFieldDefinition definition) => value.Type switch
    {
        DiagnosticFieldType.String => value.CanonicalValue,
        DiagnosticFieldType.Int64 => long.Parse(value.CanonicalValue, CultureInfo.InvariantCulture),
        DiagnosticFieldType.Decimal => new Decimal128(decimal.Parse(value.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture)),
        DiagnosticFieldType.Boolean => bool.Parse(value.CanonicalValue),
        DiagnosticFieldType.Timestamp => DateTimeOffset.Parse(value.CanonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcTicks,
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    private static string ComparisonKey(DiagnosticFieldValue value, DiagnosticFieldDefinition definition) =>
        DiagnosticStringComparisonKey.Create(value.CanonicalValue, definition.CasePolicy);

    private DiagnosticFieldValue? MaxLogicalHighWater(DiagnosticFieldValue? current, IEnumerable<DiagnosticRecord> records)
    {
        if (_definition.LogicalHighWaterField is not { } name)
            return null;
        var definition = DiagnosticRecordFieldResolver.Resolve(_definition, name)!;
        foreach (var value in records.Select(record => Scalar(record, name)).Where(value => value is not null).Select(value => value!.Value))
            if (current is null || value.CompareTo(current.Value, definition.CasePolicy) > 0)
                current = value;
        return current;
    }

    private static DiagnosticFieldValue? ReadLogicalHighWater(BsonDocument stream) =>
        stream.Contains("logical_high_water") ? ReadFieldValue(stream["logical_high_water"].AsBsonDocument) : null;

    private static DiagnosticFieldValue? Scalar(DiagnosticRecord record, string field) =>
        StringComparer.Ordinal.Equals(field, DiagnosticRecordFieldNames.OccurredAt)
            ? DiagnosticFieldValue.Timestamp(record.OccurredAt)
            : record.Fields is not null && record.Fields.TryGetValue(field, out var values) && values.Count > 0 ? values[0] : null;

    private BsonDocument AppendLedgerDocument(
        string id,
        DiagnosticRecordBatch batch,
        DiagnosticAppendResult result,
        DateTimeOffset providerNow) => new()
    {
        { "_id", id }, { "fingerprint", batch.RequestFingerprint.Value },
        { "tenant_id", batch.Scope.TenantId }, { "scope_id", batch.Scope.ScopeId }, { "stream_id", batch.Stream.Value },
        { "has_outcome", true },
        { "record_count", result.Records.Count },
        { "outcome_expires_at_ticks", (providerNow + _definition.AppendIdempotencyWindow).UtcTicks },
        { "tombstone_until_ticks", TombstoneUntil(batch.OperationId, providerNow, _definition.AppendIdempotencyWindow).UtcTicks },
        { "cursor_high_water", ParseCursor(result.CommittedCursorHighWater) },
        { "logical_high_water", result.LogicalHighWater is null ? BsonNull.Value : FieldValueDocument(result.LogicalHighWater.Value) }
    };

    private BsonDocument AppendOutcomeDocument(
        string operationId,
        int ordinal,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticRecord record)
    {
        var document = RecordDocument(scope, stream, record);
        document["_id"] = $"{operationId}:{ordinal.ToString(CultureInfo.InvariantCulture)}";
        document["operation_id"] = operationId;
        document["ordinal"] = ordinal;
        return document;
    }

    private BsonDocument TrimLedgerDocument(
        string id,
        DiagnosticTrimRequest request,
        DiagnosticTrimResult result,
        DateTimeOffset providerNow) => new()
    {
        { "_id", id }, { "fingerprint", request.RequestFingerprint.Value },
        { "tenant_id", request.Scope.TenantId }, { "scope_id", request.Scope.ScopeId }, { "stream_id", request.Stream.Value },
        { "has_outcome", true },
        { "outcome_expires_at_ticks", (providerNow + _definition.TrimIdempotencyWindow).UtcTicks },
        { "tombstone_until_ticks", TombstoneUntil(request.OperationId, providerNow, _definition.TrimIdempotencyWindow).UtcTicks },
        { "examined", result.ExaminedCount.Value }, { "deleted", result.DeletedCount.Value },
        { "statistics", StatisticsDocument(result.Statistics) }
    };

    private static BsonDocument StatisticsDocument(DiagnosticStreamStatistics statistics) => new()
    {
        { "retained", statistics.RetainedCount.Value },
        { "max_retained", statistics.MaxRetainedCursor is null ? BsonNull.Value : ParseCursor(statistics.MaxRetainedCursor.Value) },
        { "cursor_high_water", statistics.LifetimeCommittedCursorHighWater is null ? BsonNull.Value : ParseCursor(statistics.LifetimeCommittedCursorHighWater.Value) },
        { "logical_high_water", statistics.LifetimeLogicalHighWater is null ? BsonNull.Value : FieldValueDocument(statistics.LifetimeLogicalHighWater.Value) }
    };

    private static DiagnosticStreamStatistics ReadStatistics(BsonDocument document) => new(
        new(document["retained"].ToInt64()),
        document["max_retained"].IsBsonNull ? null : new(document["max_retained"].ToInt64().ToString(CultureInfo.InvariantCulture)),
        document["cursor_high_water"].IsBsonNull ? null : new(document["cursor_high_water"].ToInt64().ToString(CultureInfo.InvariantCulture)),
        document["logical_high_water"].IsBsonNull ? null : ReadFieldValue(document["logical_high_water"].AsBsonDocument));

    private async Task<DiagnosticAppendResult> ReplayAppendAsync(
        IClientSessionHandle session,
        BsonDocument prior,
        DiagnosticRecordBatch batch,
        DateTimeOffset providerNow,
        CancellationToken cancellationToken)
    {
        if (!prior.GetValue("has_outcome", false).ToBoolean() ||
            providerNow.UtcTicks >= prior["outcome_expires_at_ticks"].ToInt64())
            throw new DiagnosticOperationExpiredException(DiagnosticOperationKind.Append, batch.OperationId);
        if (!StringComparer.Ordinal.Equals(prior["fingerprint"].AsString, batch.RequestFingerprint.Value))
            throw new DiagnosticOperationConflictException(DiagnosticOperationKind.Append, batch.OperationId);
        var outcomeDocuments = await AppendOutcomes
            .Find(session, Builders<BsonDocument>.Filter.Eq("operation_id", prior["_id"].AsString))
            .Sort(Builders<BsonDocument>.Sort.Ascending("ordinal"))
            .ToListAsync(cancellationToken);
        if (outcomeDocuments.Count != prior["record_count"].ToInt32())
            throw new InvalidOperationException(
                $"MongoDB append replay outcome '{prior["_id"].AsString}' is incomplete.");
        var records = DiagnosticRecordSnapshot.Capture(outcomeDocuments.Select(ReadRecord).ToArray());
        return new(
            DiagnosticAppendStatus.Replayed,
            records,
            new(prior["cursor_high_water"].ToInt64().ToString(CultureInfo.InvariantCulture)),
            prior["logical_high_water"].IsBsonNull ? null : ReadFieldValue(prior["logical_high_water"].AsBsonDocument));
    }

    private static DiagnosticTrimResult ReplayTrim(BsonDocument prior, DiagnosticTrimRequest request, DateTimeOffset providerNow)
    {
        if (!prior.GetValue("has_outcome", false).ToBoolean() ||
            providerNow.UtcTicks >= prior["outcome_expires_at_ticks"].ToInt64())
            throw new DiagnosticOperationExpiredException(DiagnosticOperationKind.Trim, request.OperationId);
        if (!StringComparer.Ordinal.Equals(prior["fingerprint"].AsString, request.RequestFingerprint.Value))
            throw new DiagnosticOperationConflictException(DiagnosticOperationKind.Trim, request.OperationId);
        return new(
            DiagnosticTrimStatus.Replayed,
            new(prior["examined"].ToInt64()),
            new(prior["deleted"].ToInt64()),
            ReadStatistics(prior["statistics"].AsBsonDocument));
    }

    private DateTimeOffset TombstoneUntil(DiagnosticOperationId operationId, DateTimeOffset committedAt, TimeSpan window)
    {
        var outcomeExpiry = committedAt + window;
        var admissionHorizon = operationId.IssuedAt + window + _definition.MaxOperationClockSkew;
        return outcomeExpiry >= admissionHorizon ? outcomeExpiry : admissionHorizon;
    }

    private static DiagnosticRecordValidationException ExistingRecordException(IEnumerable<string> ids) => new(
        ids.Distinct(StringComparer.Ordinal).Select(id =>
            new DiagnosticValidationError("append.record_id.exists", $"Record id '{id}' already exists in this scope and stream.", "records")).ToArray());

    private static string StreamKey(DiagnosticStorageScope scope, DiagnosticStreamId stream) =>
        $"{scope.TenantId.Length}:{scope.TenantId}{scope.ScopeId.Length}:{scope.ScopeId}{stream.Value.Length}:{stream.Value}";

    private static string OperationKey(DiagnosticStorageScope scope, DiagnosticStreamId stream, DiagnosticOperationId operationId) =>
        $"{StreamKey(scope, stream)}/{operationId.IssuedAt.ToUniversalTime():O}/{operationId.Nonce}";

    internal static string SortPath(string field) => $"sort.{SortKey(field)}";

    internal static string SortPrefixPath(string field) => $"sort.{SortPrefixKey(field)}";

    internal static string SortKey(string field) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(field)))[..24];

    internal static string SortPrefixKey(string field) => $"p{SortKey(field)}";

    private static long ParseCursor(DiagnosticCursor cursor) => long.Parse(cursor.Value, CultureInfo.InvariantCulture);
}
