using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;

namespace Groundwork.MongoDb.Materialization;

internal static class MongoDbMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this MongoDbGroundworkMaterializer materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(PortableMaterializationPlanFactory.Create(manifest, provider), cancellationToken);
}
