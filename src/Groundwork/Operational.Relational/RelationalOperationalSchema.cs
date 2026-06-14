namespace Groundwork.Operational.Relational;

/// <summary>
/// Table-creation SQL for the relational operational stores. Provider materializers execute these to
/// create the work-queue, outbox, lease, idempotency, and sequence tables. The SQL targets the
/// common SQLite/relational subset; providers may override for dialect specifics.
/// </summary>
public static class RelationalOperationalSchema
{
    public const string WorkQueueTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_work_queue (
            unit TEXT NOT NULL,
            message_id TEXT NOT NULL,
            partition_key TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            payload TEXT NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            max_attempts INTEGER NOT NULL,
            next_visible_utc TEXT NOT NULL,
            lease_token TEXT NULL,
            lease_expires_utc TEXT NULL,
            dead_lettered INTEGER NOT NULL DEFAULT 0,
            enqueued_utc TEXT NOT NULL,
            PRIMARY KEY (unit, message_id)
        );
        """;

    public const string OutboxTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_outbox (
            unit TEXT NOT NULL,
            message_id TEXT NOT NULL,
            stream_key TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            payload TEXT NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 0,
            max_attempts INTEGER NOT NULL,
            next_visible_utc TEXT NOT NULL,
            lease_token TEXT NULL,
            lease_expires_utc TEXT NULL,
            dead_lettered INTEGER NOT NULL DEFAULT 0,
            appended_utc TEXT NOT NULL,
            PRIMARY KEY (unit, message_id)
        );
        """;

    public const string LeasesTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_leases (
            unit TEXT NOT NULL,
            resource_key TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            fencing_token INTEGER NOT NULL,
            expires_utc TEXT NOT NULL,
            PRIMARY KEY (unit, resource_key)
        );
        """;

    public const string DequeueIdempotencyTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_dequeue_idempotency (
            unit TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            status TEXT NOT NULL,
            message_id TEXT NULL,
            partition_key TEXT NULL,
            sequence INTEGER NULL,
            payload TEXT NULL,
            attempt INTEGER NULL,
            PRIMARY KEY (unit, idempotency_key)
        );
        """;

    public const string SequenceTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_operational_sequence (
            unit TEXT NOT NULL,
            scope_key TEXT NOT NULL,
            next_value INTEGER NOT NULL,
            PRIMARY KEY (unit, scope_key)
        );
        """;

    public static IReadOnlyList<string> CreateTableStatements { get; } =
    [
        WorkQueueTableSql,
        OutboxTableSql,
        LeasesTableSql,
        DequeueIdempotencyTableSql,
        SequenceTableSql
    ];
}
