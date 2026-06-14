using System.Text.Json;
using Groundwork.Operational.Leases;
using Groundwork.Operational.Outbox;
using Groundwork.Operational.UnitOfWork;
using Groundwork.Operational.WorkQueue;
using Groundwork.Sqlite.Operational;

namespace Groundwork.SupportTickets.Operations;

/// <summary>
/// Domain-facing service that runs the support desk's operational hot path on the Groundwork
/// operational store family. It deliberately sits <i>beside</i> the portable
/// <see cref="SupportTicketRepository"/> (which only touches <c>IDocumentStore</c>) so the portable
/// document contract stays clean while operational concerns — FIFO triage, exclusive ownership, and
/// an at-least-once notification outbox — use the dedicated operational seams.
/// </summary>
/// <remarks>
/// Real-world capabilities showcased here:
/// <list type="bullet">
///   <item>Triage queue — newly created tickets are claimed by exactly one agent in FIFO order per
///   priority lane, under a visibility-timeout lease so stalled triage is redelivered.</item>
///   <item>Ticket ownership — only one agent edits a ticket at a time, with fencing tokens so a
///   stalled owner cannot clobber a newer owner's work after the lease expires.</item>
///   <item>Notification outbox — status-change notifications are delivered at least once, in order
///   per ticket, with retry/dead-letter.</item>
///   <item>Atomic escalation — bumping a ticket to a supervisor enqueues supervisor triage <i>and</i>
///   appends a manager notification in one atomic cross-unit commit (all-or-nothing).</item>
/// </list>
/// </remarks>
public sealed class SupportTicketOperations(SqliteOperationalStore store)
{
    private const string SupervisorLane = "supervisor";

    public async Task<long> QueueForTriageAsync(string ticketNumber, string priority, CancellationToken cancellationToken = default)
    {
        var result = await store.WorkQueue.EnqueueAsync(
            new EnqueueRequest(
                SupportTicketOperationsManifest.TriageQueueUnit,
                priority,
                SerializeTriage(ticketNumber, priority)),
            cancellationToken);
        return result.Sequence;
    }

    public async Task<TriageAssignment?> ClaimNextTriageAsync(
        string agentId,
        TimeSpan leaseDuration,
        string? priority = null,
        CancellationToken cancellationToken = default)
    {
        var claimed = await store.WorkQueue.ClaimAsync(
            new ClaimRequest(SupportTicketOperationsManifest.TriageQueueUnit, leaseDuration, BatchSize: 1, PartitionKey: priority),
            cancellationToken);
        if (claimed.Count == 0)
            return null;

        var message = claimed[0];
        var payload = DeserializeTriage(message.Payload);
        return new TriageAssignment(
            message.MessageId,
            payload.TicketNumber,
            message.PartitionKey,
            message.Sequence,
            message.Attempt,
            agentId,
            message.LeaseToken,
            message.LeaseExpiresAt);
    }

    public async Task<bool> CompleteTriageAsync(string messageId, string leaseToken, CancellationToken cancellationToken = default)
    {
        var result = await store.WorkQueue.AcknowledgeAsync(
            new AckRequest(SupportTicketOperationsManifest.TriageQueueUnit, messageId, leaseToken),
            cancellationToken);
        return result.Status is AckStatus.Acknowledged or AckStatus.AlreadyAcknowledged;
    }

    public async Task<bool> ReturnTriageAsync(
        string messageId,
        string leaseToken,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        var result = await store.WorkQueue.AbandonAsync(
            new AbandonRequest(SupportTicketOperationsManifest.TriageQueueUnit, messageId, leaseToken, delay),
            cancellationToken);
        return result.Status is not AbandonStatus.LeaseLost;
    }

    public async Task<TicketOwnership> AcquireOwnershipAsync(
        string ticketNumber,
        string agentId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var outcome = await store.Leases.TryAcquireAsync(
            new AcquireLeaseRequest(SupportTicketOperationsManifest.OwnershipUnit, ticketNumber, agentId, leaseDuration),
            cancellationToken);
        return ToOwnership(agentId, outcome);
    }

    public async Task<TicketOwnership> RenewOwnershipAsync(
        string ticketNumber,
        string agentId,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var outcome = await store.Leases.RenewAsync(
            new RenewLeaseRequest(SupportTicketOperationsManifest.OwnershipUnit, ticketNumber, agentId, fencingToken, leaseDuration),
            cancellationToken);
        return ToOwnership(agentId, outcome);
    }

    public Task<bool> ReleaseOwnershipAsync(
        string ticketNumber,
        string agentId,
        long fencingToken,
        CancellationToken cancellationToken = default) =>
        store.Leases.ReleaseAsync(
            new ReleaseLeaseRequest(SupportTicketOperationsManifest.OwnershipUnit, ticketNumber, agentId, fencingToken),
            cancellationToken);

    public async Task<TicketOwnershipState?> ReadOwnershipAsync(string ticketNumber, CancellationToken cancellationToken = default)
    {
        var state = await store.Leases.ReadAsync(SupportTicketOperationsManifest.OwnershipUnit, ticketNumber, cancellationToken);
        return state is null
            ? null
            : new TicketOwnershipState(state.OwnerId, state.FencingToken, state.ExpiresAt);
    }

    public Task NotifyAsync(
        string ticketNumber,
        string kind,
        string detail,
        CancellationToken cancellationToken = default) =>
        store.Outbox.AppendAsync(
            new OutboxAppendRequest(
                SupportTicketOperationsManifest.NotificationsUnit,
                ticketNumber,
                SerializeNotification(ticketNumber, kind, detail)),
            cancellationToken);

    /// <summary>
    /// Claims and delivers a batch of pending notifications in order. The supplied
    /// <paramref name="channel"/> simulates the downstream side effect (email/SMS/webhook); when it
    /// returns <c>false</c> the message is retried, and exhausting attempts dead-letters it.
    /// </summary>
    public async Task<IReadOnlyList<DispatchedNotification>> DispatchNotificationsAsync(
        Func<DispatchedNotification, bool>? channel = null,
        int batchSize = 16,
        TimeSpan? leaseDuration = null,
        CancellationToken cancellationToken = default)
    {
        channel ??= static _ => true;
        var lease = leaseDuration ?? TimeSpan.FromSeconds(30);
        var deliverable = await store.Outbox.GetDeliverableAsync(
            new GetDeliverableRequest(SupportTicketOperationsManifest.NotificationsUnit, lease, batchSize),
            cancellationToken);

        var delivered = new List<DispatchedNotification>();
        foreach (var message in deliverable)
        {
            var payload = DeserializeNotification(message.Payload);
            var dispatched = new DispatchedNotification(
                message.MessageId,
                payload.TicketNumber,
                payload.Kind,
                payload.Detail,
                message.Sequence,
                message.Attempt);

            var outcome = channel(dispatched) ? DeliveryOutcome.Delivered : DeliveryOutcome.Retry;
            await store.Outbox.RecordDeliveryResultAsync(
                new DeliveryResultRequest(SupportTicketOperationsManifest.NotificationsUnit, message.MessageId, message.LeaseToken, outcome),
                cancellationToken);

            if (outcome == DeliveryOutcome.Delivered)
                delivered.Add(dispatched);
        }

        return delivered;
    }

    /// <summary>
    /// Escalates a ticket to a supervisor: enqueues supervisor-lane triage <i>and</i> appends a
    /// manager notification as one atomic cross-unit commit. Either both land or neither does.
    /// </summary>
    public async Task EscalateAsync(string ticketNumber, string reason, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = await store.BeginAsync(
            OperationalCommitScope.Of(
                SupportTicketOperationsManifest.TriageQueueUnit,
                SupportTicketOperationsManifest.NotificationsUnit),
            cancellationToken);

        await unitOfWork.WorkQueue.EnqueueAsync(
            new EnqueueRequest(
                SupportTicketOperationsManifest.TriageQueueUnit,
                SupervisorLane,
                SerializeTriage(ticketNumber, SupervisorLane)),
            cancellationToken);

        await unitOfWork.Outbox.AppendAsync(
            new OutboxAppendRequest(
                SupportTicketOperationsManifest.NotificationsUnit,
                ticketNumber,
                SerializeNotification(ticketNumber, "escalated", reason)),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    private static TicketOwnership ToOwnership(string requestingAgent, LeaseAcquisition outcome) =>
        outcome switch
        {
            LeaseAcquisition.Acquired acquired =>
                new TicketOwnership(true, requestingAgent, acquired.FencingToken, acquired.ExpiresAt),
            LeaseAcquisition.Denied denied =>
                new TicketOwnership(false, denied.CurrentOwner, 0, denied.ExpiresAt),
            _ => throw new InvalidOperationException("Unknown lease acquisition outcome.")
        };

    private static string SerializeTriage(string ticketNumber, string lane) =>
        JsonSerializer.Serialize(new TriagePayload(ticketNumber, lane));

    private static TriagePayload DeserializeTriage(string payload) =>
        JsonSerializer.Deserialize<TriagePayload>(payload)
            ?? throw new InvalidOperationException("Triage payload was empty.");

    private static string SerializeNotification(string ticketNumber, string kind, string detail) =>
        JsonSerializer.Serialize(new NotificationPayload(ticketNumber, kind, detail));

    private static NotificationPayload DeserializeNotification(string payload) =>
        JsonSerializer.Deserialize<NotificationPayload>(payload)
            ?? throw new InvalidOperationException("Notification payload was empty.");

    private sealed record TriagePayload(string TicketNumber, string Lane);

    private sealed record NotificationPayload(string TicketNumber, string Kind, string Detail);
}

/// <summary>A triage queue item claimed by an agent under a visibility-timeout lease.</summary>
public sealed record TriageAssignment(
    string MessageId,
    string TicketNumber,
    string Lane,
    long Sequence,
    int Attempt,
    string ClaimedBy,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt);

/// <summary>Outcome of an ownership acquire/renew attempt.</summary>
public sealed record TicketOwnership(
    bool Granted,
    string Owner,
    long FencingToken,
    DateTimeOffset ExpiresAt);

/// <summary>Current ownership of a ticket, if any.</summary>
public sealed record TicketOwnershipState(string Owner, long FencingToken, DateTimeOffset ExpiresAt);

/// <summary>A notification delivered out of the outbox.</summary>
public sealed record DispatchedNotification(
    string MessageId,
    string TicketNumber,
    string Kind,
    string Detail,
    long Sequence,
    int Attempt);
