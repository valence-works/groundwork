namespace Groundwork.Core.Capabilities;

/// <summary>
/// Policy describing which capabilities are evidence-gated: a provider must supply
/// benchmark/operational evidence before it may serve them, even if it claims support. The default
/// is derived from the capability registry (each descriptor's
/// <see cref="CapabilityDescriptor.EvidenceGatedByDefault"/>); a host may override.
/// </summary>
public sealed record WorkloadEvidencePolicy(IReadOnlySet<CapabilityId> EvidenceGatedCapabilities)
{
    /// <summary>Evidence policy derived from the built-in capability registry.</summary>
    public static WorkloadEvidencePolicy Default { get; } = FromRegistry(CapabilityRegistry.Default);

    /// <summary>Builds an evidence policy from a registry's descriptor defaults.</summary>
    public static WorkloadEvidencePolicy FromRegistry(ICapabilityRegistry registry) =>
        new(registry.Descriptors
            .Where(descriptor => descriptor.EvidenceGatedByDefault)
            .Select(descriptor => descriptor.Id)
            .ToHashSet());
}
