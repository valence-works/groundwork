using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalPhysicalMutationRuntimeBindingTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Provider_runtime_rejects_a_manifest_from_another_store_before_io(string provider)
    {
        var fixture = Create(provider);

        Assert.Throws<ArgumentException>(() => fixture.CreateRuntime(
            fixture.Other.Manifest,
            fixture.Model.Target.Routes.Single(),
            fixture.Provider));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Provider_runtime_rejects_a_route_from_another_store_before_io(string provider)
    {
        var fixture = Create(provider);

        Assert.Throws<ArgumentException>(() => fixture.CreateRuntime(
            fixture.Model.Manifest,
            fixture.Other.Target.Routes.Single(),
            fixture.Provider));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Provider_runtime_rejects_altered_manifest_content_with_reused_identity_and_version_before_io(
        string provider)
    {
        var fixture = Create(provider);
        var manifest = fixture.Model.Manifest;
        var unit = manifest.StorageUnits.Single();
        var storage = unit.PhysicalStorage!;
        var alteredStorage = new StorageUnitPhysicalStorage(
            storage.ProvisioningMode,
            storage.Policy,
            storage.LogicalIndexes,
            storage.BoundedQueries,
            storage.NameOverrides,
            storage.BoundedMutations.Select(mutation => mutation.Identity == "revoke-pending"
                ? new BoundedMutationDeclaration(
                    mutation.Identity,
                    mutation.PredicateQueryIdentity,
                    BoundedMutationAction.Transition("category", ["pending"], "disabled"))
                : mutation).ToArray());
        var alteredManifest = manifest with
        {
            StorageUnits = [unit with { PhysicalStorage = alteredStorage }]
        };

        Assert.Equal(manifest.Identity, alteredManifest.Identity);
        Assert.Equal(manifest.Version, alteredManifest.Version);
        Assert.Throws<ArgumentException>(() => fixture.CreateRuntime(
            alteredManifest,
            fixture.Model.Target.Routes.Single(),
            fixture.Provider));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Provider_runtime_rejects_another_provider_family_before_io(string provider)
    {
        var fixture = Create(provider);

        Assert.Throws<ArgumentException>(() => fixture.CreateRuntime(
            fixture.Model.Manifest,
            fixture.Model.Target.Routes.Single(),
            new Groundwork.Core.Capabilities.ProviderIdentity("different-provider", "1.0.0")));
    }

    private static RuntimeBindingFixture Create(string provider)
    {
        var identity = provider switch
        {
            "sqlserver" => SqlServerGroundworkCapabilities.Provider,
            "postgresql" => PostgreSqlGroundworkCapabilities.Provider,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
        var normalizer = provider == "sqlserver"
            ? SqlServerGroundworkCapabilities.PhysicalNames
            : PostgreSqlGroundworkCapabilities.PhysicalNames;
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            identity,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        var other = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            identity,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        if (provider == "sqlserver")
        {
            var store = new SqlServerPhysicalDocumentStore(
                "Server=127.0.0.1,1;Database=unused;User Id=unused;Password=unused;Encrypt=false",
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new RuntimeBindingFixture(
                model,
                other,
                identity,
                (manifest, route, runtimeProvider) => SqlServerPhysicalMutationRuntime.Create(
                    store,
                    manifest,
                    route,
                    runtimeProvider));
        }
        else
        {
            var store = new PostgreSqlPhysicalDocumentStore(
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new RuntimeBindingFixture(
                model,
                other,
                identity,
                (manifest, route, runtimeProvider) => PostgreSqlPhysicalMutationRuntime.Create(
                    store,
                    manifest,
                    route,
                    runtimeProvider));
        }
    }

    private sealed record RuntimeBindingFixture(
        (Groundwork.Core.Manifests.StorageManifest Manifest, Groundwork.Core.SchemaEvolution.PhysicalSchemaTarget Target) Model,
        (Groundwork.Core.Manifests.StorageManifest Manifest, Groundwork.Core.SchemaEvolution.PhysicalSchemaTarget Target) Other,
        Groundwork.Core.Capabilities.ProviderIdentity Provider,
        Func<Groundwork.Core.Manifests.StorageManifest, ExecutableStorageRoute, Groundwork.Core.Capabilities.ProviderIdentity,
            Groundwork.Documents.Store.IBoundedDocumentMutationStore> CreateRuntime);
}
