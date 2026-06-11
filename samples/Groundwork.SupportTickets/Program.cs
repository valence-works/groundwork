using Groundwork.SupportTickets;

await using var host = await SupportTicketSampleHost.CreateAsync();

var opened = await host.Tickets.CreateAsync(new SupportTicket(
    "TCK-1001",
    "acme",
    "Invoice export fails",
    "The monthly invoice export returns an empty file.",
    "open",
    "high",
    "triage",
    DateTimeOffset.UtcNow));

var assigned = await host.Tickets.AssignAsync(opened.Ticket.TicketNumber, "agent-alex", opened.Version);
var resolved = await host.Tickets.ResolveAsync(assigned.Ticket.TicketNumber, assigned.Version, DateTimeOffset.UtcNow);
var acmeTickets = await host.Tickets.ListByCustomerAsync("acme");
var resolvedTickets = await host.Tickets.ListByStatusAsync("resolved");

Console.WriteLine($"Created {opened.Ticket.TicketNumber} for {opened.Ticket.CustomerId}.");
Console.WriteLine($"Assigned to {assigned.Ticket.AssigneeId}; final status is {resolved.Ticket.Status}.");
Console.WriteLine($"Customer query returned {acmeTickets.Count} ticket(s).");
Console.WriteLine($"Resolved query returned {resolvedTickets.Count} ticket(s).");
