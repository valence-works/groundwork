namespace Groundwork.SupportTickets;

public sealed record SupportTicket(
    string TicketNumber,
    string CustomerId,
    string Subject,
    string Description,
    string Status,
    string Priority,
    string AssigneeId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt = null,
    DateTimeOffset? SlaDueAt = null,
    DateTimeOffset? EscalatedAt = null);
