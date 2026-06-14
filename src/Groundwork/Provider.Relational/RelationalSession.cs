using System.Data;
using System.Data.Common;

namespace Groundwork.Provider.Relational;

/// <summary>
/// Owns a relational connection and a <see cref="SemaphoreSlim"/> gate, and hands out executors. A
/// module builds its autonomous stores on <see cref="AutonomousExecutor"/> and its atomic cross-unit
/// commits on <see cref="BeginUnitOfWorkAsync"/>. Connection gating mirrors the document store, so a
/// single shared connection is used safely.
/// </summary>
public sealed class RelationalSession(DbConnection connection)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public DbConnection Connection => connection;

    /// <summary>An executor that commits each operation independently.</summary>
    public RelationalExecutor AutonomousExecutor => new GatedRelationalExecutor(connection, gate);

    /// <summary>
    /// Begins a cross-unit unit of work. All operations enlisted through the returned
    /// <see cref="RelationalUnitOfWork.Executor"/> become durable only on
    /// <see cref="RelationalUnitOfWork.CommitAsync"/>.
    /// </summary>
    public async Task<RelationalUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new RelationalUnitOfWork(connection, transaction, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }
}

/// <summary>
/// A relational cross-unit transaction. All operations enlisted via <see cref="Executor"/> run inside
/// one <see cref="DbTransaction"/> and become durable only on <see cref="CommitAsync"/>. Disposing
/// without committing rolls back. Releases the owning session's gate on completion.
/// </summary>
public sealed class RelationalUnitOfWork : IAsyncDisposable
{
    private readonly DbTransaction transaction;
    private readonly SemaphoreSlim gate;
    private bool completed;

    internal RelationalUnitOfWork(DbConnection connection, DbTransaction transaction, SemaphoreSlim gate)
    {
        this.transaction = transaction;
        this.gate = gate;
        Executor = new EnlistedRelationalExecutor(connection, transaction);
    }

    /// <summary>Executor that enlists operations in this unit of work's transaction.</summary>
    public EnlistedRelationalExecutor Executor { get; }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (completed)
            return;

        completed = true;
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
            gate.Release();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (completed)
            return;

        completed = true;
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!completed)
            await RollbackAsync();
    }
}
