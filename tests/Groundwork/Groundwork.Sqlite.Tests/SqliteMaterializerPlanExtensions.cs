using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;

namespace Groundwork.Sqlite.Materialization;

internal static class SqliteMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this SqliteGroundworkMaterializer materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(PortableMaterializationPlanFactory.Create(manifest, provider), cancellationToken);
}
