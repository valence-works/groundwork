using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Modules.Inbox;
using Xunit;

namespace Groundwork.Modules.Inbox.Tests;

/// <summary>
/// Proves the open/closed capability path: a module introduces a brand-new capability, the registry
/// recognizes it, and <see cref="ProviderCapabilityValidator"/> derives fit for it exactly as for
/// built-ins — all without editing Groundwork core.
/// </summary>
public sealed class InboxCapabilityFitTests
{
    private static StorageManifest InboxManifest() =>
        new(
            new StorageManifestIdentity("inbox-consumer"),
            new StorageManifestOwner("community.inbox"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("event-inbox"),
                    "Event inbox",
                    StorageIntent.Operational(
                        "Deduplicate redelivered integration events.",
                        WorkloadIntent.OperationalStream,
                        InboxCapabilities.IdempotentConsumer),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Global,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [],
                    [],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string>(),
            []);

    private static (ProviderCapabilityValidator Validator, WorkloadEvidencePolicy Policy) ValidatorWithInboxModule()
    {
        var (registry, policy) = new GroundworkModuleCatalog().Add(new InboxModule()).Build();
        return (new ProviderCapabilityValidator(registry), policy);
    }

    [Fact]
    public void FitIsSupportedWhenProviderAdvertisesTheCustomCapability()
    {
        var (validator, policy) = ValidatorWithInboxModule();
        var provider = PortableCapabilities(new ProviderIdentity("inbox-capable-sqlite", "1.0.0"))
            .WithCapabilities(InboxCapabilities.IdempotentConsumer);

        var fit = validator.Evaluate(InboxManifest(), provider, policy);

        Assert.IsType<ProviderFit.Supported>(fit);
    }

    [Fact]
    public void FitIsUnsupportedWhenProviderLacksTheCustomCapability()
    {
        var (validator, policy) = ValidatorWithInboxModule();
        var provider = PortableCapabilities(new ProviderIdentity("document-only", "1.0.0"));

        var fit = validator.Evaluate(InboxManifest(), provider, policy);

        var unsupported = Assert.IsType<ProviderFit.Unsupported>(fit);
        Assert.Contains(InboxCapabilities.IdempotentConsumer, unsupported.MissingRequirements);
    }

    [Fact]
    public void UnregisteredCapabilityIsRejectedByValidate()
    {
        // A core-only validator does not know the module's capability, so it flags it as unregistered.
        var coreValidator = new ProviderCapabilityValidator();
        var provider = PortableCapabilities(new ProviderIdentity("inbox-capable-sqlite", "1.0.0"))
            .WithCapabilities(InboxCapabilities.IdempotentConsumer);

        var result = coreValidator.Validate(InboxManifest(), provider);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-014");
    }

    [Fact]
    public void RegisteredCapabilityPassesValidateAgainstCapableProvider()
    {
        var (validator, _) = ValidatorWithInboxModule();
        var provider = PortableCapabilities(new ProviderIdentity("inbox-capable-sqlite", "1.0.0"))
            .WithCapabilities(InboxCapabilities.IdempotentConsumer);

        var result = validator.Validate(InboxManifest(), provider);

        Assert.True(result.IsCompatible);
    }

    private static ProviderCapabilityReport PortableCapabilities(ProviderIdentity provider) =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            []);
}
