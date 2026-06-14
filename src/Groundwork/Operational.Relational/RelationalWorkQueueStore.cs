using Groundwork.Provider.Relational;
using System.Data.Common;
using Groundwork.Operational.WorkQueue;

namespace Groundwork.Operational.Relational;

internal sealed class RelationalWorkQueueStore(RelationalExecutor executor, IOperationalClock clock)
    : RelationalOperationalStoreBase(executor, clock), IWorkQueueStore
{
    public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var sequence = await NextSequenceAsync(connection, transaction, request.Unit, request.PartitionKey, ct);
            var messageId = NewMessageId();
            var visibleAt = now + (request.InitialDelay ?? TimeSpan.Zero);

            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO groundwork_work_queue
                    (unit, message_id, partition_key, sequence, payload, attempt, max_attempts,
                     next_visible_utc, lease_token, lease_expires_utc, dead_lettered, enqueued_utc)
                VALUES
                    (@unit, @messageId, @partitionKey, @sequence, @payload, 0, @maxAttempts,
                     @nextVisible, NULL, NULL, 0, @enqueued);
                """);
            AddParameter(command, "unit", request.Unit);
            AddParameter(command, "messageId", messageId);
            AddParameter(command, "partitionKey", request.PartitionKey);
            AddParameter(command, "sequence", sequence);
            AddParameter(command, "payload", request.Payload);
            AddParameter(command, "maxAttempts", request.MaxAttempts);
            AddParameter(command, "nextVisible", Format(visibleAt));
            AddParameter(command, "enqueued", Format(now));
            await command.ExecuteNonQueryAsync(ct);

            return new EnqueueResult(messageId, sequence);
        }, cancellationToken);

    public Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var nowText = Format(now);
            var leaseExpiry = now + request.LeaseDuration;

            var candidates = new List<(string MessageId, string PartitionKey, long Sequence, string Payload, int Attempt)>();
            await using (var select = CreateCommand(connection, transaction, $"""
                SELECT message_id, partition_key, sequence, payload, attempt
                FROM groundwork_work_queue
                WHERE unit = @unit
                  AND dead_lettered = 0
                  AND next_visible_utc <= @now
                  {(request.PartitionKey is null ? "" : "AND partition_key = @partitionKey")}
                ORDER BY sequence
                LIMIT @batch;
                """))
            {
                AddParameter(select, "unit", request.Unit);
                AddParameter(select, "now", nowText);
                AddParameter(select, "batch", request.BatchSize);
                if (request.PartitionKey is not null)
                    AddParameter(select, "partitionKey", request.PartitionKey);

                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidates.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetInt32(4)));
                }
            }

            var claimed = new List<ClaimedMessage>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var leaseToken = NewLeaseToken();
                await using var update = CreateCommand(connection, transaction, """
                    UPDATE groundwork_work_queue
                    SET attempt = attempt + 1,
                        lease_token = @leaseToken,
                        next_visible_utc = @leaseExpiry,
                        lease_expires_utc = @leaseExpiry
                    WHERE unit = @unit AND message_id = @messageId AND next_visible_utc <= @now;
                    """);
                AddParameter(update, "leaseToken", leaseToken);
                AddParameter(update, "leaseExpiry", Format(leaseExpiry));
                AddParameter(update, "unit", request.Unit);
                AddParameter(update, "messageId", candidate.MessageId);
                AddParameter(update, "now", nowText);

                if (await update.ExecuteNonQueryAsync(ct) == 1)
                {
                    claimed.Add(new ClaimedMessage(
                        candidate.MessageId,
                        candidate.PartitionKey,
                        candidate.Sequence,
                        candidate.Payload,
                        candidate.Attempt + 1,
                        leaseToken,
                        leaseExpiry));
                }
            }

            return (IReadOnlyList<ClaimedMessage>)claimed;
        }, cancellationToken);

    public Task<AckResult> AcknowledgeAsync(AckRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            await using var delete = CreateCommand(connection, transaction, """
                DELETE FROM groundwork_work_queue
                WHERE unit = @unit AND message_id = @messageId AND lease_token = @leaseToken;
                """);
            AddParameter(delete, "unit", request.Unit);
            AddParameter(delete, "messageId", request.MessageId);
            AddParameter(delete, "leaseToken", request.LeaseToken);

            if (await delete.ExecuteNonQueryAsync(ct) == 1)
                return AckResult.Acknowledged;

            return await MessageExistsAsync(connection, transaction, request.Unit, request.MessageId, ct)
                ? AckResult.LeaseLost
                : AckResult.AlreadyAcknowledged;
        }, cancellationToken);

    public Task<AbandonResult> AbandonAsync(AbandonRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            int attempt;
            int maxAttempts;
            await using (var select = CreateCommand(connection, transaction, """
                SELECT attempt, max_attempts FROM groundwork_work_queue
                WHERE unit = @unit AND message_id = @messageId AND lease_token = @leaseToken;
                """))
            {
                AddParameter(select, "unit", request.Unit);
                AddParameter(select, "messageId", request.MessageId);
                AddParameter(select, "leaseToken", request.LeaseToken);
                await using var reader = await select.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return AbandonResult.LeaseLost;

                attempt = reader.GetInt32(0);
                maxAttempts = reader.GetInt32(1);
            }

            if (attempt >= maxAttempts)
            {
                await using var deadLetter = CreateCommand(connection, transaction, """
                    UPDATE groundwork_work_queue
                    SET dead_lettered = 1, lease_token = NULL, lease_expires_utc = NULL
                    WHERE unit = @unit AND message_id = @messageId;
                    """);
                AddParameter(deadLetter, "unit", request.Unit);
                AddParameter(deadLetter, "messageId", request.MessageId);
                await deadLetter.ExecuteNonQueryAsync(ct);
                return new AbandonResult(AbandonStatus.DeadLettered, attempt);
            }

            var visibleAt = Clock.UtcNow + (request.Delay ?? TimeSpan.Zero);
            await using var requeue = CreateCommand(connection, transaction, """
                UPDATE groundwork_work_queue
                SET lease_token = NULL, lease_expires_utc = NULL, next_visible_utc = @nextVisible
                WHERE unit = @unit AND message_id = @messageId;
                """);
            AddParameter(requeue, "nextVisible", Format(visibleAt));
            AddParameter(requeue, "unit", request.Unit);
            AddParameter(requeue, "messageId", request.MessageId);
            await requeue.ExecuteNonQueryAsync(ct);
            return new AbandonResult(AbandonStatus.Requeued, attempt);
        }, cancellationToken);

    public Task<DequeueResult> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            var replay = await TryReadIdempotentAsync(connection, transaction, request, ct);
            if (replay is not null)
                return replay;

            string messageId;
            long sequence;
            string payload;
            int attempt;
            await using (var select = CreateCommand(connection, transaction, """
                SELECT message_id, sequence, payload, attempt
                FROM groundwork_work_queue
                WHERE unit = @unit AND partition_key = @partitionKey
                  AND dead_lettered = 0 AND next_visible_utc <= @now
                ORDER BY sequence
                LIMIT 1;
                """))
            {
                AddParameter(select, "unit", request.Unit);
                AddParameter(select, "partitionKey", request.PartitionKey);
                AddParameter(select, "now", Format(Clock.UtcNow));
                await using var reader = await select.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    await RecordIdempotentAsync(connection, transaction, request, "empty", null, ct);
                    return DequeueResult.Empty;
                }

                messageId = reader.GetString(0);
                sequence = reader.GetInt64(1);
                payload = reader.GetString(2);
                attempt = reader.GetInt32(3);
            }

            await using (var delete = CreateCommand(connection, transaction, """
                DELETE FROM groundwork_work_queue WHERE unit = @unit AND message_id = @messageId;
                """))
            {
                AddParameter(delete, "unit", request.Unit);
                AddParameter(delete, "messageId", messageId);
                await delete.ExecuteNonQueryAsync(ct);
            }

            var message = new ClaimedMessage(
                messageId,
                request.PartitionKey,
                sequence,
                payload,
                attempt + 1,
                LeaseToken: string.Empty,
                LeaseExpiresAt: Clock.UtcNow);

            await RecordIdempotentAsync(connection, transaction, request, "dequeued", message, ct);
            return new DequeueResult(DequeueStatus.Dequeued, message);
        }, cancellationToken);

    private static async Task<bool> MessageExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT 1 FROM groundwork_work_queue WHERE unit = @unit AND message_id = @messageId;
            """);
        AddParameter(command, "unit", unit);
        AddParameter(command, "messageId", messageId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<DequeueResult?> TryReadIdempotentAsync(
        DbConnection connection,
        DbTransaction transaction,
        DequeueRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT status, message_id, partition_key, sequence, payload, attempt
            FROM groundwork_dequeue_idempotency
            WHERE unit = @unit AND idempotency_key = @key;
            """);
        AddParameter(command, "unit", request.Unit);
        AddParameter(command, "key", request.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        if (reader.GetString(0) == "empty")
            return new DequeueResult(DequeueStatus.Replayed);

        var message = new ClaimedMessage(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt32(5),
            LeaseToken: string.Empty,
            LeaseExpiresAt: default);
        return new DequeueResult(DequeueStatus.Replayed, message);
    }

    private static async Task RecordIdempotentAsync(
        DbConnection connection,
        DbTransaction transaction,
        DequeueRequest request,
        string status,
        ClaimedMessage? message,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO groundwork_dequeue_idempotency
                (unit, idempotency_key, status, message_id, partition_key, sequence, payload, attempt)
            VALUES (@unit, @key, @status, @messageId, @partitionKey, @sequence, @payload, @attempt);
            """);
        AddParameter(command, "unit", request.Unit);
        AddParameter(command, "key", request.IdempotencyKey);
        AddParameter(command, "status", status);
        AddParameter(command, "messageId", message?.MessageId);
        AddParameter(command, "partitionKey", message?.PartitionKey);
        AddParameter(command, "sequence", message?.Sequence);
        AddParameter(command, "payload", message?.Payload);
        AddParameter(command, "attempt", message?.Attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
