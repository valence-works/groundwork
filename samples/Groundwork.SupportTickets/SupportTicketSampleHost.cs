using Groundwork.Core.Capabilities;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;

namespace Groundwork.SupportTickets;

public sealed class SupportTicketSampleHost : IAsyncDisposable
{
    private SupportTicketSampleHost(SqliteConnection connection, SupportTicketRepository tickets)
    {
        Connection = connection;
        Tickets = tickets;
    }

    public SqliteConnection Connection { get; }
    public SupportTicketRepository Tickets { get; }

    public static async Task<SupportTicketSampleHost> CreateAsync(string connectionString = "Data Source=:memory:")
    {
        var connection = new SqliteConnection(connectionString);
        var manifest = SupportTicketManifest.Create();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(
            manifest,
            new ProviderIdentity("groundwork-sqlite", "1.0.0"));

        IDocumentStore store = new SqliteDocumentStore(connection, manifest);
        return new SupportTicketSampleHost(connection, new SupportTicketRepository(store));
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
