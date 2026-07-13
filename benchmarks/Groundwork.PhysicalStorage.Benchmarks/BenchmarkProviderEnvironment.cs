using Groundwork.Core.PhysicalStorage;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal interface IBenchmarkProviderEnvironment : IAsyncDisposable
{
    Task StartAsync(
        IReadOnlyList<BenchmarkProvider> providers,
        bool allowContainers,
        CancellationToken cancellationToken);

    IPhysicalStorageBenchmarkTarget CreateTarget(
        BenchmarkProvider provider,
        PhysicalStorageForm form,
        string instance,
        string scratchDirectory,
        int migrationDatasetSize);
}

public sealed class BenchmarkProviderEnvironment : IBenchmarkProviderEnvironment
{
    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04";
    public const string PostgreSqlImage = "postgres:17.6-alpine3.22";
    public const string MongoDbImage = "mongo:7.0.24";
    private MsSqlContainer? sqlServer;
    private PostgreSqlContainer? postgreSql;
    private MongoDbContainer? mongoDb;
    private string? sqlServerConnection;
    private string? postgreSqlConnection;
    private string? mongoDbConnection;
    private string sqlServerSource = string.Empty;
    private string postgreSqlSource = string.Empty;
    private string mongoDbSource = string.Empty;

    public async Task StartAsync(
        IReadOnlyList<BenchmarkProvider> providers,
        bool allowContainers,
        CancellationToken cancellationToken)
    {
        if (providers.Contains(BenchmarkProvider.SqlServer))
            (sqlServerConnection, sqlServerSource) = await StartSqlServerAsync(allowContainers, cancellationToken);
        if (providers.Contains(BenchmarkProvider.PostgreSql))
            (postgreSqlConnection, postgreSqlSource) = await StartPostgreSqlAsync(allowContainers, cancellationToken);
        if (providers.Contains(BenchmarkProvider.MongoDb))
            (mongoDbConnection, mongoDbSource) = await StartMongoDbAsync(allowContainers, cancellationToken);
    }

    public IPhysicalStorageBenchmarkTarget CreateTarget(
        BenchmarkProvider provider,
        PhysicalStorageForm form,
        string instance,
        string scratchDirectory,
        int migrationDatasetSize) => provider switch
        {
            BenchmarkProvider.Sqlite => new SqliteBenchmarkTarget(form, instance, scratchDirectory, migrationDatasetSize),
            BenchmarkProvider.SqlServer => new SqlServerBenchmarkTarget(
                form, instance, Required(sqlServerConnection, provider), migrationDatasetSize, sqlServerSource),
            BenchmarkProvider.PostgreSql => new PostgreSqlBenchmarkTarget(
                form, instance, Required(postgreSqlConnection, provider), migrationDatasetSize, postgreSqlSource),
            BenchmarkProvider.MongoDb => new MongoDbBenchmarkTarget(
                form, instance, Required(mongoDbConnection, provider), migrationDatasetSize, mongoDbSource),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    public async ValueTask DisposeAsync()
    {
        if (mongoDb is not null)
            await mongoDb.DisposeAsync();
        if (postgreSql is not null)
            await postgreSql.DisposeAsync();
        if (sqlServer is not null)
            await sqlServer.DisposeAsync();
    }

    private async Task<(string ConnectionString, string Source)> StartSqlServerAsync(
        bool allowContainers,
        CancellationToken cancellationToken)
    {
        var external = Environment.GetEnvironmentVariable("GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(external))
            return (external, "external:GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING");
        RequireContainers(allowContainers, BenchmarkProvider.SqlServer);
        sqlServer = new MsSqlBuilder(SqlServerImage).Build();
        await sqlServer.StartAsync(cancellationToken);
        return (sqlServer.GetConnectionString(), $"testcontainer:{SqlServerImage}");
    }

    private async Task<(string ConnectionString, string Source)> StartPostgreSqlAsync(
        bool allowContainers,
        CancellationToken cancellationToken)
    {
        var external = Environment.GetEnvironmentVariable("GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(external))
            return (external, "external:GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING");
        RequireContainers(allowContainers, BenchmarkProvider.PostgreSql);
        postgreSql = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("groundwork")
            .WithUsername("groundwork")
            .WithPassword("groundwork")
            .Build();
        await postgreSql.StartAsync(cancellationToken);
        return (postgreSql.GetConnectionString(), $"testcontainer:{PostgreSqlImage}");
    }

    private async Task<(string ConnectionString, string Source)> StartMongoDbAsync(
        bool allowContainers,
        CancellationToken cancellationToken)
    {
        var external = Environment.GetEnvironmentVariable("GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(external))
            return (external, "external:GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING");
        RequireContainers(allowContainers, BenchmarkProvider.MongoDb);
        mongoDb = new MongoDbBuilder(MongoDbImage).WithReplicaSet("groundwork-rs").Build();
        await mongoDb.StartAsync(cancellationToken);
        return (mongoDb.GetConnectionString(), $"testcontainer:{MongoDbImage};replicaSet=groundwork-rs");
    }

    private static void RequireContainers(bool allowContainers, BenchmarkProvider provider)
    {
        if (!allowContainers)
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' needs its documented connection-string environment variable or container startup.");
        }
    }

    private static string Required(string? value, BenchmarkProvider provider) =>
        value ?? throw new InvalidOperationException($"Provider environment '{provider}' was not started.");
}
