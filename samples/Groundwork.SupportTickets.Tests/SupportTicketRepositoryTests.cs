using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Relational.Physicalization;
using Groundwork.SupportTickets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.SupportTickets.Tests;

public sealed class SupportTicketRepositoryTests : IAsyncDisposable
{
    private readonly SupportTicketSampleHost host;

    public SupportTicketRepositoryTests()
    {
        host = SupportTicketSampleHost.CreateAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CreateLoadAndQueryTicketsByDeclaredIndexes()
    {
        var opened = await host.Tickets.CreateAsync(NewTicket("TCK-1001", "acme", "open", "high", "triage"));
        await host.Tickets.CreateAsync(NewTicket("TCK-1002", "acme", "open", "low", "triage"));
        await host.Tickets.CreateAsync(NewTicket("TCK-1003", "globex", "open", "high", "triage"));

        var loaded = await host.Tickets.LoadAsync(opened.Ticket.TicketNumber);
        var acmeTickets = await host.Tickets.ListByCustomerAsync("acme");
        var highPriorityTickets = await host.Tickets.ListByStatusAsync("open");
        var triageTickets = await host.Tickets.ListByAssigneeAsync("triage");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Cannot export invoice", loaded.Ticket.Subject);
        Assert.Equal(["TCK-1001", "TCK-1002"], acmeTickets.Select(ticket => ticket.Ticket.TicketNumber).Order());
        Assert.Equal(3, highPriorityTickets.Count);
        Assert.Equal(3, triageTickets.Count);
    }

    [Fact]
    public async Task AssignAndResolveUseOptimisticConcurrencyAndMaintainIndexes()
    {
        var opened = await host.Tickets.CreateAsync(NewTicket("TCK-2001", "acme", "open", "high", "triage"));
        var assigned = await host.Tickets.AssignAsync(opened.Ticket.TicketNumber, "agent-alex", opened.Version);
        var resolved = await host.Tickets.ResolveAsync(opened.Ticket.TicketNumber, assigned.Version, DateTimeOffset.Parse("2026-06-12T10:00:00Z"));

        var assignedTickets = await host.Tickets.ListByAssigneeAsync("agent-alex");
        var resolvedTickets = await host.Tickets.ListByStatusAsync("resolved");
        var openTickets = await host.Tickets.ListByStatusAsync("open");

        Assert.Equal(2, assigned.Version);
        Assert.Equal("assigned", assigned.Ticket.Status);
        Assert.Equal(3, resolved.Version);
        Assert.Equal("resolved", resolved.Ticket.Status);
        Assert.Single(assignedTickets);
        Assert.Single(resolvedTickets);
        Assert.Empty(openTickets);
    }

    [Fact]
    public async Task DuplicateTicketNumberIsReportedAsConflict()
    {
        await host.Tickets.CreateAsync(NewTicket("TCK-3001", "acme", "open", "normal", "triage"));

        var exception = await Assert.ThrowsAsync<SupportTicketConflictException>(() =>
            host.Tickets.CreateAsync(NewTicket("TCK-3001", "globex", "open", "normal", "triage")));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleVersionIsReportedAsConflict()
    {
        var opened = await host.Tickets.CreateAsync(NewTicket("TCK-4001", "acme", "open", "normal", "triage"));
        await host.Tickets.AssignAsync(opened.Ticket.TicketNumber, "agent-alex", opened.Version);

        var exception = await Assert.ThrowsAsync<SupportTicketConflictException>(() =>
            host.Tickets.ResolveAsync(opened.Ticket.TicketNumber, opened.Version, DateTimeOffset.UtcNow));

        Assert.Contains("changed before the update", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredPhysicalizedIndexesCreateOptimizedProjectionForPortableManifest()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-support-index-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        SupportTicketSampleHost? configuredHost = null;

        try
        {
            configuredHost = await SupportTicketSampleHost.CreateAsync(new SupportTicketStorageOptions(
                SupportTicketProvider.Sqlite,
                connectionString,
                PhysicalizedIndexes: new HashSet<string>(StringComparer.Ordinal)
                {
                    SupportTicketManifest.ByTicketNumber,
                    SupportTicketManifest.ByStatus
                }));

            var ticketUnit = configuredHost.Manifest.StorageUnits.Single(unit => unit.Identity.Value == SupportTicketManifest.DocumentKind);
            var fields = PhysicalizationProjection.EligibleFields(ticketUnit);
            var opened = await configuredHost.Tickets.CreateAsync(NewTicket("TCK-5001", "acme", "open", "normal", "triage"));

            Assert.Equal(PhysicalizationKind.Portable, ticketUnit.Physicalization.Kind);
            Assert.Equal([SupportTicketManifest.ByTicketNumber, SupportTicketManifest.ByStatus], fields.Select(field => field.Name));
            Assert.Equal(1, await CountPhysicalizedRowsAsync(connectionString, ticketUnit, opened.Ticket.TicketNumber));
        }
        finally
        {
            if (configuredHost is not null)
                await configuredHost.DisposeAsync();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    private static SupportTicket NewTicket(string number, string customer, string status, string priority, string assignee) =>
        new(
            number,
            customer,
            "Cannot export invoice",
            "The export screen returns an empty file.",
            status,
            priority,
            assignee,
            DateTimeOffset.Parse("2026-06-12T09:00:00Z"));

    private static async Task<long> CountPhysicalizedRowsAsync(string connectionString, StorageUnit unit, string documentId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {RelationalPhysicalizationNames.TableName(unit)}
            WHERE document_kind = @kind AND document_id = @id;
            """;
        command.Parameters.AddWithValue("@kind", unit.Identity.Value);
        command.Parameters.AddWithValue("@id", documentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
