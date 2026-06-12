using Groundwork.SupportTickets;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = SupportTicketStorageOptions.FromConfiguration(builder.Configuration);
var supportTickets = await SupportTicketSampleHost.CreateAsync(storageOptions);

builder.Services.AddSingleton(supportTickets);
builder.Services.AddSingleton(supportTickets.Tickets);

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(() => supportTickets.DisposeAsync().AsTask().GetAwaiter().GetResult());

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    provider = storageOptions.Provider.ToString(),
    physicalization = storageOptions.EffectivePhysicalization.ToString()
}));

app.MapPost("/tickets", async (CreateTicketRequest request, SupportTicketRepository tickets, CancellationToken cancellationToken) =>
{
    try
    {
        var opened = await tickets.CreateAsync(request.ToTicket(), cancellationToken);
        return Results.Created($"/tickets/{opened.Ticket.TicketNumber}", ToTicketResponse(opened));
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
    CancellationToken cancellationToken) =>
{
    try
    {
        var escalated = await tickets.EscalateAsync(ticketNumber, request.ExpectedVersion, DateTimeOffset.UtcNow, cancellationToken);
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
        return Results.Created($"/tickets/{ticketNumber}/comments/{comment.Comment.CommentId}", ToCommentResponse(comment));
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

app.MapFallbackToFile("index.html");

await app.RunAsync();

static IResult Conflict(Exception exception) => Results.Conflict(new { error = exception.Message });

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

public sealed record SupportTicketResponse(SupportTicket Ticket, long Version);

public sealed record SupportTicketCommentResponse(SupportTicketComment Comment, long Version);

public partial class Program;
