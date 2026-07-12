using System.Data.Common;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.MongoDb;
using Groundwork.Relational.Physicalization;
using Groundwork.SupportTickets;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.SupportTickets.Tests;

public abstract class SupportTicketProviderContractTests
{
    [Fact]
    public async Task MaterializesOptimizedTicketingManifestAndRunsTicketWorkflow()
    {
        await using var harness = await CreateHarnessAsync();
        var opened = await harness.Host.Tickets.CreateAsync(NewTicket("TCK-9001", "acme", "open", "high", "triage"));

        var found = await harness.Host.Tickets.FindByTicketNumberAsync(opened.Ticket.TicketNumber);
        var assigned = await harness.Host.Tickets.AssignAsync(opened.Ticket.TicketNumber, "agent-alex", opened.Version);
        var escalated = await harness.Host.Tickets.EscalateAsync(assigned.Ticket.TicketNumber, assigned.Version, DateTimeOffset.Parse("2026-06-12T10:00:00Z"));
        var comment = await harness.Host.Tickets.AddCommentAsync(
            escalated.Ticket.TicketNumber,
            "agent-alex",
            "Customer confirmed this blocks month-end billing.",
            escalated.Version,
            DateTimeOffset.Parse("2026-06-12T10:05:00Z"));
        var commented = await harness.Host.Tickets.LoadAsync(escalated.Ticket.TicketNumber)
            ?? throw new InvalidOperationException("Commented ticket was not found.");
        var resolved = await harness.Host.Tickets.ResolveAsync(commented.Ticket.TicketNumber, commented.Version, DateTimeOffset.Parse("2026-06-12T10:30:00Z"));

        var acmeTickets = await harness.Host.Tickets.ListByCustomerAsync("acme");
        var highPriorityTickets = await harness.Host.Tickets.ListByPriorityAsync("high");
        var resolvedTickets = await harness.Host.Tickets.ListByStatusAsync("resolved");
        var comments = await harness.Host.Tickets.ListCommentsAsync(opened.Ticket.TicketNumber);

        Assert.NotNull(found);
        Assert.Equal("TCK-9001", found.Ticket.TicketNumber);
        Assert.Equal("escalated", escalated.Ticket.Status);
        Assert.Equal("resolved", resolved.Ticket.Status);
        Assert.True(commented.Version > escalated.Version);
        Assert.NotNull(escalated.Ticket.EscalatedAt);
        Assert.Single(acmeTickets);
        Assert.Single(highPriorityTickets);
        Assert.Single(resolvedTickets);
        Assert.Equal(comment.Comment.CommentId, Assert.Single(comments).Comment.CommentId);
        await harness.AssertPhysicalizedTicketAndCommentAsync(resolved, comment);
    }

    [Fact]
    public async Task EnforcesUniqueTicketNumbersAndOptimisticConcurrency()
    {
        await using var harness = await CreateHarnessAsync();
        var opened = await harness.Host.Tickets.CreateAsync(NewTicket("TCK-9002", "acme", "open", "normal", "triage"));
        await harness.Host.Tickets.AssignAsync(opened.Ticket.TicketNumber, "agent-alex", opened.Version);

        var duplicate = await Assert.ThrowsAsync<SupportTicketConflictException>(() =>
            harness.Host.Tickets.CreateAsync(NewTicket("TCK-9002", "globex", "open", "normal", "triage")));
        var staleComment = await Assert.ThrowsAsync<SupportTicketConflictException>(() =>
            harness.Host.Tickets.AddCommentAsync(opened.Ticket.TicketNumber, "agent-alex", "Using the stale version should fail.", opened.Version));
        var staleResolve = await Assert.ThrowsAsync<SupportTicketConflictException>(() =>
            harness.Host.Tickets.ResolveAsync(opened.Ticket.TicketNumber, opened.Version, DateTimeOffset.UtcNow));

        Assert.Contains("already exists", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("changed before the comment", staleComment.Message, StringComparison.Ordinal);
        Assert.Contains("changed before the update", staleResolve.Message, StringComparison.Ordinal);
    }

    protected abstract Task<ISupportTicketProviderHarness> CreateHarnessAsync();

    protected static SupportTicket NewTicket(string number, string customer, string status, string priority, string assignee) =>
        new(
            number,
            customer,
            "Cannot export invoice",
            "The export screen returns an empty file.",
            status,
            priority,
            assignee,
            DateTimeOffset.Parse("2026-06-12T09:00:00Z"),
            SlaDueAt: DateTimeOffset.Parse("2026-06-12T17:00:00Z"));
}

public interface ISupportTicketProviderHarness : IAsyncDisposable
{
    SupportTicketSampleHost Host { get; }
    Task AssertPhysicalizedTicketAndCommentAsync(SupportTicketDocument ticket, SupportTicketCommentDocument comment);
}

public sealed class SqliteSupportTicketProviderTests : SupportTicketProviderContractTests
{
    protected override async Task<ISupportTicketProviderHarness> CreateHarnessAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-support-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var host = await SupportTicketSampleHost.CreateAsync(new SupportTicketStorageOptions(
            SupportTicketProvider.Sqlite,
            connectionString,
            Physicalization: PhysicalizationPolicy.Optimized));
        return new RelationalSupportTicketProviderHarness(
            host,
            () => new SqliteConnection(connectionString),
            databasePath);
    }
}

public sealed class PostgreSqlSupportTicketProviderTests : SupportTicketProviderContractTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    protected override async Task<ISupportTicketProviderHarness> CreateHarnessAsync()
    {
        var connectionString = container.GetConnectionString();
        var host = await SupportTicketSampleHost.CreateAsync(new SupportTicketStorageOptions(
            SupportTicketProvider.PostgreSql,
            connectionString,
            Physicalization: PhysicalizationPolicy.Optimized));
        return new RelationalSupportTicketProviderHarness(host, () => new NpgsqlConnection(connectionString));
    }
}

public sealed class SqlServerSupportTicketProviderTests : SupportTicketProviderContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    protected override async Task<ISupportTicketProviderHarness> CreateHarnessAsync()
    {
        var connectionString = container.GetConnectionString();
        var host = await SupportTicketSampleHost.CreateAsync(new SupportTicketStorageOptions(
            SupportTicketProvider.SqlServer,
            connectionString,
            Physicalization: PhysicalizationPolicy.Optimized));
        return new RelationalSupportTicketProviderHarness(host, () => new SqlConnection(connectionString));
    }
}

public sealed class MongoDbSupportTicketProviderTests : SupportTicketProviderContractTests, IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    protected override async Task<ISupportTicketProviderHarness> CreateHarnessAsync()
    {
        var databaseName = $"groundwork_support_{Guid.NewGuid():N}";
        var host = await SupportTicketSampleHost.CreateAsync(new SupportTicketStorageOptions(
            SupportTicketProvider.MongoDb,
            container.GetConnectionString(),
            databaseName,
            PhysicalizationPolicy.Optimized));
        return new MongoDbSupportTicketProviderHarness(host, container.GetConnectionString(), databaseName);
    }
}

internal sealed class RelationalSupportTicketProviderHarness(
    SupportTicketSampleHost host,
    Func<DbConnection> createConnection,
    string? databasePath = null) : ISupportTicketProviderHarness
{
    public SupportTicketSampleHost Host { get; } = host;

    public async Task AssertPhysicalizedTicketAndCommentAsync(SupportTicketDocument ticket, SupportTicketCommentDocument comment)
    {
        await using var connection = createConnection();
        await connection.OpenAsync();
        Assert.Equal(1, await CountPhysicalizedRowsAsync(connection, SupportTicketManifest.DocumentKind, ticket.Ticket.TicketNumber));
        Assert.Equal(1, await CountPhysicalizedRowsAsync(connection, SupportTicketManifest.CommentDocumentKind, comment.Comment.CommentId));
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (databasePath is not null && File.Exists(databasePath))
            File.Delete(databasePath);
    }

    private static async Task<long> CountPhysicalizedRowsAsync(DbConnection connection, string documentKind, string documentId)
    {
        var manifest = SupportTicketManifest.Create(PhysicalizationPolicy.Optimized);
        var unit = manifest.StorageUnits.Single(unit => unit.Identity.Value == documentKind);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {RelationalPhysicalizationNames.TableName(unit)}
            WHERE document_kind = @kind AND document_id = @id;
            """;
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "id", documentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class MongoDbSupportTicketProviderHarness(
    SupportTicketSampleHost host,
    string connectionString,
    string databaseName) : ISupportTicketProviderHarness
{
    public SupportTicketSampleHost Host { get; } = host;

    public async Task AssertPhysicalizedTicketAndCommentAsync(SupportTicketDocument ticket, SupportTicketCommentDocument comment)
    {
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var manifest = SupportTicketManifest.Create(PhysicalizationPolicy.Optimized);
        await AssertPhysicalizedDocumentAsync(database, manifest, SupportTicketManifest.DocumentKind, ticket.Ticket.TicketNumber);
        await AssertPhysicalizedDocumentAsync(database, manifest, SupportTicketManifest.CommentDocumentKind, comment.Comment.CommentId);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await new MongoClient(connectionString).DropDatabaseAsync(databaseName);
    }

    private static async Task AssertPhysicalizedDocumentAsync(IMongoDatabase database, StorageManifest manifest, string documentKind, string documentId)
    {
        var unit = manifest.StorageUnits.Single(unit => unit.Identity.Value == documentKind);
        var document = await database
            .GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(unit))
            .Find(Builders<BsonDocument>.Filter.Eq("_id.id", documentId))
            .SingleAsync();

        Assert.True(document.TryGetValue("physicalized", out var physicalized));
        Assert.NotEmpty(physicalized.AsBsonDocument);
    }
}
