using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Sqlite;

namespace Groundwork.SupportTickets.Operations;

/// <summary>
/// Declares the operational storage units behind the support desk's hot path and the capability
/// reports used to demonstrate capability-derived provider fit.
/// <para>
/// Ticket and comment <i>documents</i> stay portable across SQLite / SQL Server / PostgreSQL /
/// Mongo. The operational units below declare what their data <i>needs</i> (ordering, atomic claim,
/// fenced ownership, atomic cross-unit commit). The validator derives whether a provider can host
/// them — a portable document-only provider cannot, which is exactly why these units run on the
/// dedicated operational provider.
/// </para>
/// </summary>
public static class SupportTicketOperationsManifest
{
    /// <summary>FIFO triage queue: new tickets claimed by agents under a visibility-timeout lease.</summary>
    public const string TriageQueueUnit = "triage-queue";

    /// <summary>Per-ticket ordered customer-notification outbox with at-least-once delivery.</summary>
    public const string NotificationsUnit = "ticket-notifications";

    /// <summary>Exclusive "who is editing this ticket" ownership lease with fencing tokens.</summary>
    public const string OwnershipUnit = "ticket-ownership";

    public static StorageManifest Create() =>
        new(
            new StorageManifestIdentity("support-tickets-operational"),
            new StorageManifestOwner("groundwork.sample.support"),
            new StorageManifestVersion("1.0.0"),
            [
                Unit(
                    TriageQueueUnit,
                    "Triage work queue",
                    "Tickets are claimed by exactly one agent in FIFO order per priority lane, with a "
                        + "visibility timeout so stalled work is redelivered and poison tickets dead-letter.",
                    WorkloadIntent.OperationalStream,
                    WellKnownCapabilities.OrderedConsumption,
                    WellKnownCapabilities.AtomicClaim,
                    WellKnownCapabilities.RetryRecovery,
                    WellKnownCapabilities.Idempotency,
                    WellKnownCapabilities.AtomicCommit),
                Unit(
                    NotificationsUnit,
                    "Customer notification outbox",
                    "Status-change notifications are delivered at least once, in order per ticket, with "
                        + "retry and dead-letter on repeated delivery failure.",
                    WorkloadIntent.OperationalStream,
                    WellKnownCapabilities.OrderedConsumption,
                    WellKnownCapabilities.AtomicClaim,
                    WellKnownCapabilities.RetryRecovery,
                    WellKnownCapabilities.Idempotency,
                    WellKnownCapabilities.AtomicCommit),
                Unit(
                    OwnershipUnit,
                    "Ticket ownership lease",
                    "Only one agent may edit a ticket at a time; fencing tokens prevent a stalled owner "
                        + "from overwriting a newer owner's changes after the lease expires.",
                    WorkloadIntent.RuntimeContinuationState,
                    WellKnownCapabilities.LeaseRecovery,
                    WellKnownCapabilities.FencedOwnership)
            ],
            new HashSet<string>(),
            []);

    /// <summary>The dedicated operational provider supports and has evidence for every requirement.</summary>
    public static ProviderCapabilityReport OperationalProvider() =>
        OperationalProvider(new ProviderIdentity("groundwork-sqlite-operational", "1.0.0"));

    /// <summary>
    /// A portable document provider supports atomic document commits, but not the queue and lease
    /// semantics required by the operational storage units.
    /// </summary>
    public static ProviderCapabilityReport DocumentOnlyProvider() =>
        SqliteGroundworkCapabilities.Runtime(new ProviderIdentity("groundwork-document-only", "1.0.0"));

    private static ProviderCapabilityReport OperationalProvider(ProviderIdentity provider)
    {
        var capabilities = WellKnownCapabilities.All.Select(descriptor => descriptor.Id).ToHashSet();
        return new(
            provider,
            capabilities,
            capabilities.ToHashSet(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            []);
    }

    private static StorageUnit Unit(
        string identity,
        string displayName,
        string rationale,
        WorkloadIntent descriptor,
        params CapabilityId[] requirements) =>
        new(
            new StorageUnitIdentity(identity),
            displayName,
            StorageIntent.Operational(rationale, descriptor, requirements),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable);
}
