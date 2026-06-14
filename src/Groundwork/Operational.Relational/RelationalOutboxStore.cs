using Groundwork.Provider.Relational;
using Groundwork.Operational.Outbox;

namespace Groundwork.Operational.Relational;

internal sealed class RelationalOutboxStore(RelationalExecutor executor, IOperationalClock clock)
    : RelationalOperationalStoreBase(executor, clock), IOutboxStore
{
    public Task AppendAsync(OutboxAppendRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync<object?>(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var sequence = await NextSequenceAsync(connection, transaction, request.Unit, request.StreamKey, ct);

            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO groundwork_outbox
                    (unit, message_id, stream_key, sequence, payload, attempt, max_attempts,
                     next_visible_utc, lease_token, lease_expires_utc, dead_lettered, appended_utc)
                VALUES
                    (@unit, @messageId, @streamKey, @sequence, @payload, 0, @maxAttempts,
                     @nextVisible, NULL, NULL, 0, @appended);
                """);
            AddParameter(command, "unit", request.Unit);
            AddParameter(command, "messageId", NewMessageId());
            AddParameter(command, "streamKey", request.StreamKey);
            AddParameter(command, "sequence", sequence);
            AddParameter(command, "payload", request.Payload);
            AddParameter(command, "maxAttempts", request.MaxAttempts);
            AddParameter(command, "nextVisible", Format(now));
            AddParameter(command, "appended", Format(now));
            await command.ExecuteNonQueryAsync(ct);
            return null;
        }, cancellationToken);

    public Task<IReadOnlyList<DeliverableMessage>> GetDeliverableAsync(GetDeliverableRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var nowText = Format(now);
            var leaseExpiry = now + request.LeaseDuration;

            var candidates = new List<(string MessageId, string StreamKey, long Sequence, string Payload, int Attempt)>();
            await using (var select = CreateCommand(connection, transaction, $"""
                SELECT message_id, stream_key, sequence, payload, attempt
                FROM groundwork_outbox
                WHERE unit = @unit
                  AND dead_lettered = 0
                  AND next_visible_utc <= @now
                  {(request.StreamKey is null ? "" : "AND stream_key = @streamKey")}
                ORDER BY sequence
                LIMIT @batch;
                """))
            {
                AddParameter(select, "unit", request.Unit);
                AddParameter(select, "now", nowText);
                AddParameter(select, "batch", request.BatchSize);
                if (request.StreamKey is not null)
                    AddParameter(select, "streamKey", request.StreamKey);

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

            var deliverable = new List<DeliverableMessage>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var leaseToken = NewLeaseToken();
                await using var update = CreateCommand(connection, transaction, """
                    UPDATE groundwork_outbox
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
                    deliverable.Add(new DeliverableMessage(
                        candidate.MessageId,
                        candidate.StreamKey,
                        candidate.Sequence,
                        candidate.Payload,
                        candidate.Attempt + 1,
                        leaseToken,
                        leaseExpiry));
                }
            }

            return (IReadOnlyList<DeliverableMessage>)deliverable;
        }, cancellationToken);

    public Task RecordDeliveryResultAsync(DeliveryResultRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync<object?>(async (connection, transaction, ct) =>
        {
            if (request.Outcome == DeliveryOutcome.Delivered)
            {
                await using var delete = CreateCommand(connection, transaction, """
                    DELETE FROM groundwork_outbox
                    WHERE unit = @unit AND message_id = @messageId AND lease_token = @leaseToken;
                    """);
                AddParameter(delete, "unit", request.Unit);
                AddParameter(delete, "messageId", request.MessageId);
                AddParameter(delete, "leaseToken", request.LeaseToken);
                await delete.ExecuteNonQueryAsync(ct);
                return null;
            }

            int attempt;
            int maxAttempts;
            await using (var select = CreateCommand(connection, transaction, """
                SELECT attempt, max_attempts FROM groundwork_outbox
                WHERE unit = @unit AND message_id = @messageId AND lease_token = @leaseToken;
                """))
            {
                AddParameter(select, "unit", request.Unit);
                AddParameter(select, "messageId", request.MessageId);
                AddParameter(select, "leaseToken", request.LeaseToken);
                await using var reader = await select.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return null;

                attempt = reader.GetInt32(0);
                maxAttempts = reader.GetInt32(1);
            }

            var deadLetter = request.Outcome == DeliveryOutcome.DeadLetter || attempt >= maxAttempts;
            if (deadLetter)
            {
                await using var update = CreateCommand(connection, transaction, """
                    UPDATE groundwork_outbox
                    SET dead_lettered = 1, lease_token = NULL, lease_expires_utc = NULL
                    WHERE unit = @unit AND message_id = @messageId;
                    """);
                AddParameter(update, "unit", request.Unit);
                AddParameter(update, "messageId", request.MessageId);
                await update.ExecuteNonQueryAsync(ct);
                return null;
            }

            var visibleAt = Clock.UtcNow + (request.RetryDelay ?? TimeSpan.Zero);
            await using var retry = CreateCommand(connection, transaction, """
                UPDATE groundwork_outbox
                SET lease_token = NULL, lease_expires_utc = NULL, next_visible_utc = @nextVisible
                WHERE unit = @unit AND message_id = @messageId;
                """);
            AddParameter(retry, "nextVisible", Format(visibleAt));
            AddParameter(retry, "unit", request.Unit);
            AddParameter(retry, "messageId", request.MessageId);
            await retry.ExecuteNonQueryAsync(ct);
            return null;
        }, cancellationToken);
}
