using Groundwork.Provider.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Modules.Inbox.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IInboxStore"/>, built entirely on the reusable
/// <see cref="RelationalSession"/> / <see cref="RelationalCommands"/> toolkit — no Groundwork core
/// changes, no copied connection/transaction plumbing. This is the proof that a third party can ship
/// a new persistence primitive as a Groundwork module.
/// </summary>
public sealed class SqliteInboxStore(SqliteConnection connection) : IInboxStore
{
    private readonly RelationalSession session = new(connection);

    public Task<InboxAdmission> TryAdmitAsync(string consumer, string messageKey, CancellationToken cancellationToken = default) =>
        session.AutonomousExecutor.ExecuteAsync(async (conn, tx, ct) =>
        {
            await using var insert = RelationalCommands.CreateCommand(conn, tx, $"""
                INSERT INTO {InboxSchema.Table} (consumer, message_key, state, admitted_utc)
                VALUES (@consumer, @key, 'admitted', @now)
                ON CONFLICT (consumer, message_key) DO NOTHING;
                """);
            RelationalCommands.AddParameter(insert, "consumer", consumer);
            RelationalCommands.AddParameter(insert, "key", messageKey);
            RelationalCommands.AddParameter(insert, "now", RelationalCommands.FormatTimestamp(DateTimeOffset.UtcNow));

            var inserted = await insert.ExecuteNonQueryAsync(ct);
            return inserted == 1 ? InboxAdmission.Admitted : InboxAdmission.Duplicate;
        }, cancellationToken);

    public Task MarkProcessedAsync(string consumer, string messageKey, CancellationToken cancellationToken = default) =>
        session.AutonomousExecutor.ExecuteAsync(async (conn, tx, ct) =>
        {
            await using var update = RelationalCommands.CreateCommand(conn, tx, $"""
                UPDATE {InboxSchema.Table}
                SET state = 'processed', processed_utc = @now
                WHERE consumer = @consumer AND message_key = @key;
                """);
            RelationalCommands.AddParameter(update, "consumer", consumer);
            RelationalCommands.AddParameter(update, "key", messageKey);
            RelationalCommands.AddParameter(update, "now", RelationalCommands.FormatTimestamp(DateTimeOffset.UtcNow));
            await update.ExecuteNonQueryAsync(ct);
            return true;
        }, cancellationToken);

    public Task<bool> IsProcessedAsync(string consumer, string messageKey, CancellationToken cancellationToken = default) =>
        session.AutonomousExecutor.ExecuteAsync(async (conn, tx, ct) =>
        {
            await using var read = RelationalCommands.CreateCommand(conn, tx, $"""
                SELECT state FROM {InboxSchema.Table}
                WHERE consumer = @consumer AND message_key = @key;
                """);
            RelationalCommands.AddParameter(read, "consumer", consumer);
            RelationalCommands.AddParameter(read, "key", messageKey);
            var state = await read.ExecuteScalarAsync(ct) as string;
            return string.Equals(state, "processed", StringComparison.Ordinal);
        }, cancellationToken);
}
