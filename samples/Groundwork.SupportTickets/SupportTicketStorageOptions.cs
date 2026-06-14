using Groundwork.Core.Manifests;
using Groundwork.Operational;
using Microsoft.Extensions.Configuration;

namespace Groundwork.SupportTickets;

public sealed record SupportTicketStorageOptions(
    SupportTicketProvider Provider,
    string ConnectionString,
    string? DatabaseName = null,
    PhysicalizationPolicy? Physicalization = null)
{
    public PhysicalizationPolicy EffectivePhysicalization => Physicalization ?? PhysicalizationPolicy.Portable;

    /// <summary>
    /// Optional clock for the operational store, allowing tests to deterministically advance time to
    /// exercise lease expiry and visibility timeouts.
    /// </summary>
    public IOperationalClock? OperationalClock { get; init; }

    public static SupportTicketStorageOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Groundwork");
        var provider = Enum.TryParse<SupportTicketProvider>(section["Provider"], ignoreCase: true, out var parsedProvider)
            ? parsedProvider
            : SupportTicketProvider.Sqlite;
        var physicalization = Enum.TryParse<PhysicalizationKind>(section["Physicalization"], ignoreCase: true, out var parsedPhysicalization)
            ? new PhysicalizationPolicy(parsedPhysicalization)
            : PhysicalizationPolicy.Portable;
        var connectionString = section["ConnectionString"] ?? configuration.GetConnectionString("Groundwork")
            ?? (provider == SupportTicketProvider.Sqlite ? "Data Source=support-tickets.db" : null)
            ?? throw new InvalidOperationException($"A connection string must be configured for provider '{provider}'.");

        return new SupportTicketStorageOptions(
            provider,
            connectionString,
            section["DatabaseName"],
            physicalization);
    }
}

public enum SupportTicketProvider
{
    Sqlite,
    PostgreSql,
    SqlServer,
    MongoDb
}
