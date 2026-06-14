namespace Groundwork.Core.Capabilities;

/// <summary>
/// Describes a persistence capability: its stable id, human-readable metadata, whether it is
/// evidence-gated by default (a provider must supply benchmark/operational evidence before serving
/// it), and the module that owns it. Descriptors are contributed to an
/// <see cref="ICapabilityRegistry"/> by <see cref="IGroundworkModule"/>s.
/// </summary>
public sealed record CapabilityDescriptor(
    CapabilityId Id,
    string DisplayName,
    string Description,
    bool EvidenceGatedByDefault = false,
    string OwningModule = "groundwork")
{
    public bool Equals(CapabilityDescriptor? other) => other is not null && Id.Equals(other.Id);

    public override int GetHashCode() => Id.GetHashCode();
}
