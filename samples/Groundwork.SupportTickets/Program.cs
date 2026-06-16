using Groundwork.Core.Capabilities;
using Groundwork.Modules.Inbox;
using Groundwork.SupportTickets;
using Groundwork.SupportTickets.ExternalModules;
using Groundwork.SupportTickets.Operations;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = SupportTicketStorageOptions.FromConfiguration(builder.Configuration);
await using var supportTickets = await SupportTicketSampleHost.CreateAsync(storageOptions);

builder.Services.AddSingleton(supportTickets.Tickets);
builder.Services.AddSingleton(supportTickets.Operations);
builder.Services.AddSingleton(supportTickets.OperationalFit);
builder.Services.AddSingleton(supportTickets.Inbox);
builder.Services.AddSingleton(supportTickets.ExternalModuleFit);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    provider = storageOptions.Provider.ToString(),
    physicalization = storageOptions.EffectivePhysicalization.Kind.ToString()
}));

app.MapPost("/tickets", async (CreateTicketRequest request, SupportTicketRepository tickets, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    try
    {
        var opened = await tickets.CreateAsync(request.ToTicket(), cancellationToken);
        // Operational hot path: queue the new ticket for FIFO triage in its priority lane.
        await operations.QueueForTriageAsync(opened.Ticket.TicketNumber, opened.Ticket.Priority, cancellationToken);
        return Results.Created($"/tickets/{UrlSegment(opened.Ticket.TicketNumber)}", ToTicketResponse(opened));
    }
    catch (SupportTicketConflictException exception)
    {
        return Conflict(exception);
    }
});

app.MapGet("/tickets/{ticketNumber}", async (string ticketNumber, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
{
    var ticket = await tickets.LoadAsync(ticketNumber, cancellationToken);
    return ticket is null ? Results.NotFound() : Results.Ok(ToTicketResponse(ticket));
});

app.MapGet("/tickets", async (
    string? ticketNumber,
    string? customerId,
    string? status,
    string? assigneeId,
    string? priority,
    SupportTicketRepository tickets,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(ticketNumber))
    {
        var ticket = await tickets.FindByTicketNumberAsync(ticketNumber, cancellationToken);
        return ticket is null ? Results.Ok(Array.Empty<SupportTicketResponse>()) : Results.Ok(new[] { ToTicketResponse(ticket) });
    }

    var results = (customerId, status, assigneeId, priority) switch
    {
        ({ Length: > 0 }, _, _, _) => await tickets.ListByCustomerAsync(customerId, cancellationToken),
        (_, { Length: > 0 }, _, _) => await tickets.ListByStatusAsync(status, cancellationToken),
        (_, _, { Length: > 0 }, _) => await tickets.ListByAssigneeAsync(assigneeId, cancellationToken),
        (_, _, _, { Length: > 0 }) => await tickets.ListByPriorityAsync(priority, cancellationToken),
        _ => []
    };

    return Results.Ok(results.Select(ToTicketResponse));
});

app.MapPost("/tickets/{ticketNumber}/assign", async (
    string ticketNumber,
    AssignTicketRequest request,
    SupportTicketRepository tickets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var assigned = await tickets.AssignAsync(ticketNumber, request.AssigneeId, request.ExpectedVersion, cancellationToken);
        return Results.Ok(ToTicketResponse(assigned));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SupportTicketConflictException exception)
    {
        return Conflict(exception);
    }
});

app.MapPost("/tickets/{ticketNumber}/escalate", async (
    string ticketNumber,
    VersionedTicketRequest request,
    SupportTicketRepository tickets,
    SupportTicketOperations operations,
    CancellationToken cancellationToken) =>
{
    try
    {
        var escalated = await tickets.EscalateAsync(ticketNumber, request.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken);
        // Atomic cross-unit commit: supervisor triage + manager notification land together or not at all.
        await operations.EscalateAsync(ticketNumber, "Escalated to supervisor review.", cancellationToken);
        return Results.Ok(ToTicketResponse(escalated));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SupportTicketConflictException exception)
    {
        return Conflict(exception);
    }
});

app.MapPost("/tickets/{ticketNumber}/resolve", async (
    string ticketNumber,
    VersionedTicketRequest request,
    SupportTicketRepository tickets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await tickets.ResolveAsync(ticketNumber, request.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken);
        return Results.Ok(ToTicketResponse(resolved));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SupportTicketConflictException exception)
    {
        return Conflict(exception);
    }
});

app.MapPost("/tickets/{ticketNumber}/comments", async (
    string ticketNumber,
    AddCommentRequest request,
    SupportTicketRepository tickets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var comment = await tickets.AddCommentAsync(ticketNumber, request.AuthorId, request.Body, request.ExpectedTicketVersion, null, cancellationToken);
        return Results.Created($"/tickets/{UrlSegment(ticketNumber)}/comments/{UrlSegment(comment.Comment.CommentId)}", ToCommentResponse(comment));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (SupportTicketConflictException exception)
    {
        return Conflict(exception);
    }
});

app.MapGet("/tickets/{ticketNumber}/comments", async (string ticketNumber, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
{
    var comments = await tickets.ListCommentsAsync(ticketNumber, cancellationToken);
    return Results.Ok(comments.Select(ToCommentResponse));
});

// ---- Operational hot path ---------------------------------------------------------------------

// Capability-derived fit: the same operational requirements are Supported on an operational provider
// and Unsupported on a portable document-only provider — the verdict is computed, not declared.
app.MapGet("/operational/fit", (OperationalFitReport fit) => Results.Ok(new
{
    operationalProvider = DescribeFit(fit.OperationalProvider),
    documentOnlyProvider = DescribeFit(fit.DocumentOnlyProvider)
}));

// ---- External module capability extension ----------------------------------------------------

// Open/closed capability proof: the Inbox module contributes a custom capability that the host
// registers and validates without changing Groundwork core.
app.MapGet("/modules/inbox/fit", (ExternalModuleFitReport fit) => Results.Ok(new
{
    fit.ModuleName,
    Capability = fit.Capability.ToString(),
    moduleProvider = DescribeFit(fit.ModuleProvider),
    documentOnlyProvider = DescribeFit(fit.DocumentOnlyProvider),
    fit.CoreOnlyValidationErrors
}));

// Idempotent inbox: the same (consumer, message-key) is admitted once and then reported duplicate.
app.MapPost("/modules/inbox/admit", async (AdmitInboxMessageRequest request, IInboxStore inbox, CancellationToken cancellationToken) =>
{
    var admission = await inbox.TryAdmitAsync(request.Consumer, request.MessageKey, cancellationToken);
    return Results.Ok(new AdmitInboxMessageResponse(request.Consumer, request.MessageKey, admission.ToString()));
});

// Triage work queue: claim the next ticket (FIFO, exclusive, lease-protected).
app.MapPost("/triage/claim", async (ClaimTriageRequest request, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var assignment = await operations.ClaimNextTriageAsync(
        request.AgentId,
        TimeSpan.FromSeconds(request.LeaseSeconds <= 0 ? 30 : request.LeaseSeconds),
        request.Priority,
        cancellationToken);
    return assignment is null ? Results.NoContent() : Results.Ok(assignment);
});

// Triage work queue: finish a claimed item (ack, fenced by lease token).
app.MapPost("/triage/{messageId}/complete", async (string messageId, CompleteTriageRequest request, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var completed = await operations.CompleteTriageAsync(messageId, request.LeaseToken, cancellationToken);
    return completed ? Results.Ok(new { messageId, completed }) : Results.Conflict(new { error = "Lease lost; triage item was reclaimed." });
});

// Ownership lease: acquire exclusive edit ownership of a ticket with a fencing token.
app.MapPost("/tickets/{ticketNumber}/lock", async (string ticketNumber, LockTicketRequest request, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var ownership = await operations.AcquireOwnershipAsync(
        ticketNumber,
        request.AgentId,
        TimeSpan.FromSeconds(request.LeaseSeconds <= 0 ? 60 : request.LeaseSeconds),
        cancellationToken);
    return ownership.Granted ? Results.Ok(ownership) : Results.Conflict(ownership);
});

// Ownership lease: release ownership (requires the matching fencing token).
app.MapPost("/tickets/{ticketNumber}/unlock", async (string ticketNumber, UnlockTicketRequest request, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var released = await operations.ReleaseOwnershipAsync(ticketNumber, request.AgentId, request.FencingToken, cancellationToken);
    return released ? Results.Ok(new { ticketNumber, released }) : Results.Conflict(new { error = "Not the current owner." });
});

app.MapGet("/tickets/{ticketNumber}/lock", async (string ticketNumber, SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var state = await operations.ReadOwnershipAsync(ticketNumber, cancellationToken);
    return state is null ? Results.NoContent() : Results.Ok(state);
});

// Notification outbox: dispatch pending notifications in order (at-least-once).
app.MapPost("/notifications/dispatch", async (SupportTicketOperations operations, CancellationToken cancellationToken) =>
{
    var dispatched = await operations.DispatchNotificationsAsync(cancellationToken: cancellationToken);
    return Results.Ok(dispatched);
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

static IResult Conflict(Exception exception) => Results.Conflict(new { error = exception.Message });

static object DescribeFit(ProviderFit fit) => fit switch
{
    ProviderFit.Supported => new { verdict = "Supported", detail = (object?)null },
    ProviderFit.RequiresEvidence requiresEvidence => new { verdict = "RequiresEvidence", detail = (object?)requiresEvidence.Reasons },
    ProviderFit.Unsupported unsupported => new { verdict = "Unsupported", detail = (object?)unsupported.MissingRequirements.Select(requirement => requirement.ToString()) },
    _ => new { verdict = "Unknown", detail = (object?)null }
};

static string UrlSegment(string value) => Uri.EscapeDataString(value);

static SupportTicketResponse ToTicketResponse(SupportTicketDocument document) =>
    new(document.Ticket, document.Version);

static SupportTicketCommentResponse ToCommentResponse(SupportTicketCommentDocument document) =>
    new(document.Comment, document.Version);

public sealed record CreateTicketRequest(
    string TicketNumber,
    string CustomerId,
    string Subject,
    string Description,
    string Priority,
    DateTimeOffset? SlaDueAt = null)
{
    public SupportTicket ToTicket() =>
        new(
            TicketNumber,
            CustomerId,
            Subject,
            Description,
            "open",
            Priority,
            "triage",
            DateTimeOffset.UtcNow,
            SlaDueAt: SlaDueAt);
}

public sealed record AssignTicketRequest(string AssigneeId, long ExpectedVersion);

public sealed record VersionedTicketRequest(long ExpectedVersion);

public sealed record AddCommentRequest(string AuthorId, string Body, long ExpectedTicketVersion);

public sealed record ClaimTriageRequest(string AgentId, int LeaseSeconds = 30, string? Priority = null);

public sealed record CompleteTriageRequest(string LeaseToken);

public sealed record LockTicketRequest(string AgentId, int LeaseSeconds = 60);

public sealed record UnlockTicketRequest(string AgentId, long FencingToken);

public sealed record AdmitInboxMessageRequest(string Consumer, string MessageKey);

public sealed record AdmitInboxMessageResponse(string Consumer, string MessageKey, string Admission);

public sealed record SupportTicketResponse(SupportTicket Ticket, long Version);

public sealed record SupportTicketCommentResponse(SupportTicketComment Comment, long Version);

public partial class Program;
