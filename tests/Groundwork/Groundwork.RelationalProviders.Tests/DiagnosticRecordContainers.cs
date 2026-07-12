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
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();

    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync(bool enableReadCommittedSnapshot = true)
    {
        var name = $"groundwork_diagnostics_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{name}];" +
                              (enableReadCommittedSnapshot
                                  ? $" ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"
                                  : "");
        await command.ExecuteNonQueryAsync();
        return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    public async Task DropDatabaseAsync(string connectionString)
    {
        SqlConnection.ClearAllPools();
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;";
        await command.ExecuteNonQueryAsync();
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
