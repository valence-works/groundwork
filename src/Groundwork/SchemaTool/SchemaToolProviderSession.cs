using Groundwork.Core.SchemaEvolution;
using Groundwork.MongoDb.Materialization;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using MongoDB.Driver;

namespace Groundwork.SchemaTool;

internal sealed class SchemaToolProviderSession : IAsyncDisposable
{
    private readonly IAsyncDisposable? resource;

    private SchemaToolProviderSession(IPhysicalSchemaExecutor executor, IAsyncDisposable? resource)
    {
        Executor = executor;
        this.resource = resource;
    }

    public IPhysicalSchemaExecutor Executor { get; }

    public IPhysicalSchemaHistoryInspector Inspector =>
        Executor as IPhysicalSchemaHistoryInspector
        ?? throw new InvalidOperationException("The selected provider does not support non-mutating physical-schema inspection.");

    public static SchemaToolProviderSession Create(
        string provider,
        string connectionString,
        string? database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        switch (provider.ToLowerInvariant())
        {
            case "sqlite":
                var connection = new SqliteConnection(connectionString);
                return new SchemaToolProviderSession(new SqlitePhysicalSchemaExecutor(connection), connection);
            case "sqlserver":
                return new SchemaToolProviderSession(
                    new SqlServerPhysicalSchemaExecutor(connectionString),
                    resource: null);
            case "postgresql":
                return new SchemaToolProviderSession(
                    new PostgreSqlPhysicalSchemaExecutor(connectionString),
                    resource: null);
            case "mongodb":
                var mongoUrl = MongoUrl.Create(connectionString);
                var databaseName = database ?? mongoUrl.DatabaseName;
                if (string.IsNullOrWhiteSpace(databaseName))
                {
                    throw new SchemaToolConfigurationException(
                        "GW-CLI-006",
                        "MongoDB requires '--database' or a database name in the connection URI.");
                }
                var mongo = new MongoClient(mongoUrl).GetDatabase(databaseName);
                return new SchemaToolProviderSession(
                    new MongoDbPhysicalSchemaExecutor(mongo),
                    resource: null);
            default:
                throw new SchemaToolConfigurationException(
                    "GW-CLI-002",
                    "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (resource is not null)
            await resource.DisposeAsync();
    }
}
