namespace Groundwork.Core.Capabilities;

/// <summary>
/// A Groundwork extension module. Modules introduce new capabilities (and optionally new contract
/// surfaces and provider implementations in their own packages) without editing Groundwork core.
/// A module registers its capability descriptors into the shared registry; evidence gating is taken
/// from each descriptor's <see cref="CapabilityDescriptor.EvidenceGatedByDefault"/>.
/// </summary>
public interface IGroundworkModule
{
    /// <summary>A stable module name, used as the owning-module attribution and for diagnostics.</summary>
    string Name { get; }

    /// <summary>Contributes this module's capability descriptors to the registry being built.</summary>
    void RegisterCapabilities(ICapabilityRegistryBuilder builder);
}
