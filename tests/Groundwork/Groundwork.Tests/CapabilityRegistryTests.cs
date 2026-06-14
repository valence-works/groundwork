using Groundwork.Core.Capabilities;
using Xunit;

namespace Groundwork.Tests;

public sealed class CapabilityRegistryTests
{
    private sealed class SampleModule(CapabilityDescriptor descriptor) : IGroundworkModule
    {
        public string Name => "sample.module";

        public void RegisterCapabilities(ICapabilityRegistryBuilder builder) => builder.Add(descriptor);
    }

    [Fact]
    public void DefaultRegistryContainsBuiltInCapabilities()
    {
        Assert.True(CapabilityRegistry.Default.IsRegistered(WellKnownCapabilities.AtomicClaim));
        Assert.True(CapabilityRegistry.Default.IsRegistered(WellKnownCapabilities.AtomicCommit));
    }

    [Fact]
    public void CapabilityIdRejectsNonNamespacedValues()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityId("atomicclaim"));
        Assert.Throws<ArgumentException>(() => new CapabilityId("Groundwork.Operational.AtomicClaim"));
        Assert.Throws<ArgumentException>(() => new CapabilityId(""));
    }

    [Fact]
    public void CatalogComposesModuleCapabilitiesWithBuiltIns()
    {
        var custom = new CapabilityDescriptor(new CapabilityId("sample.module.special"), "Special", "A custom capability.");
        var (registry, _) = new GroundworkModuleCatalog().Add(new SampleModule(custom)).Build();

        Assert.True(registry.IsRegistered(custom.Id));
        Assert.True(registry.IsRegistered(WellKnownCapabilities.AtomicClaim));
    }

    [Fact]
    public void EvidencePolicyIsDerivedFromDescriptorDefaults()
    {
        var gated = new CapabilityDescriptor(new CapabilityId("sample.module.gated"), "Gated", "Evidence gated.", EvidenceGatedByDefault: true);
        var ungated = new CapabilityDescriptor(new CapabilityId("sample.module.open"), "Open", "Not gated.");
        var (_, policy) = new GroundworkModuleCatalog()
            .Add(new SampleModule(gated))
            .Add(new SampleModule(ungated))
            .Build();

        Assert.Contains(gated.Id, policy.EvidenceGatedCapabilities);
        Assert.DoesNotContain(ungated.Id, policy.EvidenceGatedCapabilities);
        // Built-in gated capability is still present.
        Assert.Contains(WellKnownCapabilities.AtomicClaim, policy.EvidenceGatedCapabilities);
    }

    [Fact]
    public void ConflictingRedefinitionThrows()
    {
        var id = new CapabilityId("sample.module.conflict");
        var builder = CapabilityRegistry.CreateBuilder();
        builder.Add(new CapabilityDescriptor(id, "One", "First.", EvidenceGatedByDefault: false));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Add(new CapabilityDescriptor(id, "Two", "Conflicting.", EvidenceGatedByDefault: true)));
    }
}
