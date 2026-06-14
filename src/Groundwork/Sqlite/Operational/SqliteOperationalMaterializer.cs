using System.Data;
using Groundwork.Operational.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Operational;

/// <summary>
/// Creates the operational tables (work queue, outbox, leases, dequeue idempotency, sequence) for a
/// SQLite connection, using the shared <see cref="RelationalOperationalSchema"/> statements.
/// </summary>
public sealed class SqliteOperationalMaterializer(SqliteConnection connection)
{
    public async Task MaterializeAsync(CancellationToken cancellationToken = default)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in RelationalOperationalSchema.CreateTableStatements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
