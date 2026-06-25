using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.MongoDb;

namespace Groundwork.MongoDb.Materialization;

internal static class MongoDbMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this MongoDbGroundworkMaterializer materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(CreatePlan(manifest, provider), cancellationToken);

    private static MaterializationPlan CreatePlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, MongoDbGroundworkCapabilities.Runtime(provider), MongoDbGroundworkCapabilities.Materialization(provider));
}
