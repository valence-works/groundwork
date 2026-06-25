using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;

namespace Groundwork.Materialization;

public sealed record MaterializationPlan(
    ProviderIdentity Provider,
    StorageManifestIdentity ManifestIdentity,
    StorageManifestVersion ManifestVersion,
    IReadOnlyList<MaterializationOperation> Operations,
    SchemaHistoryEntry SchemaHistory,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsPlannable => Diagnostics.All(diagnostic => !diagnostic.IsError);
}
