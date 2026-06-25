using Groundwork.Core.Capabilities;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Modules.Inbox;
using Groundwork.Sqlite;

namespace Groundwork.SupportTickets.ExternalModules;

/// <summary>
/// Declares the storage semantics contributed by external modules used by the support-ticket sample.
/// </summary>
public static class SupportTicketExternalModuleManifest
{
    public const string InboxUnit = "incoming-ticket-events";

    public static StorageManifest CreateInboxManifest() =>
        new(
            new StorageManifestIdentity("support-tickets-external-modules"),
            new StorageManifestOwner("groundwork.sample.support"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity(InboxUnit),
                    "Incoming ticket event inbox",
                    StorageIntent.Operational(
                        "Deduplicate redelivered ticket integration events before handling them.",
                        WorkloadIntent.OperationalStream,
                        InboxCapabilities.IdempotentConsumer),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.None,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [],
                    [],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string>(),
            []);

    public static ExternalModuleFitReport EvaluateInboxFit()
    {
        var manifest = CreateInboxManifest();
        var (registry, evidencePolicy) = new GroundworkModuleCatalog().Add(new InboxModule()).Build();
        var validator = new ProviderCapabilityValidator(registry);
        var provider = InboxProvider();
        var coreOnlyValidation = new ProviderCapabilityValidator().Validate(manifest, provider);

        return new ExternalModuleFitReport(
            new InboxModule().Name,
            InboxCapabilities.IdempotentConsumer,
            validator.Evaluate(manifest, provider, evidencePolicy),
            validator.Evaluate(manifest, DocumentOnlyProvider(), evidencePolicy),
            coreOnlyValidation.Errors.Select(error => $"{error.Code}: {error.Message}").ToArray());
    }

    private static ProviderCapabilityReport InboxProvider() =>
        SqliteGroundworkCapabilities
            .Runtime(new ProviderIdentity("community-inbox-sqlite", "1.0.0"))
            .WithCapabilities(InboxCapabilities.IdempotentConsumer);

    private static ProviderCapabilityReport DocumentOnlyProvider() =>
        SqliteGroundworkCapabilities.Runtime(new ProviderIdentity("groundwork-document-only", "1.0.0"));
}
