namespace Groundwork.Core.Capabilities;

/// <summary>
/// Default <see cref="ICapabilityRegistry"/> implementation. Use <see cref="CreateBuilder"/> to
/// compose a registry from the built-in capabilities plus module contributions, or
/// <see cref="Default"/> for the built-ins alone.
/// </summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly IReadOnlyDictionary<CapabilityId, CapabilityDescriptor> descriptors;

    private CapabilityRegistry(IReadOnlyDictionary<CapabilityId, CapabilityDescriptor> descriptors) =>
        this.descriptors = descriptors;

    /// <summary>A registry containing only the built-in <see cref="WellKnownCapabilities"/>.</summary>
    public static CapabilityRegistry Default { get; } = CreateBuilder().Build();

    public IReadOnlyCollection<CapabilityDescriptor> Descriptors => descriptors.Values.ToList();

    public bool IsRegistered(CapabilityId id) => descriptors.ContainsKey(id);

    public bool TryGet(CapabilityId id, out CapabilityDescriptor descriptor) =>
        descriptors.TryGetValue(id, out descriptor!);

    public CapabilityDescriptor Get(CapabilityId id) =>
        descriptors.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Capability '{id}' is not registered.");

    /// <summary>Creates a builder pre-seeded with the built-in capabilities.</summary>
    public static Builder CreateBuilder()
    {
        var builder = new Builder();
        foreach (var descriptor in WellKnownCapabilities.All)
            builder.Add(descriptor);
        return builder;
    }

    public sealed class Builder : ICapabilityRegistryBuilder
    {
        private readonly Dictionary<CapabilityId, CapabilityDescriptor> descriptors = [];

        public ICapabilityRegistryBuilder Add(CapabilityDescriptor descriptor)
        {
            if (descriptors.TryGetValue(descriptor.Id, out var existing))
            {
                if (!existing.Equals(descriptor) ||
                    !string.Equals(existing.OwningModule, descriptor.OwningModule, StringComparison.Ordinal) ||
                    existing.EvidenceGatedByDefault != descriptor.EvidenceGatedByDefault)
                {
                    throw new InvalidOperationException(
                        $"Capability '{descriptor.Id}' is already registered by module '{existing.OwningModule}' with a different definition.");
                }

                return this;
            }

            descriptors.Add(descriptor.Id, descriptor);
            return this;
        }

        public CapabilityRegistry Build() => new(new Dictionary<CapabilityId, CapabilityDescriptor>(descriptors));
    }
}
