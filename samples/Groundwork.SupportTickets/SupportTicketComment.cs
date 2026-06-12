namespace Groundwork.SupportTickets;

public sealed record SupportTicketComment(
    string CommentId,
    string TicketNumber,
    string AuthorId,
    string Body,
    DateTimeOffset CreatedAt);
