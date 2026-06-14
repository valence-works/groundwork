namespace Groundwork.Core.Capabilities;

/// <summary>
/// Read-only lookup of the capability descriptors known to a Groundwork host. Built by composing the
/// built-in capabilities with module contributions; it is the authority for whether a capability id
/// is recognized and whether it is evidence-gated.
/// </summary>
public interface ICapabilityRegistry
{
    bool IsRegistered(CapabilityId id);

    bool TryGet(CapabilityId id, out CapabilityDescriptor descriptor);

    CapabilityDescriptor Get(CapabilityId id);

    IReadOnlyCollection<CapabilityDescriptor> Descriptors { get; }
}

/// <summary>Mutable builder used by modules to contribute capability descriptors.</summary>
public interface ICapabilityRegistryBuilder
{
    /// <summary>
    /// Registers a capability descriptor. Re-registering the same id with an equivalent descriptor is
    /// a no-op; registering a conflicting descriptor for an existing id throws.
    /// </summary>
    ICapabilityRegistryBuilder Add(CapabilityDescriptor descriptor);
}
