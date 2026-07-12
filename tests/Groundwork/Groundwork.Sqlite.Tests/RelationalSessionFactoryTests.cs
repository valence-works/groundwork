using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class RelationalSessionFactoryTests
{
    [Fact]
    public async Task IndependentOperationOwnsOneConnection()
    {
        var connections = new List<SqliteConnection>();
        var sessions = RelationalSessionFactory.Concurrent(() =>
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connections.Add(connection);
            return connection;
        });

        var state = await sessions.ExecuteAsync(
            (connection, _) => Task.FromResult(connection.State));

        Assert.Equal(ConnectionState.Open, state);
        Assert.Single(connections);
        Assert.Equal(ConnectionState.Closed, connections[0].State);
    }

    [Fact]
    public async Task UnitOfWorkOwnsOneConnectionUntilCommit()
    {
        await using var database = await SessionDatabase.CreateAsync();
        await using var unitOfWork = await database.Sessions.BeginUnitOfWorkAsync();

        await InsertValueAsync(unitOfWork);

        Assert.Single(database.Connections);
        Assert.Equal(ConnectionState.Open, database.Connections[0].State);

        await unitOfWork.CommitAsync();

        Assert.Equal(ConnectionState.Closed, database.Connections[0].State);
        Assert.Equal(1, await database.CountValuesAsync());
    }

    [Fact]
    public async Task DisposingUnitOfWorkRollsBackAndReleasesItsConnection()
    {
        await using var database = await SessionDatabase.CreateAsync();
        await using (var unitOfWork = await database.Sessions.BeginUnitOfWorkAsync())
        {
            await InsertValueAsync(unitOfWork);
        }

        Assert.Single(database.Connections);
        Assert.Equal(ConnectionState.Closed, database.Connections[0].State);
        Assert.Equal(0, await database.CountValuesAsync());
    }

    [Fact]
    public async Task RollingBackUnitOfWorkReleasesItsConnectionWithoutPersisting()
    {
        await using var database = await SessionDatabase.CreateAsync();
        await using var unitOfWork = await database.Sessions.BeginUnitOfWorkAsync();

        await InsertValueAsync(unitOfWork);
        await unitOfWork.RollbackAsync();

        Assert.Single(database.Connections);
        Assert.Equal(ConnectionState.Closed, database.Connections[0].State);
        Assert.Equal(0, await database.CountValuesAsync());
    }

    [Fact]
    public async Task StatelessDocumentFacadeUsesIndependentOwnedConnections()
    {
        var databasePath = Path.GetTempFileName();
        var manifest = ClosedQueryManifests.WidgetManifest();
        var connections = new List<SqliteConnection>();

        try
        {
            await using (var materializationConnection = new SqliteConnection($"Data Source={databasePath}"))
                await new SqliteGroundworkMaterializer(materializationConnection).MaterializeAsync(manifest, ClosedQueryManifests.Provider);

            var sessions = RelationalSessionFactory.Serialized(() =>
            {
                var connection = new SqliteConnection($"Data Source={databasePath}");
                connections.Add(connection);
                return connection;
            });
            IDocumentStore store = new SqliteDocumentStore(sessions, manifest);

            var saved = await store.SaveAsync(new SaveDocumentRequest(
                "widget", "w1", "1.0.0", """{"name":"w1","category":"tools","sortKey":"001"}"""));
            var loaded = await store.LoadAsync("widget", "w1");

            Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
            Assert.NotNull(loaded);
            Assert.Equal(2, connections.Count);
            Assert.All(connections, connection => Assert.Equal(ConnectionState.Closed, connection.State));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CancelledOperationDoesNotAcquireConnection()
    {
        var connectionCreated = false;
        var sessions = RelationalSessionFactory.Concurrent(() =>
        {
            connectionCreated = true;
            return new SqliteConnection("Data Source=:memory:");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.ExecuteAsync(
            (_, _) => Task.FromResult(true),
            cancellation.Token));

        Assert.False(connectionCreated);
    }

    [Fact]
    public async Task FailedOperationDisposesItsConnection()
    {
        SqliteConnection? ownedConnection = null;
        var sessions = RelationalSessionFactory.Concurrent(() =>
            ownedConnection = new SqliteConnection("Data Source=:memory:"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.ExecuteAsync<bool>(
            (_, _) => throw new InvalidOperationException("operation failed")));

        Assert.NotNull(ownedConnection);
        Assert.Equal(ConnectionState.Closed, ownedConnection.State);
    }

    [Fact]
    public async Task FailedOperationPreservesPrimaryFailureWhenConnectionDisposalFails()
    {
        var connection = new CompletionTrackingConnection
        {
            ConnectionDisposeFailure = new InvalidOperationException("connection dispose failed")
        };
        var sessions = RelationalSessionFactory.Concurrent(() => connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.ExecuteAsync<bool>(
            (_, _) => throw new InvalidOperationException("operation failed")));

        Assert.Equal("operation failed", exception.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(cleanupFailures, cleanup => Assert.Equal("connection dispose failed", cleanup.Message));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task CancellationDuringOperationDisposesItsConnection()
    {
        SqliteConnection? ownedConnection = null;
        var sessions = RelationalSessionFactory.Concurrent(() =>
            ownedConnection = new SqliteConnection("Data Source=:memory:"));
        using var cancellation = new CancellationTokenSource();

        var operation = sessions.ExecuteAsync(async (_, cancellationToken) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.NotNull(ownedConnection);
        Assert.Equal(ConnectionState.Closed, ownedConnection.State);
    }

    [Fact]
    public async Task ConcurrentPolicyDoesNotSerializeIndependentOperations()
    {
        var sessions = RelationalSessionFactory.Concurrent(() => new SqliteConnection("Data Source=:memory:"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task<bool> HoldConnection(DbConnection _, CancellationToken __)
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.SetResult();
            await release.Task;
            return true;
        }

        var first = sessions.ExecuteAsync(HoldConnection);
        var second = sessions.ExecuteAsync(HoldConnection);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    [Fact]
    public async Task SerializedPolicyDefinesTheSqliteConcurrencyBoundary()
    {
        var sessions = RelationalSessionFactory.Serialized(() => new SqliteConnection("Data Source=:memory:"));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task<bool> HoldFirstConnection(DbConnection _, CancellationToken __)
        {
            if (Interlocked.Increment(ref entered) == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondEntered.SetResult();
            }
            return true;
        }

        var first = sessions.ExecuteAsync(HoldFirstConnection);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = sessions.ExecuteAsync(HoldFirstConnection);
        try
        {
            await Task.Delay(200);
            Assert.False(secondEntered.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);
        }

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellingSerializedWaitDoesNotCreateConnectionOrPoisonGate()
    {
        var connectionCount = 0;
        var sessions = RelationalSessionFactory.Serialized(() =>
        {
            Interlocked.Increment(ref connectionCount);
            return new SqliteConnection("Data Source=:memory:");
        });
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = sessions.ExecuteAsync(async (_, _) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return true;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var cancelled = sessions.ExecuteAsync((_, _) => Task.FromResult(true), cancellation.Token);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(1, Volatile.Read(ref connectionCount));
        }
        finally
        {
            releaseFirst.TrySetResult();
            await first;
        }

        Assert.True(await sessions.ExecuteAsync((_, _) => Task.FromResult(true)));
        Assert.Equal(2, Volatile.Read(ref connectionCount));
    }

    [Fact]
    public async Task FailedOpenPreservesPrimaryFailureAndReleasesSerializedGateWhenDisposalFails()
    {
        var connectionCount = 0;
        var sessions = RelationalSessionFactory.Serialized(() =>
            Interlocked.Increment(ref connectionCount) == 1
                ? new FailingOpenAndDisposeConnection()
                : new SqliteConnection("Data Source=:memory:"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.ExecuteAsync((_, _) => Task.FromResult(true)));
        Assert.Equal("open failed", exception.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(cleanupFailures, cleanup => Assert.Equal("dispose failed", cleanup.Message));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(await sessions.ExecuteAsync((_, _) => Task.FromResult(true), cancellation.Token));
    }

    [Fact]
    public async Task FailedCommitPreservesPrimaryFailureAndAttachesAllCleanupFailures()
    {
        var connection = new CompletionTrackingConnection
        {
            CommitFailure = new InvalidOperationException("commit failed"),
            TransactionDisposeFailure = new InvalidOperationException("transaction dispose failed"),
            ConnectionDisposeFailure = new InvalidOperationException("connection dispose failed")
        };
        var sessions = RelationalSessionFactory.Concurrent(() => connection);
        await using var unitOfWork = await sessions.BeginUnitOfWorkAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

        Assert.Equal("commit failed", exception.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(
            cleanupFailures,
            cleanup => Assert.Equal("transaction dispose failed", cleanup.Message),
            cleanup => Assert.Equal("connection dispose failed", cleanup.Message));
        Assert.Equal(1, connection.Transaction.CommitCount);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task FailedRollbackPreservesPrimaryFailureWhenCleanupFails()
    {
        var connection = new CompletionTrackingConnection
        {
            RollbackFailure = new InvalidOperationException("rollback failed"),
            TransactionDisposeFailure = new InvalidOperationException("transaction dispose failed")
        };
        var sessions = RelationalSessionFactory.Concurrent(() => connection);
        await using var unitOfWork = await sessions.BeginUnitOfWorkAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.RollbackAsync());

        Assert.Equal("rollback failed", exception.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(cleanupFailures, cleanup => Assert.Equal("transaction dispose failed", cleanup.Message));
        Assert.Equal(1, connection.Transaction.RollbackCount);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentTerminalCallsPerformOneActionAndReleaseSerializedSessionOnce()
    {
        var connection = new CompletionTrackingConnection();
        connection.Transaction.HoldCommit = true;
        var sessions = RelationalSessionFactory.Serialized(() => connection);
        var unitOfWork = await sessions.BeginUnitOfWorkAsync();

        var commit = unitOfWork.CommitAsync();
        await connection.Transaction.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rollback = unitOfWork.RollbackAsync();
        var firstDispose = unitOfWork.DisposeAsync().AsTask();
        var secondDispose = unitOfWork.DisposeAsync().AsTask();

        Assert.False(rollback.IsCompleted);
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);
        connection.Transaction.ReleaseCommit.TrySetResult();
        await Task.WhenAll(commit, rollback, firstDispose, secondDispose);

        Assert.Equal(1, connection.Transaction.CommitCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(await sessions.ExecuteAsync((_, _) => Task.FromResult(true), cancellation.Token));
    }

    [Fact]
    public async Task NullConnectionFromFactoryFailsClearlyWithoutPoisoningSerializedGate()
    {
        var connectionCount = 0;
        var sessions = RelationalSessionFactory.Serialized(() =>
            Interlocked.Increment(ref connectionCount) == 1
                ? null!
                : new SqliteConnection("Data Source=:memory:"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.ExecuteAsync((_, _) => Task.FromResult(true)));
        Assert.Contains("returned null", exception.Message);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(await sessions.ExecuteAsync((_, _) => Task.FromResult(true), cancellation.Token));
    }

    [Fact]
    public void StatelessSqliteStoreRejectsPrivateInMemoryDatabase()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SqliteDocumentStore("Data Source=:memory:", ClosedQueryManifests.WidgetManifest()));

        Assert.Contains("direct-connection constructor", exception.Message);
    }

    [Fact]
    public void SqlitePublicConstructorsOwnTheSessionPolicy()
    {
        Assert.DoesNotContain(
            typeof(SqliteDocumentStore).GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(RelationalSessionFactory)));
    }

    private static Task<bool> InsertValueAsync(RelationalUnitOfWork unitOfWork) =>
        unitOfWork.Executor.ExecuteAsync(async (connection, transaction, cancellationToken) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO values_table (value) VALUES (42);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, default);

    private sealed class SessionDatabase : IAsyncDisposable
    {
        private readonly string databasePath = Path.GetTempFileName();

        private SessionDatabase()
        {
            Sessions = RelationalSessionFactory.Concurrent(() =>
            {
                var connection = new SqliteConnection($"Data Source={databasePath}");
                Connections.Add(connection);
                return connection;
            });
        }

        public List<SqliteConnection> Connections { get; } = [];
        public RelationalSessionFactory Sessions { get; }

        public static async Task<SessionDatabase> CreateAsync()
        {
            var database = new SessionDatabase();
            await database.Sessions.ExecuteAsync(async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE values_table (value INTEGER NOT NULL);";
                await command.ExecuteNonQueryAsync(cancellationToken);
                return true;
            });
            database.Connections.Clear();
            return database;
        }

        public Task<int> CountValuesAsync() => Sessions.ExecuteAsync(async (connection, cancellationToken) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM values_table;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        });

        public ValueTask DisposeAsync()
        {
            File.Delete(databasePath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingOpenAndDisposeConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("open failed");
        public override Task OpenAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("open failed");
        public override ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failed"));

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class CompletionTrackingConnection : DbConnection
    {
        private ConnectionState state = ConnectionState.Closed;

        public CompletionTrackingConnection() => Transaction = new CompletionTrackingTransaction(this);

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => state;

        public Exception? CommitFailure { get => Transaction.CommitFailure; init => Transaction.CommitFailure = value; }
        public Exception? RollbackFailure { get => Transaction.RollbackFailure; init => Transaction.RollbackFailure = value; }
        public Exception? TransactionDisposeFailure { get => Transaction.DisposeFailure; init => Transaction.DisposeFailure = value; }
        public Exception? ConnectionDisposeFailure { get; init; }
        public CompletionTrackingTransaction Transaction { get; }
        public int DisposeCount { get; private set; }

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => state = ConnectionState.Closed;
        public override void Open() => state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            state = ConnectionState.Closed;
            return ConnectionDisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(ConnectionDisposeFailure);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Transaction;
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class CompletionTrackingTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection DbConnection => connection;

        public Exception? CommitFailure { get; set; }
        public Exception? RollbackFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool HoldCommit { get; set; }
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();

        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            CommitEntered.TrySetResult();
            if (HoldCommit)
                await ReleaseCommit.Task.WaitAsync(cancellationToken);
            if (CommitFailure is not null)
                throw CommitFailure;
        }

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return RollbackFailure is null ? Task.CompletedTask : Task.FromException(RollbackFailure);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }
}
