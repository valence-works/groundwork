using Groundwork.Core.Capabilities;

namespace Groundwork.SupportTickets.ExternalModules;

/// <summary>
/// Capability-derived verdict for an external module wired into the sample host. The module provider
/// advertises the custom capability; the document-only provider does not.
/// </summary>
public sealed record ExternalModuleFitReport(
    string ModuleName,
    CapabilityId Capability,
    ProviderFit ModuleProvider,
    ProviderFit DocumentOnlyProvider,
    IReadOnlyList<string> CoreOnlyValidationErrors);
