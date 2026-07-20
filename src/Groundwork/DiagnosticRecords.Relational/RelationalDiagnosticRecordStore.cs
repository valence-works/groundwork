using System.Data.Common;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Groundwork.Provider.Relational;

namespace Groundwork.DiagnosticRecords.Relational;

/// <summary>
/// Shared transaction, idempotency, retention, and bounded-query kernel for relational providers.
/// A provider supplies only its session policy, SQL dialect, and schema materializer.
/// </summary>
public sealed class RelationalDiagnosticRecordStore :
    IDiagnosticRecordStore,
    IDiagnosticAppendHandler,
    IDiagnosticQueryHandler,
    IDiagnosticInspectHandler,
    IDiagnosticTrimHandler
{
    internal const int OperationCleanupBatchSize = 32;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly RelationalSessionFactory readSessions;
    private readonly RelationalSessionFactory writeSessions;
    private readonly DiagnosticRecordStreamDefinition definition;
    private readonly RelationalDiagnosticRecordDialect dialect;
    private readonly TimeProvider timeProvider;
    private readonly Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask> interceptAsync;
    private readonly RelationalDiagnosticRecordAdmission? admission;

    internal RelationalDiagnosticRecordStore(
        RelationalSessionFactory sessions,
        DiagnosticRecordStreamDefinition definition,
        RelationalDiagnosticRecordDialect dialect,
        TimeProvider? timeProvider = null)
        : this(sessions, sessions, definition, dialect, timeProvider, null, null)
    {
    }

    internal RelationalDiagnosticRecordStore(
        RelationalSessionFactory readSessions,
        RelationalSessionFactory writeSessions,
        DiagnosticRecordStreamDefinition definition,
        RelationalDiagnosticRecordDialect dialect,
        TimeProvider? timeProvider,
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? interceptAsync,
        Func<CancellationToken, Task>? admitAsync)
    {
        ArgumentNullException.ThrowIfNull(readSessions);
        ArgumentNullException.ThrowIfNull(writeSessions);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dialect);
        this.readSessions = readSessions;
        this.writeSessions = writeSessions;
        this.definition = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(this.definition);
        this.dialect = dialect;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.interceptAsync = interceptAsync ?? ((_, _) => ValueTask.CompletedTask);
        admission = admitAsync is null ? null : new(admitAsync);
        Handlers = new(this, this, this, this);
    }

    public DiagnosticRecordStoreHandlers Handlers { get; }

    public DiagnosticQueryHandlerCapabilities Capabilities { get; } = new(
        Enum.GetValues<DiagnosticPredicateOperator>().ToHashSet(),
        SupportsCursorOrder: true,
        SupportsFieldOrder: true,
        SupportsSnapshotContinuation: true,
        SupportsExactCount: true,
        SupportsLatestPerKey: true);

    public async ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        batch = DiagnosticRecordRequestSnapshot.Capture(batch);
        DiagnosticRecordRequestValidator.Validate(batch, definition);
        await EnsureAdmissionAsync(cancellationToken);
        await interceptAsync(RelationalDiagnosticRecordExecutionPoint.AppendBeforeCommit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await interceptAsync(RelationalDiagnosticRecordExecutionPoint.AppendBeforeStreamLock, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        OperationExecution<DiagnosticAppendResult> execution;
        try
        {
            if (dialect.UsesSessionScopedStreamLock)
            {
                execution = await ExecuteSessionLockedWriteAsync(
                    batch.Scope,
                    batch.Stream,
                    (connection, transaction, providerNow, ct) => AppendInTransactionAsync(
                        connection,
                        transaction,
                        batch,
                        providerNow,
                        streamLockHeld: true,
                        ct,
                        cancellationToken),
                    cancellationToken);
            }
            else
            {
                var providerNow = await AdvanceProviderClockAsync(cancellationToken);
                execution = await writeSessions.AutonomousExecutor.ExecuteAsync(
                    (connection, transaction, ct) => AppendInTransactionAsync(
                        connection,
                        transaction,
                        batch,
                        providerNow,
                        streamLockHeld: false,
                        ct,
                        cancellationToken),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw new OperationCanceledException("The relational diagnostic append was canceled by the caller.", exception, cancellationToken);
        }
        execution.ThrowIfFailed();
        await interceptAsync(RelationalDiagnosticRecordExecutionPoint.AppendAfterCommitBeforeAcknowledgement, cancellationToken);
        return execution.Value!;
    }

    public async ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = DiagnosticRecordQuerySnapshot.Capture(query, definition.Limits.MaxPredicateNodes);
        DiagnosticRecordQueryValidator.Validate(query, definition, this);
        await EnsureAdmissionAsync(cancellationToken);
        return await readSessions.AutonomousExecutor.ExecuteAsync(
            async (connection, transaction, ct) =>
            {
                var snapshot = query.Continuation is null
                    ? await ReadCursorHighWaterAsync(connection, transaction, query.Scope, query.Stream, ct)
                    : ParseCursor(query.Continuation.SnapshotHighWater);
                var builder = new RelationalDiagnosticQueryBuilder(definition, dialect);
                var pageCommand = builder.Build(query, snapshot);
                var rows = await ReadRecordRowsAsync(connection, transaction, pageCommand, ct);
                var pageRows = rows.Take(query.Limit).ToArray();
                var records = await HydrateRecordsAsync(connection, transaction, pageRows, ct);
                long? exactCount = null;
                if (query.IncludeExactCount)
                {
                    var countCommand = new RelationalDiagnosticQueryBuilder(definition, dialect)
                        .BuildCount(query, snapshot);
                    exactCount = await ExecuteScalarInt64Async(connection, transaction, countCommand, ct);
                }

                DiagnosticRecordContinuation? continuation = null;
                if (rows.Count > query.Limit)
                {
                    var last = pageRows[^1];
                    var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
                    continuation = new(
                        new(snapshot.ToString(CultureInfo.InvariantCulture)),
                        new(last.Cursor.ToString(CultureInfo.InvariantCulture)),
                        DiagnosticRequestFingerprint.ForQuery(query with { Continuation = null }, definition),
                        order.Field is null
                            ? null
                            : StringComparer.Ordinal.Equals(order.Field, DiagnosticRecordFieldNames.OccurredAt)
                                ? DiagnosticFieldValue.Timestamp(new DateTimeOffset(last.OccurredAtTicks, TimeSpan.Zero))
                                : ParseFieldValue(order.Field, last.OrderValue!));
                }

                return new DiagnosticRecordPage(DiagnosticRecordSnapshot.Capture(records), continuation, exactCount);
            },
            cancellationToken);
    }

    public async ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, definition);
        await EnsureAdmissionAsync(cancellationToken);
        return await readSessions.AutonomousExecutor.ExecuteAsync(
            (connection, transaction, ct) => ReadStatisticsAsync(connection, transaction, request.Scope, request.Stream, ct),
            cancellationToken);
    }

    public async ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticRecordRequestValidator.Validate(request, definition);
        await EnsureAdmissionAsync(cancellationToken);
        await interceptAsync(RelationalDiagnosticRecordExecutionPoint.TrimBeforeCommit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        OperationExecution<DiagnosticTrimResult> execution;
        try
        {
            if (dialect.UsesSessionScopedStreamLock)
            {
                execution = await ExecuteSessionLockedWriteAsync(
                    request.Scope,
                    request.Stream,
                    (connection, transaction, providerNow, ct) => TrimInTransactionAsync(
                        connection,
                        transaction,
                        request,
                        providerNow,
                        streamLockHeld: true,
                        ct,
                        cancellationToken),
                    cancellationToken);
            }
            else
            {
                var providerNow = await AdvanceProviderClockAsync(cancellationToken);
                execution = await writeSessions.AutonomousExecutor.ExecuteAsync(
                    (connection, transaction, ct) => TrimInTransactionAsync(
                        connection,
                        transaction,
                        request,
                        providerNow,
                        streamLockHeld: false,
                        ct,
                        cancellationToken),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw new OperationCanceledException("The relational diagnostic trim was canceled by the caller.", exception, cancellationToken);
        }
        execution.ThrowIfFailed();
        await interceptAsync(RelationalDiagnosticRecordExecutionPoint.TrimAfterCommitBeforeAcknowledgement, cancellationToken);
        return execution.Value!;
    }

    internal RelationalDiagnosticCommand BuildQueryCommand(DiagnosticRecordQuery query, long snapshotHighWater)
    {
        var command = new RelationalDiagnosticQueryBuilder(definition, dialect).Build(query, snapshotHighWater);
        return command with { CommandText = dialect.PrepareCommandText(command.CommandText) };
    }

    private ValueTask EnsureAdmissionAsync(CancellationToken cancellationToken) =>
        admission?.EnsureAsync(cancellationToken) ?? ValueTask.CompletedTask;

    internal RelationalDiagnosticCommand BuildTrimSelectionCommand(DiagnosticTrimRequest request)
    {
        var parameters = ScopeParameters(request.Scope, request.Stream);
        parameters.Add("keep", request.KeepNewest);
        var sql = $"""
            SELECT cursor
            FROM {dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "r")}
            WHERE tenant_id = {dialect.Parameter("tenant")}
              AND scope_id = {dialect.Parameter("scope")}
              AND stream_id = {dialect.Parameter("stream")}
            ORDER BY cursor DESC
            """;
        return new(dialect.PrepareCommandText(dialect.ApplyLimit(sql, "keep")), parameters);
    }

    internal RelationalDiagnosticCommand BuildStatisticsCommand(DiagnosticStreamInspectionRequest request)
    {
        var parameters = ScopeParameters(request.Scope, request.Stream);
        return new(dialect.PrepareCommandText($"""
            SELECT
                (SELECT COUNT(*) FROM {RelationalDiagnosticRecordSchema.RecordsTable} WHERE {ScopeWhere()}),
                (SELECT MAX(cursor) FROM {RelationalDiagnosticRecordSchema.RecordsTable} WHERE {ScopeWhere()}),
                next_cursor,
                logical_high_water_type,
                logical_high_water_value
            FROM {RelationalDiagnosticRecordSchema.StreamsTable}
            WHERE {ScopeWhere()};
            """), parameters);
    }

    private async Task<OperationExecution<DiagnosticAppendResult>> AppendInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DiagnosticRecordBatch batch,
        DateTimeOffset providerNow,
        bool streamLockHeld,
        CancellationToken cancellationToken,
        CancellationToken observerCancellationToken)
    {
        if (!streamLockHeld)
            await AcquireStreamLockAsync(connection, transaction, batch.Scope, batch.Stream, cancellationToken);
        // Once the stream writer boundary is held, finish or roll back at the staged hook instead
        // of interrupting an arbitrary command halfway through the atomic mutation.
        cancellationToken = CancellationToken.None;
        await CleanupExpiredOperationsAsync(connection, transaction, providerNow, cancellationToken);
        var prior = await ReadOperationAsync(connection, transaction, RelationalDiagnosticRecordSchema.AppendOperationsTable, batch.Scope, batch.Stream, batch.OperationId, cancellationToken);
        if (prior is not null)
        {
            var replay = await ResolvePriorOperationAsync<DiagnosticAppendResult>(
                connection, transaction, RelationalDiagnosticRecordSchema.AppendOperationsTable,
                batch.Scope, batch.Stream, batch.OperationId, batch.RequestFingerprint,
                DiagnosticOperationKind.Append, prior, providerNow, cancellationToken);
            if (replay.Exception is not null)
                return replay;
            return OperationExecution<DiagnosticAppendResult>.Success(replay.Value! with
            {
                Status = DiagnosticAppendStatus.Replayed,
                Records = DiagnosticRecordSnapshot.Capture(replay.Value.Records)
            });
        }

        try
        {
            DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(batch, definition, providerNow);
        }
        catch (Exception exception) when (exception is DiagnosticOperationExpiredException or DiagnosticOperationClockSkewException)
        {
            return OperationExecution<DiagnosticAppendResult>.Failure(exception);
        }

        await EnsureStreamAsync(connection, transaction, batch.Scope, batch.Stream, cancellationToken);
        var conflicts = await FindExistingRecordIdsAsync(connection, transaction, batch, cancellationToken);
        if (conflicts.Count > 0)
        {
            return OperationExecution<DiagnosticAppendResult>.Failure(new DiagnosticRecordValidationException(conflicts.Select(id =>
                new DiagnosticValidationError("append.record_id.exists", $"Record id '{id}' already exists in this scope and stream.", "records")).ToArray()));
        }

        var firstCursor = await AllocateCursorsAsync(connection, transaction, batch.Scope, batch.Stream, batch.Records.Count, cancellationToken);
        var records = new List<DiagnosticRecord>(batch.Records.Count);
        for (var index = 0; index < batch.Records.Count; index++)
        {
            var input = batch.Records[index];
            var cursor = firstCursor + index;
            await InsertRecordAsync(connection, transaction, batch.Scope, batch.Stream, cursor, input, cancellationToken);
            records.Add(new(input.RecordId, input.OccurredAt, input.Payload, new(cursor.ToString(CultureInfo.InvariantCulture)), input.Fields));
            await interceptAsync(RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit, observerCancellationToken);
            observerCancellationToken.ThrowIfCancellationRequested();
        }

        var logicalHighWater = await UpdateLogicalHighWaterAsync(connection, transaction, batch.Scope, batch.Stream, records, cancellationToken);
        var result = new DiagnosticAppendResult(
            DiagnosticAppendStatus.Committed,
            DiagnosticRecordSnapshot.Capture(records),
            records[^1].Cursor,
            logicalHighWater);
        await InsertOperationAsync(
            connection, transaction, RelationalDiagnosticRecordSchema.AppendOperationsTable,
            batch.Scope, batch.Stream, batch.OperationId, batch.RequestFingerprint,
            providerNow, definition.AppendIdempotencyWindow, result, cancellationToken);
        return OperationExecution<DiagnosticAppendResult>.Success(result);
    }

    private async Task<OperationExecution<DiagnosticTrimResult>> TrimInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DiagnosticTrimRequest request,
        DateTimeOffset providerNow,
        bool streamLockHeld,
        CancellationToken cancellationToken,
        CancellationToken observerCancellationToken)
    {
        if (!streamLockHeld)
            await AcquireStreamLockAsync(connection, transaction, request.Scope, request.Stream, cancellationToken);
        // Once the stream writer boundary is held, finish or roll back at the staged hook instead
        // of interrupting an arbitrary command halfway through the atomic mutation.
        cancellationToken = CancellationToken.None;
        await CleanupExpiredOperationsAsync(connection, transaction, providerNow, cancellationToken);
        var prior = await ReadOperationAsync(connection, transaction, RelationalDiagnosticRecordSchema.TrimOperationsTable, request.Scope, request.Stream, request.OperationId, cancellationToken);
        if (prior is not null)
        {
            var replay = await ResolvePriorOperationAsync<DiagnosticTrimResult>(
                connection, transaction, RelationalDiagnosticRecordSchema.TrimOperationsTable,
                request.Scope, request.Stream, request.OperationId, request.RequestFingerprint,
                DiagnosticOperationKind.Trim, prior, providerNow, cancellationToken);
            if (replay.Exception is not null)
                return replay;
            return OperationExecution<DiagnosticTrimResult>.Success(replay.Value! with { Status = DiagnosticTrimStatus.Replayed });
        }

        try
        {
            DiagnosticRecordRequestValidator.ValidateNewOperationAdmission(request, definition, providerNow);
        }
        catch (Exception exception) when (exception is DiagnosticOperationExpiredException or DiagnosticOperationClockSkewException)
        {
            return OperationExecution<DiagnosticTrimResult>.Failure(exception);
        }

        await EnsureStreamAsync(connection, transaction, request.Scope, request.Stream, cancellationToken);
        var examined = await CountRecordsAsync(connection, transaction, request.Scope, request.Stream, cancellationToken);
        var deleteCount = Math.Max(0, examined - request.KeepNewest);
        if (deleteCount > 0)
        {
            var deleteParameters = ScopeParameters(request.Scope, request.Stream);
            deleteParameters.Add("deleteCount", deleteCount);
            var cursorSelection = dialect.ApplyLimit(
                $"""
                SELECT cursor FROM {dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "r")}
                WHERE tenant_id = {dialect.Parameter("tenant")} AND scope_id = {dialect.Parameter("scope")} AND stream_id = {dialect.Parameter("stream")}
                ORDER BY cursor ASC
                """,
                "deleteCount");
            var fieldDelete = new RelationalDiagnosticCommand(
                $"DELETE FROM {RelationalDiagnosticRecordSchema.FieldsTable} WHERE tenant_id = {dialect.Parameter("tenant")} AND scope_id = {dialect.Parameter("scope")} AND stream_id = {dialect.Parameter("stream")} AND cursor IN ({cursorSelection});",
                deleteParameters);
            var recordDelete = fieldDelete with
            {
                CommandText = $"DELETE FROM {RelationalDiagnosticRecordSchema.RecordsTable} WHERE tenant_id = {dialect.Parameter("tenant")} AND scope_id = {dialect.Parameter("scope")} AND stream_id = {dialect.Parameter("stream")} AND cursor IN ({cursorSelection});"
            };
            await ExecuteNonQueryAsync(connection, transaction, fieldDelete, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, recordDelete, cancellationToken);
            await interceptAsync(RelationalDiagnosticRecordExecutionPoint.TrimAfterRecordDeletedBeforeCommit, observerCancellationToken);
            observerCancellationToken.ThrowIfCancellationRequested();
        }

        var statistics = await ReadStatisticsAsync(connection, transaction, request.Scope, request.Stream, cancellationToken);
        var result = new DiagnosticTrimResult(
            DiagnosticTrimStatus.Completed,
            new(examined),
            new(deleteCount),
            statistics);
        await InsertOperationAsync(
            connection, transaction, RelationalDiagnosticRecordSchema.TrimOperationsTable,
            request.Scope, request.Stream, request.OperationId, request.RequestFingerprint,
            providerNow, definition.TrimIdempotencyWindow, result, cancellationToken);
        return OperationExecution<DiagnosticTrimResult>.Success(result);
    }

    private Task<DateTimeOffset> AdvanceProviderClockAsync(CancellationToken cancellationToken) =>
        writeSessions.AutonomousExecutor.ExecuteAsync(
            (connection, transaction, ct) => AdvanceProviderClockInTransactionAsync(connection, transaction, ct),
            cancellationToken);

    private async Task<DateTimeOffset> AdvanceProviderClockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var providerNow = await AdvanceProviderClockInTransactionAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return providerNow;
    }

    private async Task<DateTimeOffset> AdvanceProviderClockInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var current = timeProvider.GetUtcNow().UtcTicks;
        var highWater = await ExecuteScalarInt64Async(
            connection,
            transaction,
            new(
                dialect.BuildProviderClockAdvance(RelationalDiagnosticRecordSchema.ProviderStateTable, "clock"),
                new Dictionary<string, object> { ["clock"] = current }),
            cancellationToken);
        return new DateTimeOffset(highWater, TimeSpan.Zero);
    }

    private Task<T> ExecuteSessionLockedWriteAsync<T>(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        Func<DbConnection, DbTransaction, DateTimeOffset, CancellationToken, Task<T>> writeAsync,
        CancellationToken cancellationToken) =>
        writeSessions.ExecuteAsync(async (connection, ct) =>
        {
            Exception? primaryFailure = null;
            try
            {
                await ExecuteSessionStreamLockCommandAsync(connection, dialect.BuildStreamLock(), scope, stream, ct);
                var providerNow = await AdvanceProviderClockAsync(connection, ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                var result = await writeAsync(connection, transaction, providerNow, ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await ReleaseSessionStreamLockAsync(
                        connection,
                        scope,
                        stream,
                        CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    try
                    {
                        dialect.InvalidateConnectionPool(connection);
                    }
                    catch (Exception invalidationFailure)
                    {
                        cleanupFailure.Data["Groundwork.DiagnosticRecords.PoolInvalidationFailure"] = invalidationFailure;
                    }

                    if (primaryFailure is null)
                        throw;
                    primaryFailure.Data["Groundwork.DiagnosticRecords.StreamUnlockFailure"] = cleanupFailure;
                }
            }
        }, cancellationToken);

    private async Task ReleaseSessionStreamLockAsync(
        DbConnection connection,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(
            connection,
            transaction: null,
            new(dialect.BuildStreamUnlock(), ScopeParameters(scope, stream)),
            cancellationToken);
        dialect.ValidateStreamUnlockResult(result);
    }

    private Task<int> ExecuteSessionStreamLockCommandAsync(
        DbConnection connection,
        string commandText,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            connection,
            transaction: null,
            new(commandText, ScopeParameters(scope, stream)),
            cancellationToken);

    private Task AcquireStreamLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        CancellationToken cancellationToken)
    {
        return ExecuteNonQueryAsync(
            connection,
            transaction,
            new(dialect.BuildStreamLock(), ScopeParameters(scope, stream)),
            cancellationToken);
    }

    private async Task CleanupExpiredOperationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset providerNow,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["cleanupBefore"] = providerNow.UtcTicks,
            ["cleanupBatch"] = OperationCleanupBatchSize
        };
        foreach (var table in new[]
                 {
                     RelationalDiagnosticRecordSchema.AppendOperationsTable,
                     RelationalDiagnosticRecordSchema.TrimOperationsTable
                 })
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                new(dialect.BuildOperationCleanup(table, "cleanupBefore", "cleanupBatch"), parameters),
                cancellationToken);
        }
    }

    private async Task<OperationExecution<T>> ResolvePriorOperationAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        DiagnosticRequestFingerprint fingerprint,
        DiagnosticOperationKind kind,
        OperationRow prior,
        DateTimeOffset providerNow,
        CancellationToken cancellationToken)
    {
        if (providerNow.UtcTicks >= prior.OutcomeExpiresAtTicks)
        {
            var parameters = OperationParameters(scope, stream, operationId);
            if (providerNow.UtcTicks > prior.TombstoneUntilTicks)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    new($"DELETE FROM {table} WHERE {OperationWhere()};", parameters), cancellationToken);
            }
            else if (!prior.IsTombstone)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    new($"UPDATE {table} SET fingerprint = NULL, result_json = NULL, is_tombstone = 1 WHERE {OperationWhere()};", parameters),
                    cancellationToken);
            }
            return OperationExecution<T>.Failure(new DiagnosticOperationExpiredException(kind, operationId));
        }
        if (prior.IsTombstone)
            return OperationExecution<T>.Failure(new DiagnosticOperationExpiredException(kind, operationId));
        if (!StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint.Value))
            return OperationExecution<T>.Failure(new DiagnosticOperationConflictException(kind, operationId));
        return OperationExecution<T>.Success(JsonSerializer.Deserialize<T>(prior.ResultJson!, SerializerOptions)!);
    }

    private async Task<OperationRow?> ReadOperationAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            new($"SELECT fingerprint, outcome_expires_at_ticks, tombstone_until_ticks, result_json, is_tombstone FROM {table} WHERE {OperationWhere()};",
                OperationParameters(scope, stream, operationId)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0)
            : null;
    }

    private async Task InsertOperationAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        DiagnosticRequestFingerprint fingerprint,
        DateTimeOffset committedAt,
        TimeSpan window,
        T result,
        CancellationToken cancellationToken)
    {
        var expires = committedAt + window;
        var admissionHorizon = operationId.IssuedAt + window + definition.MaxOperationClockSkew;
        var parameters = OperationParameters(scope, stream, operationId);
        parameters.Add("fingerprint", fingerprint.Value);
        parameters.Add("committed", committedAt.UtcTicks);
        parameters.Add("expires", expires.UtcTicks);
        parameters.Add("tombstone", (expires >= admissionHorizon ? expires : admissionHorizon).UtcTicks);
        parameters.Add("result", JsonSerializer.Serialize(result, SerializerOptions));
        await ExecuteNonQueryAsync(connection, transaction,
            new($"""
                INSERT INTO {table}
                (tenant_id, scope_id, stream_id, issued_at_ticks, nonce, fingerprint, committed_at_ticks, outcome_expires_at_ticks, tombstone_until_ticks, result_json, is_tombstone)
                VALUES ({dialect.Parameter("tenant")}, {dialect.Parameter("scope")}, {dialect.Parameter("stream")}, {dialect.Parameter("issued")}, {dialect.Parameter("nonce")}, {dialect.Parameter("fingerprint")}, {dialect.Parameter("committed")}, {dialect.Parameter("expires")}, {dialect.Parameter("tombstone")}, {dialect.Parameter("result")}, 0);
                """, parameters),
            cancellationToken);
    }

    private async Task EnsureStreamAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, CancellationToken cancellationToken)
    {
        var parameters = ScopeParameters(scope, stream);
        var exists = await ExecuteScalarNullableInt64Async(connection, transaction,
            new($"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE {ScopeWhere()};", parameters), cancellationToken);
        if (exists is null)
        {
            await ExecuteNonQueryAsync(connection, transaction,
                new($"INSERT INTO {RelationalDiagnosticRecordSchema.StreamsTable} (tenant_id, scope_id, stream_id, next_cursor) VALUES ({dialect.Parameter("tenant")}, {dialect.Parameter("scope")}, {dialect.Parameter("stream")}, 0);", parameters),
                cancellationToken);
        }
    }

    private async Task<long> AllocateCursorsAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, int count, CancellationToken cancellationToken)
    {
        var parameters = ScopeParameters(scope, stream);
        var current = (await ExecuteScalarNullableInt64Async(connection, transaction,
            new($"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE {ScopeWhere()};", parameters), cancellationToken))!.Value;
        parameters.Add("next", current + count);
        await ExecuteNonQueryAsync(connection, transaction,
            new($"UPDATE {RelationalDiagnosticRecordSchema.StreamsTable} SET next_cursor = {dialect.Parameter("next")} WHERE {ScopeWhere()};", parameters),
            cancellationToken);
        return current + 1;
    }

    private async Task<IReadOnlyList<string>> FindExistingRecordIdsAsync(DbConnection connection, DbTransaction transaction, DiagnosticRecordBatch batch, CancellationToken cancellationToken)
    {
        var conflicts = new List<string>();
        foreach (var chunk in batch.Records.Chunk(Math.Max(1, dialect.MaxParametersPerCommand - 3)))
        {
            var parameters = ScopeParameters(batch.Scope, batch.Stream);
            var ids = new List<string>(chunk.Length);
            for (var index = 0; index < chunk.Length; index++)
            {
                var name = $"record{index}";
                parameters.Add(name, chunk[index].RecordId);
                ids.Add(dialect.Parameter(name));
            }
            await using var command = CreateCommand(connection, transaction,
                new($"SELECT record_id FROM {RelationalDiagnosticRecordSchema.RecordsTable} WHERE {ScopeWhere()} AND record_id IN ({string.Join(", ", ids)});", parameters));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                conflicts.Add(reader.GetString(0));
        }
        return conflicts;
    }

    private async Task InsertRecordAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, long cursor, DiagnosticRecordInput input, CancellationToken cancellationToken)
    {
        var parameters = ScopeParameters(scope, stream);
        parameters.Add("cursor", cursor);
        parameters.Add("record", input.RecordId);
        parameters.Add("occurred", input.OccurredAt.UtcTicks);
        parameters.Add("payload", input.Payload);
        await ExecuteNonQueryAsync(connection, transaction,
            new($"INSERT INTO {RelationalDiagnosticRecordSchema.RecordsTable} (tenant_id, scope_id, stream_id, cursor, record_id, occurred_at_ticks, payload_json) VALUES ({dialect.Parameter("tenant")}, {dialect.Parameter("scope")}, {dialect.Parameter("stream")}, {dialect.Parameter("cursor")}, {dialect.Parameter("record")}, {dialect.Parameter("occurred")}, {dialect.Parameter("payload")});", parameters),
            cancellationToken);

        if (input.Fields is null)
            return;
        foreach (var field in input.Fields)
        {
            var fieldDefinition = DiagnosticRecordFieldResolver.Resolve(definition, field.Key)!;
            for (var ordinal = 0; ordinal < field.Value.Count; ordinal++)
            {
                var value = field.Value[ordinal];
                var stored = DiagnosticStoredFieldKeys.Create(value, fieldDefinition);
                var fieldParameters = ScopeParameters(scope, stream);
                fieldParameters.Add("cursor", cursor);
                fieldParameters.Add("field", field.Key);
                fieldParameters.Add("ordinal", ordinal);
                fieldParameters.Add("type", (int)value.Type);
                fieldParameters.Add("canonical", stored.CanonicalValue);
                fieldParameters.Add("comparison", stored.ComparisonKey);
                fieldParameters.Add("comparisonPrefix", stored.ComparisonKeyPrefix);
                fieldParameters.Add("comparisonHash", stored.ComparisonKeyHash);
                fieldParameters.Add("search", stored.SearchKey);
                await ExecuteNonQueryAsync(connection, transaction,
                    new($"INSERT INTO {RelationalDiagnosticRecordSchema.FieldsTable} (tenant_id, scope_id, stream_id, cursor, field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key) VALUES ({dialect.Parameter("tenant")}, {dialect.Parameter("scope")}, {dialect.Parameter("stream")}, {dialect.Parameter("cursor")}, {dialect.Parameter("field")}, {dialect.Parameter("ordinal")}, {dialect.Parameter("type")}, {dialect.Parameter("canonical")}, {dialect.Parameter("comparison")}, {dialect.Parameter("comparisonPrefix")}, {dialect.Parameter("comparisonHash")}, {dialect.Parameter("search")});", fieldParameters),
                    cancellationToken);
            }
        }
    }

    private async Task<DiagnosticFieldValue?> UpdateLogicalHighWaterAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, IReadOnlyList<DiagnosticRecord> records, CancellationToken cancellationToken)
    {
        if (definition.LogicalHighWaterField is not { } fieldName)
            return null;
        DiagnosticFieldValue? candidate = null;
        foreach (var value in records
                     .Select(record => record.Fields is not null && record.Fields.TryGetValue(fieldName, out var values) ? values[0] : (DiagnosticFieldValue?)null)
                     .Where(value => value is not null)
                     .Select(value => value!.Value))
        {
            if (candidate is null || value.CompareTo(candidate.Value, DiagnosticStringCasePolicy.Ordinal) > 0)
                candidate = value;
        }
        var statistics = await ReadStatisticsAsync(connection, transaction, scope, stream, cancellationToken);
        var current = statistics.LifetimeLogicalHighWater;
        var highWater = candidate is not null && (current is null || candidate.Value.CompareTo(current.Value, DiagnosticStringCasePolicy.Ordinal) > 0)
            ? candidate
            : current;
        if (highWater is not null)
        {
            var parameters = ScopeParameters(scope, stream);
            parameters.Add("type", (int)highWater.Value.Type);
            parameters.Add("value", highWater.Value.CanonicalValue);
            await ExecuteNonQueryAsync(connection, transaction,
                new($"UPDATE {RelationalDiagnosticRecordSchema.StreamsTable} SET logical_high_water_type = {dialect.Parameter("type")}, logical_high_water_value = {dialect.Parameter("value")} WHERE {ScopeWhere()};", parameters), cancellationToken);
        }
        return highWater;
    }

    private async Task<DiagnosticStreamStatistics> ReadStatisticsAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            BuildStatisticsCommand(new(scope, stream)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new(new(0), null, null, null);
        var retained = RelationalDiagnosticValueConversions.ToInt64(reader.GetValue(0));
        var max = reader.IsDBNull(1) ? (DiagnosticCursor?)null : new(reader.GetInt64(1).ToString(CultureInfo.InvariantCulture));
        var lifetime = reader.GetInt64(2) == 0 ? (DiagnosticCursor?)null : new(reader.GetInt64(2).ToString(CultureInfo.InvariantCulture));
        var logical = reader.IsDBNull(3)
            ? (DiagnosticFieldValue?)null
            : new DiagnosticFieldValue((DiagnosticFieldType)reader.GetInt64(3), reader.GetString(4));
        return new(new(retained), max, lifetime, logical);
    }

    private async Task<long> ReadCursorHighWaterAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, CancellationToken cancellationToken) =>
        await ExecuteScalarNullableInt64Async(connection, transaction,
            new($"SELECT next_cursor FROM {RelationalDiagnosticRecordSchema.StreamsTable} WHERE {ScopeWhere()};", ScopeParameters(scope, stream)), cancellationToken) ?? 0;

    private async Task<long> CountRecordsAsync(DbConnection connection, DbTransaction transaction, DiagnosticStorageScope scope, DiagnosticStreamId stream, CancellationToken cancellationToken) =>
        await ExecuteScalarInt64Async(connection, transaction,
            new($"SELECT COUNT(*) FROM {RelationalDiagnosticRecordSchema.RecordsTable} WHERE {ScopeWhere()};", ScopeParameters(scope, stream)), cancellationToken);

    private async Task<IReadOnlyList<RecordRow>> ReadRecordRowsAsync(DbConnection connection, DbTransaction transaction, RelationalDiagnosticCommand sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<RecordRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }

    private async Task<IReadOnlyList<DiagnosticRecord>> HydrateRecordsAsync(DbConnection connection, DbTransaction transaction, IReadOnlyList<RecordRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];
        var fieldsByCursor = new Dictionary<long, Dictionary<string, List<(int Ordinal, DiagnosticFieldValue Value)>>>();
        foreach (var chunk in rows.Chunk(Math.Max(1, dialect.MaxParametersPerCommand - 3)))
        {
            var cursors = chunk.Select((row, index) => (row.Cursor, Name: $"cursor{index}")).ToArray();
            var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tenant"] = rows[0].TenantId,
                ["scope"] = rows[0].ScopeId,
                ["stream"] = rows[0].StreamId
            };
            foreach (var cursor in cursors)
                parameters.Add(cursor.Name, cursor.Cursor);
            await using var command = CreateCommand(connection, transaction,
                new($"SELECT cursor, field_name, value_ordinal, field_type, canonical_value FROM {RelationalDiagnosticRecordSchema.FieldsTable} WHERE cursor IN ({string.Join(", ", cursors.Select(cursor => dialect.Parameter(cursor.Name)))}) AND tenant_id = {dialect.Parameter("tenant")} AND scope_id = {dialect.Parameter("scope")} AND stream_id = {dialect.Parameter("stream")} ORDER BY cursor, field_name, value_ordinal;",
                    parameters));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var cursor = reader.GetInt64(0);
                var field = reader.GetString(1);
                var ordinal = checked((int)reader.GetInt64(2));
                var fieldType = (DiagnosticFieldType)reader.GetInt64(3);
                var value = new DiagnosticFieldValue(
                    fieldType,
                    DiagnosticStoredFieldKeys.DecodeCanonical(fieldType, reader.GetString(4)));
                if (!fieldsByCursor.TryGetValue(cursor, out var byName))
                    fieldsByCursor[cursor] = byName = new(StringComparer.Ordinal);
                if (!byName.TryGetValue(field, out var values))
                    byName[field] = values = [];
                values.Add((ordinal, value));
            }
        }

        return rows.Select(row =>
        {
            IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>? fields = null;
            if (fieldsByCursor.TryGetValue(row.Cursor, out var byName))
            {
                fields = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>(
                    byName.ToDictionary(
                        entry => entry.Key,
                        entry => (IReadOnlyList<DiagnosticFieldValue>)Array.AsReadOnly(entry.Value.OrderBy(value => value.Ordinal).Select(value => value.Value).ToArray()),
                        StringComparer.Ordinal));
            }
            return new DiagnosticRecord(
                row.RecordId,
                new DateTimeOffset(row.OccurredAtTicks, TimeSpan.Zero),
                row.Payload,
                new(row.Cursor.ToString(CultureInfo.InvariantCulture)),
                fields);
        }).ToArray();
    }

    private DiagnosticFieldValue ParseFieldValue(string fieldName, string canonicalValue)
    {
        var field = DiagnosticRecordFieldResolver.Resolve(definition, fieldName)!;
        return new(field.Type, DiagnosticStoredFieldKeys.DecodeCanonical(field.Type, canonicalValue));
    }

    private static long ParseCursor(DiagnosticCursor cursor) => long.Parse(cursor.Value, CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        options.Converters.Add(new DiagnosticFieldValueJsonConverter());
        return options;
    }

    private static readonly IReadOnlyDictionary<string, object> EmptyParameters = new Dictionary<string, object>();

    private Dictionary<string, object> ScopeParameters(DiagnosticStorageScope scope, DiagnosticStreamId stream) => new(StringComparer.Ordinal)
    {
        ["tenant"] = scope.TenantId,
        ["scope"] = scope.ScopeId,
        ["stream"] = stream.Value
    };

    private Dictionary<string, object> OperationParameters(DiagnosticStorageScope scope, DiagnosticStreamId stream, DiagnosticOperationId operationId)
    {
        var parameters = ScopeParameters(scope, stream);
        parameters.Add("issued", operationId.IssuedAt.UtcTicks);
        parameters.Add("nonce", operationId.Nonce);
        return parameters;
    }

    private string ScopeWhere() => $"tenant_id = {dialect.Parameter("tenant")} AND scope_id = {dialect.Parameter("scope")} AND stream_id = {dialect.Parameter("stream")}";
    private string OperationWhere() => $"{ScopeWhere()} AND issued_at_ticks = {dialect.Parameter("issued")} AND nonce = {dialect.Parameter("nonce")}";

    private DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, RelationalDiagnosticCommand commandDefinition)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = dialect.PrepareCommandText(commandDefinition.CommandText);
        foreach (var parameter in commandDefinition.Parameters)
            AddParameter(command, parameter.Key, parameter.Value);
        return command;
    }

    private void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value;
        dialect.ConfigureParameter(parameter, value);
        command.Parameters.Add(parameter);
    }

    private async Task<int> ExecuteNonQueryAsync(DbConnection connection, DbTransaction? transaction, RelationalDiagnosticCommand command, CancellationToken cancellationToken)
    {
        await using var dbCommand = CreateCommand(connection, transaction, command);
        return await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> ExecuteScalarInt64Async(DbConnection connection, DbTransaction transaction, RelationalDiagnosticCommand command, CancellationToken cancellationToken) =>
        Convert.ToInt64(await ExecuteScalarAsync(connection, transaction, command, cancellationToken), CultureInfo.InvariantCulture);

    private async Task<long?> ExecuteScalarNullableInt64Async(DbConnection connection, DbTransaction transaction, RelationalDiagnosticCommand command, CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(connection, transaction, command, cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<object?> ExecuteScalarAsync(DbConnection connection, DbTransaction? transaction, RelationalDiagnosticCommand command, CancellationToken cancellationToken)
    {
        await using var dbCommand = CreateCommand(connection, transaction, command);
        return await dbCommand.ExecuteScalarAsync(cancellationToken);
    }

    private sealed record OperationRow(string? Fingerprint, long OutcomeExpiresAtTicks, long TombstoneUntilTicks, string? ResultJson, bool IsTombstone);
    private sealed record RecordRow(
        string TenantId,
        string ScopeId,
        string StreamId,
        long Cursor,
        string RecordId,
        long OccurredAtTicks,
        string Payload,
        string? OrderValue);

    private sealed record OperationExecution<T>(T? Value, Exception? Exception)
    {
        public static OperationExecution<T> Success(T value) => new(value, null);
        public static OperationExecution<T> Failure(Exception exception) => new(default, exception);
        public void ThrowIfFailed()
        {
            if (Exception is not null)
                ExceptionDispatchInfo.Capture(Exception).Throw();
        }
    }

    private sealed class DiagnosticFieldValueJsonConverter : System.Text.Json.Serialization.JsonConverter<DiagnosticFieldValue>
    {
        public override DiagnosticFieldValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return new(
                (DiagnosticFieldType)root.GetProperty(nameof(DiagnosticFieldValue.Type)).GetInt32(),
                root.GetProperty(nameof(DiagnosticFieldValue.CanonicalValue)).GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, DiagnosticFieldValue value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(DiagnosticFieldValue.Type), (int)value.Type);
            writer.WriteString(nameof(DiagnosticFieldValue.CanonicalValue), value.CanonicalValue);
            writer.WriteEndObject();
        }
    }
}

internal static class RelationalDiagnosticValueConversions
{
    public static long ToInt64(object value) =>
        Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
