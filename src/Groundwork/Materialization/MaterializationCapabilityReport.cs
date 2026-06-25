using Groundwork.Core.Capabilities;

namespace Groundwork.Materialization;

public sealed record MaterializationCapabilityReport(
    ProviderIdentity Provider,
    IReadOnlySet<MaterializationOperationKind> SupportedOperations,
    bool SupportsSchemaHistory);
