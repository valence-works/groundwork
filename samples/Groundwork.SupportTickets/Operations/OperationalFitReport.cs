using Groundwork.Core.Capabilities;

namespace Groundwork.SupportTickets.Operations;

/// <summary>
/// The capability-derived verdict for the operational manifest evaluated against two providers. The
/// same requirements yield <see cref="ProviderFit.Supported"/> on an operational-capable provider and
/// <see cref="ProviderFit.Unsupported"/> on a portable document-only provider — proving fit is
/// computed from declared requirements rather than self-declared by the manifest author.
/// </summary>
public sealed record OperationalFitReport(ProviderFit OperationalProvider, ProviderFit DocumentOnlyProvider);
