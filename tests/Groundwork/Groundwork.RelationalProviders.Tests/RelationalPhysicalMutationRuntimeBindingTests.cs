using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.Provider.Relational;
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
        Assert.Equal(0, fixture.ConnectionFactoryCalls());
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
        Assert.Equal(0, fixture.ConnectionFactoryCalls());
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
        Assert.Equal(0, fixture.ConnectionFactoryCalls());
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
        Assert.Equal(0, fixture.ConnectionFactoryCalls());
    }

    [Theory]
    [MemberData(nameof(InvalidTransitionValues))]
    public void Provider_runtime_rejects_invalid_fixed_transition_values_before_io(
        string provider,
        PhysicalStorageForm form,
        string field,
        string source,
        string target)
    {
        var fixture = Create(provider, form, field, source, target);

        Assert.Throws<InvalidDataException>(() => fixture.CreateRuntime(
            fixture.Model.Manifest,
            fixture.Model.Target.Routes.Single(),
            fixture.Provider));
        Assert.Equal(0, fixture.ConnectionFactoryCalls());
    }

    public static IEnumerable<object[]> InvalidTransitionValues()
    {
        foreach (var provider in new[] { "sqlserver", "postgresql" })
            foreach (var form in Enum.GetValues<PhysicalStorageForm>())
            {
                yield return [provider, form, "priority", "not-a-number", "2.0"];
                yield return [provider, form, "priority", "1.0", "not-a-number"];
                yield return [provider, form, "priority", "1000", "2.0"];
                yield return [provider, form, "priority", "1.0", "1000"];
                yield return [provider, form, "enabled", "not-a-boolean", "false"];
                yield return [provider, form, "enabled", "true", "not-a-boolean"];
                yield return [provider, form, "dueAt", "2026-01-01T00:00:00", "2026-02-02T00:00:00Z"];
                yield return [provider, form, "dueAt", "2026-01-01T00:00:00Z", "2026-02-02T00:00:00"];
                yield return [provider, form, "externalId", "not-a-guid", "22222222-2222-2222-2222-222222222222"];
                yield return [provider, form, "externalId", "11111111-1111-1111-1111-111111111111", "not-a-guid"];
            }
    }

    private static RuntimeBindingFixture Create(
        string provider,
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        string? transitionField = null,
        string? priorityTransitionSource = null,
        string? priorityTransitionTarget = null)
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
            form,
            identity,
            includePriority: transitionField is not null,
            priorityType: PortablePhysicalType.Decimal,
            priorityPrecision: 3,
            priorityScale: 1,
            includeCategoryTransition: transitionField is null,
            normalizer: normalizer,
            includeTypedTransitions: transitionField is not null,
            typedTransitions: new RelationalTypedTransitionTestOptions(
                priorityTransitionSource ?? "1",
                priorityTransitionTarget ?? "2",
                transitionField is null or "priority"
                    ? null
                    : new Dictionary<string, (string Source, string Target)>
                    {
                        [transitionField] = (priorityTransitionSource!, priorityTransitionTarget!)
                    }));
        var other = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            identity,
            includePriority: false,
            includeCategoryTransition: true,
            normalizer: normalizer);
        var connectionFactoryCalls = 0;
        var sessions = RelationalSessionFactory.Concurrent(() =>
        {
            Interlocked.Increment(ref connectionFactoryCalls);
            throw new InvalidOperationException("Runtime certification must not create a database connection.");
        });
        if (provider == "sqlserver")
        {
            var store = new SqlServerPhysicalDocumentStore(
                sessions,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new RuntimeBindingFixture(
                model,
                other,
                identity,
                () => connectionFactoryCalls,
                (manifest, route, runtimeProvider) => SqlServerPhysicalMutationRuntime.Create(
                    store,
                    manifest,
                    route,
                    runtimeProvider));
        }
        else
        {
            var store = new PostgreSqlPhysicalDocumentStore(
                sessions,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            return new RuntimeBindingFixture(
                model,
                other,
                identity,
                () => connectionFactoryCalls,
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
        Func<int> ConnectionFactoryCalls,
        Func<Groundwork.Core.Manifests.StorageManifest, ExecutableStorageRoute, Groundwork.Core.Capabilities.ProviderIdentity,
            Groundwork.Documents.Store.IBoundedDocumentMutationStore> CreateRuntime);
}
