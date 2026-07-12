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
    private readonly SemaphoreSlim? gate;
    private readonly IAsyncDisposable? sessionOwner;
    private readonly object completionLock = new();
    private Task? completionTask;

    internal RelationalUnitOfWork(DbConnection connection, DbTransaction transaction, SemaphoreSlim gate)
    {
        this.transaction = transaction;
        this.gate = gate;
        Executor = new EnlistedRelationalExecutor(connection, transaction);
    }

    internal RelationalUnitOfWork(DbConnection connection, DbTransaction transaction, IAsyncDisposable sessionOwner)
    {
        this.transaction = transaction;
        this.sessionOwner = sessionOwner;
        Executor = new EnlistedRelationalExecutor(connection, transaction);
    }

    /// <summary>Executor that enlists operations in this unit of work's transaction.</summary>
    public EnlistedRelationalExecutor Executor { get; }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        CompleteOnceAsync(() => transaction.CommitAsync(cancellationToken), suppressExistingFailure: false);

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        CompleteOnceAsync(() => transaction.RollbackAsync(cancellationToken), suppressExistingFailure: false);

    public ValueTask DisposeAsync() => new(CompleteOnceAsync(() => transaction.RollbackAsync(), suppressExistingFailure: true));

    private Task CompleteOnceAsync(Func<Task> terminalAction, bool suppressExistingFailure)
    {
        lock (completionLock)
        {
            if (completionTask is null)
            {
                completionTask = CompleteCoreAsync(terminalAction);
                return completionTask;
            }

            return suppressExistingFailure ? AwaitExistingCompletionAsync(completionTask) : completionTask;
        }
    }

    private async Task CompleteCoreAsync(Func<Task> terminalAction)
    {
        Exception? primaryFailure = null;
        try
        {
            await terminalAction();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        primaryFailure = await CaptureCleanupFailureAsync(primaryFailure, () => transaction.DisposeAsync());
        primaryFailure = sessionOwner is not null
            ? await CaptureCleanupFailureAsync(primaryFailure, () => sessionOwner.DisposeAsync())
            : CaptureCleanupFailure(primaryFailure, () => gate!.Release());

        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private static async Task AwaitExistingCompletionAsync(Task existingCompletion)
    {
        try
        {
            await existingCompletion;
        }
        catch
        {
            // The caller that won the terminal transition observes the failure. Later completion
            // calls remain idempotent and only wait for cleanup to finish.
        }
    }

    private static async Task<Exception?> CaptureCleanupFailureAsync(
        Exception? primaryFailure,
        Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
                return cleanupFailure;

            RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
        }

        return primaryFailure;
    }

    private static Exception? CaptureCleanupFailure(Exception? primaryFailure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is null)
                return cleanupFailure;

            RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
        }

        return primaryFailure;
    }
}
