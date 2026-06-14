using Groundwork.Core.Capabilities;

namespace Groundwork.Modules.Inbox;

/// <summary>
/// Capabilities introduced by the (third-party) Inbox module. These live in the module's own
/// namespace (<c>community.inbox.*</c>) and are registered into Groundwork's capability registry via
/// <see cref="InboxModule"/> — without any edit to Groundwork core.
/// </summary>
public static class InboxCapabilities
{
    /// <summary>
    /// Exactly-once / idempotent message consumption: a (consumer, message-key) pair is admitted at
    /// most once, so a redelivered message is recognized as a duplicate and skipped.
    /// </summary>
    public static readonly CapabilityId IdempotentConsumer = new("community.inbox.idempotent-consumer");

    public static readonly CapabilityDescriptor IdempotentConsumerDescriptor = new(
        IdempotentConsumer,
        "Idempotent consumer",
        "Deduplicates redelivered messages so each (consumer, message-key) is processed at most once.",
        EvidenceGatedByDefault: false,
        OwningModule: "community.inbox");
}
