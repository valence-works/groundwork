using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.Sqlite;

namespace Groundwork.Sqlite.Materialization;

internal static class SqliteMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this SqliteGroundworkMaterializer materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(CreatePlan(manifest, provider), cancellationToken);

    private static MaterializationPlan CreatePlan(StorageManifest manifest, ProviderIdentity provider) =>
        new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, SqliteGroundworkCapabilities.Runtime(provider), SqliteGroundworkCapabilities.Materialization(provider));
}
