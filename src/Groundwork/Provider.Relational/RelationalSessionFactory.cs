using System.Data;
using System.Data.Common;

namespace Groundwork.Provider.Relational;

/// <summary>
/// Creates and owns a relational connection for each independent operation. Provider connection
/// pooling remains the provider's responsibility.
/// </summary>
public sealed class RelationalSessionFactory
{
    private readonly Func<DbConnection> createConnection;
    private readonly SemaphoreSlim? serializationGate;

    private RelationalSessionFactory(Func<DbConnection> createConnection, bool serialize)
    {
        ArgumentNullException.ThrowIfNull(createConnection);
        this.createConnection = createConnection;
        serializationGate = serialize ? new SemaphoreSlim(1, 1) : null;
    }

    /// <summary>Creates sessions without a process-wide serialization boundary.</summary>
    public static RelationalSessionFactory Concurrent(Func<DbConnection> createConnection) => new(createConnection, false);

    /// <summary>Creates sessions one at a time for providers that require serialized access.</summary>
    public static RelationalSessionFactory Serialized(Func<DbConnection> createConnection) => new(createConnection, true);

    /// <summary>An executor whose calls own and commit independent sessions.</summary>
    public RelationalExecutor AutonomousExecutor => new SessionRelationalExecutor(this);

    /// <summary>Runs one independent operation against a connection owned by that operation.</summary>
    public async Task<T> ExecuteAsync<T>(
        Func<DbConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var session = await OpenAsync(cancellationToken);
        return await operation(session.Connection, cancellationToken);
    }

    /// <summary>
    /// Begins an explicit unit of work that owns one connection and transaction until completion.
    /// </summary>
    public async Task<RelationalUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default)
    {
        var session = await OpenAsync(cancellationToken);
        try
        {
            var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
            return new RelationalUnitOfWork(session.Connection, transaction, session);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task<T> ExecuteInTransactionAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await BeginUnitOfWorkAsync(cancellationToken);
        var result = await unitOfWork.Executor.ExecuteAsync(operation, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<OwnedRelationalSession> OpenAsync(CancellationToken cancellationToken)
    {
        if (serializationGate is not null)
            await serializationGate.WaitAsync(cancellationToken);

        DbConnection? connection = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection = createConnection()
                ?? throw new InvalidOperationException("The relational connection factory returned null.");
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            return new OwnedRelationalSession(connection, serializationGate);
        }
        catch (Exception primaryFailure)
        {
            try
            {
                if (connection is not null)
                    await connection.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
            }
            finally
            {
                try
                {
                    serializationGate?.Release();
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
                }
            }
            throw;
        }
    }

    private sealed class OwnedRelationalSession(DbConnection connection, SemaphoreSlim? serializationGate) : IAsyncDisposable
    {
        private int disposed;

        public DbConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            Exception? primaryFailure = null;
            try
            {
                await Connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                try
                {
                    serializationGate?.Release();
                }
                catch (Exception cleanupFailure) when (primaryFailure is not null)
                {
                    RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
                }
            }

            if (primaryFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private sealed class SessionRelationalExecutor(RelationalSessionFactory sessions) : RelationalExecutor
    {
        public override Task<T> ExecuteAsync<T>(
            Func<DbConnection, DbTransaction, CancellationToken, Task<T>> work,
            CancellationToken cancellationToken) =>
            sessions.ExecuteInTransactionAsync(work, cancellationToken);
    }
}
