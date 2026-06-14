using Groundwork.Core.Capabilities;

namespace Groundwork.Modules.Inbox;

/// <summary>
/// The Inbox extension module. Registering it with a <see cref="GroundworkModuleCatalog"/> makes the
/// <see cref="InboxCapabilities.IdempotentConsumer"/> capability known to the validator, so storage
/// units can require it and providers can advertise it — all without modifying Groundwork core.
/// </summary>
public sealed class InboxModule : IGroundworkModule
{
    public string Name => "community.inbox";

    public void RegisterCapabilities(ICapabilityRegistryBuilder builder) =>
        builder.Add(InboxCapabilities.IdempotentConsumerDescriptor);
}
