namespace Groundwork.Core.Capabilities;

/// <summary>
/// Composes a set of <see cref="IGroundworkModule"/>s into a single capability registry and a derived
/// <see cref="WorkloadEvidencePolicy"/>. This is how a host wires its built-in and custom modules
/// together without core edits: the resulting registry is the single authority the validator uses.
/// </summary>
public sealed class GroundworkModuleCatalog
{
    private readonly List<IGroundworkModule> modules = [];

    /// <summary>Registers a module's capabilities. Built-in capabilities are always included.</summary>
    public GroundworkModuleCatalog Add(IGroundworkModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        modules.Add(module);
        return this;
    }

    public IReadOnlyList<IGroundworkModule> Modules => modules;

    /// <summary>Builds a registry seeded with built-ins plus every registered module's capabilities.</summary>
    public CapabilityRegistry BuildRegistry()
    {
        var builder = CapabilityRegistry.CreateBuilder();
        foreach (var module in modules)
            module.RegisterCapabilities(builder);
        return builder.Build();
    }

    /// <summary>Builds the registry and its derived evidence policy together.</summary>
    public (CapabilityRegistry Registry, WorkloadEvidencePolicy EvidencePolicy) Build()
    {
        var registry = BuildRegistry();
        return (registry, WorkloadEvidencePolicy.FromRegistry(registry));
    }
}
