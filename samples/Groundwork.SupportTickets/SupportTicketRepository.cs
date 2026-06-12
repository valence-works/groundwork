using System.Text.Json;
using Groundwork.Documents.Store;

namespace Groundwork.SupportTickets;

public sealed class SupportTicketRepository(IDocumentStore store)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<SupportTicketDocument> CreateAsync(SupportTicket ticket, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(ticket.TicketNumber, cancellationToken) is not null)
            throw new SupportTicketConflictException($"Ticket '{ticket.TicketNumber}' already exists.");

        var result = await store.SaveAsync(
            new SaveDocumentRequest(
                SupportTicketManifest.DocumentKind,
                ticket.TicketNumber,
                SupportTicketManifest.SchemaVersion,
                Serialize(ticket)),
            cancellationToken);

        return ToSavedTicket(result, $"Ticket '{ticket.TicketNumber}' already exists.");
    }

    public async Task<SupportTicketDocument?> LoadAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var envelope = await store.LoadAsync(SupportTicketManifest.DocumentKind, ticketNumber, cancellationToken);
        return envelope is null ? null : ToTicket(envelope);
    }

    public async Task<SupportTicketDocument?> FindByTicketNumberAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var tickets = await QueryAsync(SupportTicketManifest.ByTicketNumber, ticketNumber, cancellationToken);
        return tickets.SingleOrDefault();
    }

    public Task<IReadOnlyList<SupportTicketDocument>> ListByCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
        QueryAsync(SupportTicketManifest.ByCustomer, customerId, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByStatusAsync(string status, CancellationToken cancellationToken = default) =>
        QueryAsync(SupportTicketManifest.ByStatus, status, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByAssigneeAsync(string assigneeId, CancellationToken cancellationToken = default) =>
        QueryAsync(SupportTicketManifest.ByAssignee, assigneeId, cancellationToken);

    public Task<IReadOnlyList<SupportTicketDocument>> ListByPriorityAsync(string priority, CancellationToken cancellationToken = default) =>
        QueryAsync(SupportTicketManifest.ByPriority, priority, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketCommentDocument>> ListCommentsAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var envelopes = await store.QueryAsync(
            new DocumentStoreQuery(SupportTicketManifest.CommentDocumentKind, SupportTicketManifest.ByCommentTicket, ticketNumber),
            cancellationToken);

        return envelopes.Select(ToComment).ToList();
    }

    public async Task<SupportTicketDocument> AssignAsync(
        string ticketNumber,
        string assigneeId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { AssigneeId = assigneeId, Status = "assigned" };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketDocument> EscalateAsync(
        string ticketNumber,
        long expectedVersion,
        DateTimeOffset escalatedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { Status = "escalated", EscalatedAt = escalatedAt };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketDocument> ResolveAsync(
        string ticketNumber,
        long expectedVersion,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        var updated = existing.Ticket with { Status = "resolved", ResolvedAt = resolvedAt };
        return await SaveExistingAsync(updated, expectedVersion, cancellationToken);
    }

    public async Task<SupportTicketCommentDocument> AddCommentAsync(
        string ticketNumber,
        string authorId,
        string body,
        long expectedTicketVersion,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await RequireAsync(ticketNumber, cancellationToken);
        if (existing.Version != expectedTicketVersion)
            throw new SupportTicketConflictException($"Ticket '{ticketNumber}' changed before the comment could be saved.");

        var comment = new SupportTicketComment(
            $"comment-{Guid.NewGuid():N}",
            ticketNumber,
            authorId,
            body,
            createdAt ?? DateTimeOffset.UtcNow);
        var result = await store.SaveAsync(
            new SaveDocumentRequest(
                SupportTicketManifest.CommentDocumentKind,
                comment.CommentId,
                SupportTicketManifest.SchemaVersion,
                Serialize(comment)),
            cancellationToken);

        return ToSavedComment(result, $"Comment '{comment.CommentId}' already exists.");
    }

    private async Task<IReadOnlyList<SupportTicketDocument>> QueryAsync(
        string indexName,
        string value,
        CancellationToken cancellationToken)
    {
        var envelopes = await store.QueryAsync(
            new DocumentStoreQuery(SupportTicketManifest.DocumentKind, indexName, value),
            cancellationToken);

        return envelopes.Select(ToTicket).ToList();
    }

    private async Task<SupportTicketDocument> RequireAsync(string ticketNumber, CancellationToken cancellationToken)
    {
        var ticket = await LoadAsync(ticketNumber, cancellationToken);
        return ticket ?? throw new KeyNotFoundException($"Ticket '{ticketNumber}' was not found.");
    }

    private async Task<SupportTicketDocument> SaveExistingAsync(
        SupportTicket ticket,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var result = await store.SaveAsync(
            new SaveDocumentRequest(
                SupportTicketManifest.DocumentKind,
                ticket.TicketNumber,
                SupportTicketManifest.SchemaVersion,
                Serialize(ticket),
                expectedVersion),
            cancellationToken);

        return ToSavedTicket(result, $"Ticket '{ticket.TicketNumber}' changed before the update could be saved.");
    }

    private static SupportTicketDocument ToSavedTicket(DocumentStoreWriteResult result, string conflictMessage) =>
        result.Status switch
        {
            DocumentStoreWriteStatus.Saved => ToTicket(result.Document!),
            DocumentStoreWriteStatus.ConcurrencyConflict => throw new SupportTicketConflictException(conflictMessage),
            DocumentStoreWriteStatus.NotFound => throw new KeyNotFoundException("Ticket was not found."),
            _ => throw new InvalidOperationException($"Unexpected write status '{result.Status}'.")
        };

    private static SupportTicketCommentDocument ToSavedComment(DocumentStoreWriteResult result, string conflictMessage) =>
        result.Status switch
        {
            DocumentStoreWriteStatus.Saved => ToComment(result.Document!),
            DocumentStoreWriteStatus.ConcurrencyConflict => throw new SupportTicketConflictException(conflictMessage),
            DocumentStoreWriteStatus.NotFound => throw new KeyNotFoundException("Comment was not found."),
            _ => throw new InvalidOperationException($"Unexpected write status '{result.Status}'.")
        };

    private static SupportTicketDocument ToTicket(DocumentEnvelope envelope) =>
        new(Deserialize(envelope.ContentJson), envelope.Version);

    private static SupportTicketCommentDocument ToComment(DocumentEnvelope envelope) =>
        new(DeserializeComment(envelope.ContentJson), envelope.Version);

    private static string Serialize<T>(T document) =>
        JsonSerializer.Serialize(document, SerializerOptions);

    private static SupportTicket Deserialize(string contentJson) =>
        JsonSerializer.Deserialize<SupportTicket>(contentJson, SerializerOptions)
        ?? throw new InvalidOperationException("Support ticket document content was empty.");

    private static SupportTicketComment DeserializeComment(string contentJson) =>
        JsonSerializer.Deserialize<SupportTicketComment>(contentJson, SerializerOptions)
        ?? throw new InvalidOperationException("Support ticket comment document content was empty.");
}
