using System.Data;
using System.Data.Common;

namespace Groundwork.Provider.Relational;

/// <summary>
/// Runs a unit of work against a relational connection. Implementations decide whether each call
/// commits independently (autonomous) or enlists in an ambient transaction (unit of work). This is
/// the reusable plumbing custom Groundwork modules build relational stores on, so they do not copy
/// connection-gating and transaction handling.
/// </summary>
public abstract class RelationalExecutor
{
    public abstract Task<T> ExecuteAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// Autonomous executor: gates the shared connection with a <see cref="SemaphoreSlim"/>, opens its own
/// transaction per call, and commits on success.
/// </summary>
public sealed class GatedRelationalExecutor(DbConnection connection, SemaphoreSlim gate) : RelationalExecutor
{
    public override async Task<T> ExecuteAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await work(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Enlisted executor: runs every call against an ambient transaction owned by a
/// <see cref="RelationalUnitOfWork"/>. Never commits — the unit of work commits once for all enlisted
/// operations, providing cross-unit atomic commit.
/// </summary>
public sealed class EnlistedRelationalExecutor(DbConnection connection, DbTransaction transaction) : RelationalExecutor
{
    public override Task<T> ExecuteAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        work(connection, transaction, cancellationToken);
}
