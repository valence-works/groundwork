using System.Data;
using Microsoft.Data.Sqlite;

namespace Groundwork.Modules.Inbox.Sqlite;

/// <summary>Creates the inbox table for a SQLite connection.</summary>
public sealed class SqliteInboxMaterializer(SqliteConnection connection)
{
    public async Task MaterializeAsync(CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = InboxSchema.CreateTable;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
