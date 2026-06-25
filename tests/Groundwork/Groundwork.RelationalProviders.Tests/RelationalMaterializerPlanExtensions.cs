using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Materialization;
using Groundwork.PostgreSql;
using Groundwork.Relational.Materialization;
using Groundwork.SqlServer;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalMaterializerPlanExtensions
{
    public static Task MaterializeAsync(
        this RelationalMaterializerBase materializer,
        StorageManifest manifest,
        ProviderIdentity provider,
        CancellationToken cancellationToken = default) =>
        materializer.MaterializeAsync(CreatePlan(manifest, provider), cancellationToken);

    private static MaterializationPlan CreatePlan(StorageManifest manifest, ProviderIdentity provider)
    {
        var (runtime, materialization) = provider.Name switch
        {
            "groundwork-postgresql" => (
                PostgreSqlGroundworkCapabilities.Runtime(provider),
                PostgreSqlGroundworkCapabilities.Materialization(provider)),
            "groundwork-sqlserver" => (
                SqlServerGroundworkCapabilities.Runtime(provider),
                SqlServerGroundworkCapabilities.Materialization(provider)),
            _ => throw new InvalidOperationException($"Unknown relational provider '{provider}'.")
        };

        return new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
            .Plan(manifest, runtime, materialization);
    }
}
