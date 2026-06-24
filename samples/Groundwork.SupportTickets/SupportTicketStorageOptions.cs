using Groundwork.Core.Manifests;
using Groundwork.Operational;
using Microsoft.Extensions.Configuration;

namespace Groundwork.SupportTickets;

public sealed record SupportTicketStorageOptions(
    SupportTicketProvider Provider,
    string ConnectionString,
    string? DatabaseName = null,
    PhysicalizationPolicy? Physicalization = null,
    IReadOnlySet<string>? PhysicalizedIndexes = null)
{
    public PhysicalizationPolicy EffectivePhysicalization => Physicalization ?? PhysicalizationPolicy.Portable;
    public IReadOnlySet<string> EffectivePhysicalizedIndexes => PhysicalizedIndexes ?? EmptyPhysicalizedIndexes;

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
        var physicalizedIndexes = ParsePhysicalizedIndexes(section);

        return new SupportTicketStorageOptions(
            provider,
            connectionString,
            section["DatabaseName"],
            physicalization,
            physicalizedIndexes);
    }

    private static readonly IReadOnlySet<string> EmptyPhysicalizedIndexes = new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> ParsePhysicalizedIndexes(IConfigurationSection section)
    {
        var values = section
            .GetSection("PhysicalizedIndexes")
            .GetChildren()
            .Select(child => child.Value)
            .Concat(SplitPhysicalizedIndexes(section["PhysicalizedIndexes"]))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());

        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static IEnumerable<string> SplitPhysicalizedIndexes(string? value) =>
        value?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];
}

public enum SupportTicketProvider
{
    Sqlite,
    PostgreSql,
    SqlServer,
    MongoDb
}
