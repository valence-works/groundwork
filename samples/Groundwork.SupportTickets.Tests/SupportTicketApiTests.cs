using System.Net;
using System.Net.Http.Json;
using Groundwork.SupportTickets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Groundwork.SupportTickets.Tests;

public sealed class SupportTicketApiTests : IAsyncDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-support-api-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> factory;

    public SupportTicketApiTests()
    {
        factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder
                    .UseSetting("Groundwork:Provider", SupportTicketProvider.Sqlite.ToString())
                    .UseSetting("Groundwork:ConnectionString", $"Data Source={databasePath}")
                    .UseSetting("Groundwork:Physicalization", "Optimized");
            });
    }

    [Fact]
    public async Task HttpApiCreatesAssignsCommentsAndResolvesTicket()
    {
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<SupportTicketHealthResponse>("/healthz");
        var create = await client.PostAsJsonAsync("/tickets", new CreateTicketRequest(
            "TCK-API-1",
            "acme",
            "Invoice export fails",
            "The monthly invoice export returns an empty file.",
            "high",
            DateTimeOffset.Parse("2026-06-12T17:00:00Z")));
        var opened = await ReadTicketAsync(create);

        Assert.Equal(SupportTicketProvider.Sqlite.ToString(), health!.Provider);
        Assert.Equal("Optimized", health.Physicalization);

        var assign = await client.PostAsJsonAsync($"/tickets/{opened.Ticket.TicketNumber}/assign", new AssignTicketRequest("agent-alex", opened.Version));
        var assigned = await ReadTicketAsync(assign);

        var comment = await client.PostAsJsonAsync(
            $"/tickets/{opened.Ticket.TicketNumber}/comments",
            new AddCommentRequest("agent-alex", "Customer confirmed this blocks month-end billing.", assigned.Version));
        var savedComment = await ReadCommentAsync(comment);
        var commented = await client.GetFromJsonAsync<SupportTicketResponse>($"/tickets/{opened.Ticket.TicketNumber}");

        var resolve = await client.PostAsJsonAsync($"/tickets/{opened.Ticket.TicketNumber}/resolve", new VersionedTicketRequest(commented!.Version));
        var resolved = await ReadTicketAsync(resolve);
        var comments = await client.GetFromJsonAsync<IReadOnlyList<SupportTicketCommentResponse>>($"/tickets/{opened.Ticket.TicketNumber}/comments");
        var byPriority = await client.GetFromJsonAsync<IReadOnlyList<SupportTicketResponse>>("/tickets?priority=high");

        Assert.Equal("assigned", assigned.Ticket.Status);
        Assert.Equal("resolved", resolved.Ticket.Status);
        Assert.True(commented.Version > assigned.Version);
        Assert.Equal(savedComment.Comment.CommentId, Assert.Single(comments!).Comment.CommentId);
        Assert.Equal("TCK-API-1", Assert.Single(byPriority!).Ticket.TicketNumber);
    }

    [Fact]
    public async Task HttpApiDemonstratesExternalInboxModule()
    {
        using var client = factory.CreateClient();

        var fit = await client.GetFromJsonAsync<ExternalModuleFitResponse>("/modules/inbox/fit");
        var first = await client.PostAsJsonAsync("/modules/inbox/admit", new AdmitInboxMessageRequest("ticket-webhook", "evt-api-1"));
        var redelivery = await client.PostAsJsonAsync("/modules/inbox/admit", new AdmitInboxMessageRequest("ticket-webhook", "evt-api-1"));

        var admitted = await ReadInboxAdmissionAsync(first);
        var duplicate = await ReadInboxAdmissionAsync(redelivery);

        Assert.Equal("community.inbox", fit!.ModuleName);
        Assert.Equal("community.inbox.idempotent-consumer", fit.Capability);
        Assert.Equal("Supported", fit.ModuleProvider.Verdict);
        Assert.Equal("Unsupported", fit.DocumentOnlyProvider.Verdict);
        Assert.Contains(fit.CoreOnlyValidationErrors, error => error.StartsWith("GW-CAP-014:", StringComparison.Ordinal));
        Assert.Equal("Admitted", admitted.Admission);
        Assert.Equal("Duplicate", duplicate.Admission);
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        if (File.Exists(databasePath))
            File.Delete(databasePath);
    }

    private static async Task<SupportTicketResponse> ReadTicketAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected 200 or 201 but received {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<SupportTicketResponse>()
            ?? throw new InvalidOperationException("Ticket response was empty.");
    }

    private static async Task<SupportTicketCommentResponse> ReadCommentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SupportTicketCommentResponse>()
            ?? throw new InvalidOperationException("Comment response was empty.");
    }

    private static async Task<InboxAdmissionResponse> ReadInboxAdmissionAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<InboxAdmissionResponse>()
            ?? throw new InvalidOperationException("Inbox admission response was empty.");
    }

    private sealed record SupportTicketHealthResponse(string Provider, string Physicalization);

    private sealed record AdmitInboxMessageRequest(string Consumer, string MessageKey);

    private sealed record InboxAdmissionResponse(string Consumer, string MessageKey, string Admission);

    private sealed record ExternalModuleFitResponse(
        string ModuleName,
        string Capability,
        FitResponse ModuleProvider,
        FitResponse DocumentOnlyProvider,
        IReadOnlyList<string> CoreOnlyValidationErrors);

    private sealed record FitResponse(string Verdict);
}
