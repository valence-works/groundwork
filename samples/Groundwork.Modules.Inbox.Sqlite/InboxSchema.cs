namespace Groundwork.Modules.Inbox.Sqlite;

/// <summary>Schema for the inbox dedup table.</summary>
public static class InboxSchema
{
    public const string Table = "community_inbox";

    public const string CreateTable = $"""
        CREATE TABLE IF NOT EXISTS {Table} (
            consumer     TEXT NOT NULL,
            message_key  TEXT NOT NULL,
            state        TEXT NOT NULL,
            admitted_utc TEXT NOT NULL,
            processed_utc TEXT NULL,
            PRIMARY KEY (consumer, message_key)
        );
        """;
}
