using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;

namespace Groundwork.Materialization;

public static class PortableMaterializationPlanFactory
{
    private static readonly IReadOnlySet<MaterializationOperationKind> AllOperations =
        Enum.GetValues<MaterializationOperationKind>().ToHashSet();

    public static MaterializationPlan Create(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(
                manifest,
                ProviderCapabilityReport.PortableDocumentProvider(provider),
                new MaterializationCapabilityReport(provider, AllOperations, true));
}
