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
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();
    private readonly SemaphoreSlim databaseLifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> ownedDatabases = new(StringComparer.Ordinal);

    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public async Task DisposeAsync()
    {
        var cleanupFailures = new List<Exception>();
        await databaseLifecycleGate.WaitAsync();
        try
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
            if (ownedDatabases.ContainsKey(name))
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
        CancellationToken cancellationToken) =>
        await SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
            name,
            operation,
            DatabaseOperationTimeout,
            async operationToken =>
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync(operationToken);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = checked((int)DatabaseOperationTimeout.TotalSeconds);
                command.CommandText = commandText;
                await command.ExecuteNonQueryAsync(operationToken);
            },
            diagnosticToken => ReadDatabaseDiagnosticsAsync(name, diagnosticToken),
            cancellationToken);

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
    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(5);

    public static async Task ExecuteAsync(
        string databaseName,
        string operation,
        TimeSpan timeout,
        Func<CancellationToken, Task> execute,
        Func<CancellationToken, Task<string>> readDiagnostics,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await execute(timeoutSource.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string diagnostics;
            using var diagnosticSource = new CancellationTokenSource(DiagnosticTimeout);
            try
            {
                diagnostics = await readDiagnostics(diagnosticSource.Token);
            }
            catch (Exception diagnosticFailure)
            {
                diagnostics = $"diagnostics unavailable ({diagnosticFailure.GetType().Name}: {diagnosticFailure.Message})";
            }

            throw new InvalidOperationException(
                $"SQL diagnostic database '{databaseName}' {operation} failed within {timeout}. Server diagnostics: {diagnostics}.",
                exception);
        }
    }
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
