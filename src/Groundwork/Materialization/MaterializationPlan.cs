using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Materialization;
using Groundwork.Core.Validation;

namespace Groundwork.Materialization;

public sealed record MaterializationPlan(
    ProviderIdentity Provider,
    StorageManifestIdentity ManifestIdentity,
    StorageManifestVersion ManifestVersion,
    IReadOnlyList<IProviderMaterializationOperation> Operations,
    SchemaHistoryEntry SchemaHistory,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsPlannable => Diagnostics.All(diagnostic => !diagnostic.IsError);

    public MaterializationPlan RequirePlannable()
    {
        if (IsPlannable)
            return this;

        throw new InvalidOperationException(
            $"Cannot execute an unplannable materialization plan: {string.Join("; ", Diagnostics.Where(x => x.IsError).Select(x => $"{x.Code}: {x.Message}"))}");
    }
}
