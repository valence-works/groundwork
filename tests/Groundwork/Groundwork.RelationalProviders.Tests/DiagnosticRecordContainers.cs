using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[CollectionDefinition(SqlServerDiagnosticRecordCollection.Name, DisableParallelization = true)]
public sealed class SqlServerDiagnosticRecordCollection : ICollectionFixture<SqlServerDiagnosticContainer>
{
    public const string Name = "SQL Server diagnostic records";
}

public sealed class SqlServerDiagnosticContainer : IAsyncLifetime
{
    private static readonly TimeSpan DatabaseOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DatabaseAbortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DatabaseDiagnosticTimeout = TimeSpan.FromSeconds(5);
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();
    private readonly SemaphoreSlim databaseLifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> ownedDatabases = new(StringComparer.Ordinal);
    private int poisoned;

    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public async Task DisposeAsync()
    {
        var cleanupFailures = new List<Exception>();
        await databaseLifecycleGate.WaitAsync();
        try
        {
            if (!IsPoisoned)
            {
                foreach (var name in ownedDatabases.Keys)
                {
                    try
                    {
                        await DropDatabaseCoreAsync(name, CancellationToken.None);
                        ownedDatabases.TryRemove(name, out _);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
            }
        }
        finally
        {
            databaseLifecycleGate.Release();
            databaseLifecycleGate.Dispose();
        }

        try
        {
            await container.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        if (cleanupFailures.Count > 0)
            throw new AggregateException("SQL diagnostic container cleanup failed.", cleanupFailures);
    }

    public async Task<string> CreateDatabaseAsync(
        bool enableReadCommittedSnapshot = true,
        CancellationToken cancellationToken = default)
    {
        var name = $"groundwork_diagnostics_{Guid.NewGuid():N}";
        await databaseLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfPoisoned();
            if (!ownedDatabases.TryAdd(name, 0))
                throw new InvalidOperationException($"SQL diagnostic database '{name}' is already owned by this fixture.");
            await ExecuteDatabaseOperationAsync(
                name,
                "creation",
                $"CREATE DATABASE [{name}];",
                cancellationToken);
            if (enableReadCommittedSnapshot)
            {
                await ExecuteDatabaseOperationAsync(
                    name,
                    "READ_COMMITTED_SNAPSHOT configuration",
                    $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON;",
                    cancellationToken);
            }

            return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name }.ConnectionString;
        }
        catch (Exception primaryFailure)
        {
            if (!IsPoisoned && ownedDatabases.ContainsKey(name))
            {
                try
                {
                    await DropDatabaseCoreAsync(name, CancellationToken.None);
                    ownedDatabases.TryRemove(name, out _);
                }
                catch (Exception cleanupFailure)
                {
                    primaryFailure.Data["Groundwork.Tests.SqlServerDiagnosticDatabaseCleanupFailure"] = cleanupFailure;
                }
            }
            throw;
        }
        finally
        {
            databaseLifecycleGate.Release();
        }
    }

    public async Task DropDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("groundwork_diagnostics_", StringComparison.Ordinal))
            throw new InvalidOperationException("The connection string does not identify a Groundwork diagnostic test database.");

        await databaseLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfPoisoned();
            if (!ownedDatabases.ContainsKey(name))
                throw new InvalidOperationException($"SQL diagnostic database '{name}' is not owned by this fixture.");
            using var pooledConnection = new SqlConnection(connectionString);
            SqlConnection.ClearPool(pooledConnection);
            await DropDatabaseCoreAsync(name, cancellationToken);
            ownedDatabases.TryRemove(name, out _);
        }
        finally
        {
            databaseLifecycleGate.Release();
        }
    }

    private async Task DropDatabaseCoreAsync(string name, CancellationToken cancellationToken) =>
        await ExecuteDatabaseOperationAsync(
            name,
            "drop",
            $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;",
            cancellationToken);

    private async Task ExecuteDatabaseOperationAsync(
        string name,
        string operation,
        string commandText,
        CancellationToken cancellationToken)
    {
        SqlConnection? activeConnection = null;
        SqlCommand? activeCommand = null;
        try
        {
            await SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                name,
                operation,
                DatabaseOperationTimeout,
                DatabaseAbortTimeout,
                DatabaseDiagnosticTimeout,
                async operationToken =>
                {
                    await using var connection = new SqlConnection(ConnectionString);
                    activeConnection = connection;
                    await connection.OpenAsync(operationToken);
                    await using var command = connection.CreateCommand();
                    activeCommand = command;
                    command.CommandTimeout = checked((int)DatabaseOperationTimeout.TotalSeconds);
                    command.CommandText = commandText;
                    await command.ExecuteNonQueryAsync(operationToken);
                },
                () =>
                {
                    try
                    {
                        activeCommand?.Cancel();
                    }
                    finally
                    {
                        activeConnection?.Close();
                    }

                    return ValueTask.CompletedTask;
                },
                diagnosticToken => ReadDatabaseDiagnosticsAsync(name, diagnosticToken),
                cancellationToken);
        }
        catch (SqlServerDiagnosticDatabaseOperationException exception) when (!exception.OperationQuiesced)
        {
            Interlocked.Exchange(ref poisoned, 1);
            throw;
        }
    }

    private bool IsPoisoned => Volatile.Read(ref poisoned) != 0;

    private void ThrowIfPoisoned()
    {
        if (IsPoisoned)
            throw new InvalidOperationException("The SQL diagnostic container is poisoned by an operation that did not quiesce and must be disposed.");
    }

    private async Task<string> ReadDatabaseDiagnosticsAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT d.state_desc, d.user_access_desc, d.is_read_committed_snapshot_on,
                   r.session_id, r.status, r.command, r.wait_type, r.wait_time, r.blocking_session_id
            FROM sys.databases AS d
            LEFT JOIN sys.dm_exec_requests AS r ON r.database_id = d.database_id
            WHERE d.name = @name
            ORDER BY r.session_id;
            """;
        command.Parameters.AddWithValue("@name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"state={reader.GetString(0)},access={reader.GetString(1)},rcsi={reader.GetBoolean(2)}," +
                $"session={(reader.IsDBNull(3) ? "none" : reader.GetInt32(3))}," +
                $"status={(reader.IsDBNull(4) ? "none" : reader.GetString(4))}," +
                $"command={(reader.IsDBNull(5) ? "none" : reader.GetString(5))}," +
                $"wait={(reader.IsDBNull(6) ? "none" : reader.GetString(6))}," +
                $"wait_ms={(reader.IsDBNull(7) ? 0 : reader.GetInt32(7))}," +
                $"blocker={(reader.IsDBNull(8) ? 0 : reader.GetInt32(8))}"));
        }

        return rows.Count == 0 ? "database not present" : string.Join("; ", rows);
    }
}

internal static class SqlServerDiagnosticDatabaseOperation
{
    public static async Task ExecuteAsync(
        string databaseName,
        string operation,
        TimeSpan operationTimeout,
        TimeSpan abortTimeout,
        TimeSpan diagnosticTimeout,
        Func<CancellationToken, Task> execute,
        Func<ValueTask> abort,
        Func<CancellationToken, Task<string>> readDiagnostics,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(operationTimeout);
        var executionTask = Task.Run(() => execute(timeoutSource.Token), CancellationToken.None);

        try
        {
            await executionTask.WaitAsync(timeoutSource.Token);
        }
        catch (Exception exception)
        {
            var quiescence = await QuiesceAsync(executionTask, abort, abortTimeout);
            if (cancellationToken.IsCancellationRequested && quiescence.OperationQuiesced)
            {
                AttachAbortFailure(exception, quiescence.AbortFailure);
                AttachCompletionFailure(exception, quiescence.CompletionFailure);
                throw;
            }

            var diagnostics = cancellationToken.IsCancellationRequested
                ? "diagnostics not collected after caller cancellation because the operation did not quiesce"
                : await ReadDiagnosticsAsync(readDiagnostics, diagnosticTimeout);
            var failure = new SqlServerDiagnosticDatabaseOperationException(
                $"SQL diagnostic database '{databaseName}' {operation} failed " +
                $"(operation deadline {operationTimeout}; quiesced={quiescence.OperationQuiesced}). " +
                $"Server diagnostics: {diagnostics}.",
                quiescence.CompletionFailure ?? exception,
                quiescence.OperationQuiesced);
            AttachAbortFailure(failure, quiescence.AbortFailure);
            throw failure;
        }
    }

    private static async Task<QuiescenceResult> QuiesceAsync(
        Task executionTask,
        Func<ValueTask> abort,
        TimeSpan abortTimeout)
    {
        if (executionTask.IsCompleted)
        {
            try
            {
                await executionTask;
                return new(true, null, null);
            }
            catch (Exception completionFailure)
            {
                return new(true, null, completionFailure);
            }
        }

        using var abortDeadline = new CancellationTokenSource(abortTimeout);
        Exception? abortFailure = null;
        Task? abortTask = null;
        try
        {
            abortTask = Task.Run(async () => await abort(), CancellationToken.None);
            await abortTask.WaitAsync(abortDeadline.Token);
        }
        catch (Exception exception)
        {
            abortFailure = abortDeadline.IsCancellationRequested
                ? new TimeoutException($"SQL diagnostic database abort exceeded {abortTimeout}.", exception)
                : exception;
            if (abortTask is { IsCompleted: false })
                ObserveEventually(abortTask);
        }

        try
        {
            await executionTask.WaitAsync(abortDeadline.Token);
            return new(true, abortFailure, null);
        }
        catch (OperationCanceledException) when (abortDeadline.IsCancellationRequested)
        {
            ObserveEventually(executionTask);
            return new(
                false,
                abortFailure ?? new TimeoutException($"SQL diagnostic database operation did not quiesce within {abortTimeout}."),
                null);
        }
        catch (Exception completionFailure)
        {
            return new(true, abortFailure, completionFailure);
        }
    }

    private static async Task<string> ReadDiagnosticsAsync(
        Func<CancellationToken, Task<string>> readDiagnostics,
        TimeSpan diagnosticTimeout)
    {
        using var diagnosticDeadline = new CancellationTokenSource(diagnosticTimeout);
        var diagnosticTask = Task.Run(
            () => readDiagnostics(diagnosticDeadline.Token),
            CancellationToken.None);

        try
        {
            return await diagnosticTask.WaitAsync(diagnosticDeadline.Token);
        }
        catch (OperationCanceledException) when (diagnosticDeadline.IsCancellationRequested)
        {
            if (!diagnosticTask.IsCompleted)
                ObserveEventually(diagnosticTask);
            return $"diagnostics unavailable (deadline {diagnosticTimeout} exceeded)";
        }
        catch (Exception exception)
        {
            return UnavailableDiagnostics(exception);
        }
    }

    private static string UnavailableDiagnostics(Exception exception) =>
        $"diagnostics unavailable ({exception.GetType().Name}: {exception.Message})";

    private static void AttachAbortFailure(Exception target, Exception? abortFailure)
    {
        if (abortFailure is not null)
            target.Data["Groundwork.Tests.SqlServerDiagnosticDatabaseAbortFailure"] = abortFailure;
    }

    private static void AttachCompletionFailure(Exception target, Exception? completionFailure)
    {
        if (completionFailure is not null && !ReferenceEquals(target, completionFailure))
            target.Data["Groundwork.Tests.SqlServerDiagnosticDatabaseCompletionFailure"] = completionFailure;
    }

    private static void ObserveEventually(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed record QuiescenceResult(
        bool OperationQuiesced,
        Exception? AbortFailure,
        Exception? CompletionFailure);
}

internal sealed class SqlServerDiagnosticDatabaseOperationException(
    string message,
    Exception innerException,
    bool operationQuiesced) : InvalidOperationException(message, innerException)
{
    public bool OperationQuiesced { get; } = operationQuiesced;
}

[CollectionDefinition(PostgreSqlDiagnosticRecordCollection.Name, DisableParallelization = true)]
public sealed class PostgreSqlDiagnosticRecordCollection : ICollectionFixture<PostgreSqlDiagnosticContainer>
{
    public const string Name = "PostgreSQL diagnostic records";
}

public sealed class PostgreSqlDiagnosticContainer : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<string> CreateSchemaAsync()
    {
        var name = $"diagnostics_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {name};";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = name }.ConnectionString;
    }

    public async Task DropSchemaAsync(string connectionString)
    {
        NpgsqlConnection.ClearAllPools();
        var name = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {name} CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
