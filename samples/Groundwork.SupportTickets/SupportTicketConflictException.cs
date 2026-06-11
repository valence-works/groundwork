namespace Groundwork.SupportTickets;

public sealed class SupportTicketConflictException(string message) : InvalidOperationException(message);
