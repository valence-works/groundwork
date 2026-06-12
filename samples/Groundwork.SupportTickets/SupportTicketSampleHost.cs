using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.Materialization;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.Materialization;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Driver;
using Npgsql;

namespace Groundwork.SupportTickets;

public sealed class SupportTicketSampleHost : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables;

    private SupportTicketSampleHost(
        StorageManifest manifest,
        IDocumentStore store,
        SupportTicketRepository tickets,
        List<IAsyncDisposable> disposables)
    {
        Manifest = manifest;
        Store = store;
        Tickets = tickets;
        this.disposables = disposables;
    }

    public StorageManifest Manifest { get; }
    public IDocumentStore Store { get; }
    public SupportTicketRepository Tickets { get; }

    public static Task<SupportTicketSampleHost> CreateAsync(string connectionString = "Data Source=:memory:") =>
        CreateAsync(new SupportTicketStorageOptions(SupportTicketProvider.Sqlite, connectionString));

    public static async Task<SupportTicketSampleHost> CreateAsync(
        SupportTicketStorageOptions options,
        CancellationToken cancellationToken = default)
    {
        var manifest = SupportTicketManifest.Create(options.EffectivePhysicalization);
        var (store, disposables) = await CreateStoreAsync(options, manifest, cancellationToken);
        return new SupportTicketSampleHost(manifest, store, new SupportTicketRepository(store), disposables);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in disposables)
            await disposable.DisposeAsync();
    }

    private static async Task<(IDocumentStore Store, List<IAsyncDisposable> Disposables)> CreateStoreAsync(
        SupportTicketStorageOptions options,
        StorageManifest manifest,
        CancellationToken cancellationToken)
    {
        var disposables = new List<IAsyncDisposable>();
        switch (options.Provider)
        {
            case SupportTicketProvider.Sqlite:
            {
                var connection = new SqliteConnection(options.ConnectionString);
                disposables.Add(connection);
                await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, Provider("groundwork-sqlite"), cancellationToken);
                return (new SqliteDocumentStore(connection, manifest), disposables);
            }
            case SupportTicketProvider.PostgreSql:
            {
                var connection = new NpgsqlConnection(options.ConnectionString);
                disposables.Add(connection);
                await new PostgreSqlGroundworkMaterializer(connection).MaterializeAsync(manifest, Provider("groundwork-postgresql"), cancellationToken);
                return (new PostgreSqlDocumentStore(connection, manifest), disposables);
            }
            case SupportTicketProvider.SqlServer:
            {
                var connection = new SqlConnection(options.ConnectionString);
                disposables.Add(connection);
                await new SqlServerGroundworkMaterializer(connection).MaterializeAsync(manifest, Provider("groundwork-sqlserver"), cancellationToken);
                return (new SqlServerDocumentStore(connection, manifest), disposables);
            }
            case SupportTicketProvider.MongoDb:
            {
                var client = new MongoClient(options.ConnectionString);
                var database = client.GetDatabase(options.DatabaseName ?? "groundwork_support_tickets");
                await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, Provider("groundwork-mongodb"), cancellationToken);
                return (new MongoDbDocumentStore(database, manifest), disposables);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unsupported support-ticket provider.");
        }
    }

    private static ProviderIdentity Provider(string name) => new(name, "1.0.0");
}
