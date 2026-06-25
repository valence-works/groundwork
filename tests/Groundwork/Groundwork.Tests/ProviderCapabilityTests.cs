using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Xunit;

namespace Groundwork.Tests;

public sealed class ProviderCapabilityTests
{
    private readonly ProviderCapabilityValidator _validator = new();

    [Fact]
    public void CompatibleCapabilityReportAllowsPlanning()
    {
        var result = _validator.Validate(SampleManifests.MetadataManifest(), SampleManifests.PortableCapabilities());

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UnsupportedRequiredIndexCapabilityBlocksCompatibility()
    {
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            Indexes = IndexCapabilities.All with { SupportsUniqueIndexes = false }
        };

        var result = _validator.Validate(SampleManifests.MetadataManifest(), capabilities);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-007");
    }

    [Fact]
    public void UnsupportedConcurrencyModeBlocksCompatibility()
    {
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            SupportedConcurrencyModes = new HashSet<ConcurrencyKind> { ConcurrencyKind.None }
        };

        var result = _validator.Validate(SampleManifests.MetadataManifest(), capabilities);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-005");
    }

    [Fact]
    public void SupportedFallbackEmitsWarningWithoutChangingManifestIntent()
    {
        var manifest = SampleManifests.MetadataManifest();
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            Warnings = ["Provider will materialize indexes lazily."]
        };

        var result = _validator.Validate(manifest, capabilities);

        Assert.True(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-CAP-002");
        Assert.Equal(ConcurrencyKind.Optimistic, manifest.StorageUnits.Single().Concurrency.Kind);
    }

    [Fact]
    public void UnknownManifestRequiredCapabilityBlocksCompatibility()
    {
        var manifest = SampleManifests.MetadataManifest() with
        {
            RequiredCapabilities = new HashSet<string> { "schema-history", "custom-required-capability" }
        };

        var result = _validator.Validate(manifest, SampleManifests.PortableCapabilities());

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-012");
    }

    [Fact]
    public void OperationalRequirementIsRejectedByPortableRuntimeProvider()
    {
        var manifest = WithSingleUnitIntent(StorageIntent.Operational(
            "Requires atomic task claiming.",
            WorkloadIntent.OperationalStream,
            WellKnownCapabilities.AtomicClaim));

        var result = _validator.Validate(manifest, SampleManifests.PortableCapabilities());

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-004");
    }

    [Fact]
    public void UnsupportedStorageRequirementBlocksCompatibility()
    {
        var manifest = WithSingleUnitIntent(StorageIntent.Operational(
            "Requires task claiming and abandoned claim recovery.",
            WorkloadIntent.OperationalStream,
            WellKnownCapabilities.AtomicClaim,
            WellKnownCapabilities.LeaseRecovery));
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            SupportedCapabilities = new HashSet<CapabilityId> { WellKnownCapabilities.AtomicClaim },
            EvidencedCapabilities = new HashSet<CapabilityId> { WellKnownCapabilities.AtomicClaim }
        };

        var result = _validator.Validate(manifest, capabilities);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-004");
    }

    [Fact]
    public void SupportedAndEvidencedRequirementsAllowCompatibility()
    {
        var manifest = WithSingleUnitIntent(StorageIntent.Operational(
            "Requires task claiming and abandoned claim recovery.",
            WorkloadIntent.OperationalStream,
            WellKnownCapabilities.AtomicClaim,
            WellKnownCapabilities.LeaseRecovery));
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            SupportedCapabilities = new HashSet<CapabilityId>
            {
                WellKnownCapabilities.AtomicClaim,
                WellKnownCapabilities.LeaseRecovery
            },
            EvidencedCapabilities = new HashSet<CapabilityId>
            {
                WellKnownCapabilities.AtomicClaim,
                WellKnownCapabilities.LeaseRecovery
            }
        };

        var result = _validator.Validate(manifest, capabilities);

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void SupportedButUnevidencedRequirementRequiresEvidence()
    {
        var manifest = WithSingleUnitIntent(StorageIntent.Operational(
            "Requires task claiming.",
            WorkloadIntent.OperationalStream,
            WellKnownCapabilities.AtomicClaim));
        var capabilities = SampleManifests.PortableCapabilities() with
        {
            SupportedCapabilities = new HashSet<CapabilityId> { WellKnownCapabilities.AtomicClaim },
            EvidencedCapabilities = new HashSet<CapabilityId>()
        };

        var fit = _validator.Evaluate(manifest, capabilities);
        var result = _validator.Validate(manifest, capabilities);

        Assert.IsType<ProviderFit.RequiresEvidence>(fit);
        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-013");
    }

    [Fact]
    public void EvaluateDerivesSupportedUnsupportedAndRequiresEvidence()
    {
        var manifest = WithSingleUnitIntent(StorageIntent.Operational(
            "Requires task claiming.",
            WorkloadIntent.OperationalStream,
            WellKnownCapabilities.AtomicClaim));

        Assert.IsType<ProviderFit.Unsupported>(
            _validator.Evaluate(manifest, SampleManifests.PortableCapabilities()));

        var supportedNoEvidence = SampleManifests.PortableCapabilities() with
        {
            SupportedCapabilities = new HashSet<CapabilityId> { WellKnownCapabilities.AtomicClaim }
        };
        Assert.IsType<ProviderFit.RequiresEvidence>(_validator.Evaluate(manifest, supportedNoEvidence));

        var operational = SampleManifests.OperationalCapabilities(new ProviderIdentity("operational-test-provider", "1.0.0"));
        Assert.IsType<ProviderFit.Supported>(_validator.Evaluate(manifest, operational));
    }

    [Fact]
    public void ProviderCapabilityReportDoesNotCarryMaterializationCapability()
    {
        var propertyNames = typeof(ProviderCapabilityReport)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain("SupportedMaterializationOperations", propertyNames);
        Assert.DoesNotContain("SupportsSchemaHistory", propertyNames);
    }

    private static StorageManifest WithSingleUnitIntent(StorageIntent intent)
    {
        var manifest = SampleManifests.MetadataManifest();
        return manifest with { StorageUnits = [manifest.StorageUnits.Single() with { Intent = intent }] };
    }
}
