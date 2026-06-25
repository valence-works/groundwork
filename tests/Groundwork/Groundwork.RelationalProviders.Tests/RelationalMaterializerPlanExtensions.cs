using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;
using Groundwork.Relational.Materialization;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this RelationalMaterializerBase materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(PortableMaterializationPlanFactory.Create(manifest, provider), cancellationToken);
}
