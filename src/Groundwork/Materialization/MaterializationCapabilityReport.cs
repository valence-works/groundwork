using Groundwork.Core.Capabilities;
using Groundwork.Core.Materialization;

namespace Groundwork.Materialization;

public sealed record MaterializationCapabilityReport(
    ProviderIdentity Provider,
    IReadOnlySet<MaterializationOperationKind> SupportedOperations,
    bool SupportsSchemaHistory);
