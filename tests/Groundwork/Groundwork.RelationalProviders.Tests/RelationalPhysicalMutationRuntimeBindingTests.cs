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
    [InlineData("postgresql", "wrong-target")]
    [InlineData("postgresql", "wrong-index")]
    [InlineData("postgresql", "missing-primary")]
    [InlineData("sqlserver", "wrong-target")]
    [InlineData("sqlserver", "wrong-index")]
    [InlineData("sqlserver", "missing-primary")]
    public void Native_mutation_plan_inspector_fails_closed_on_target_or_index_drift(
        string provider,
        string drift)
    {
        var fixture = Create(provider, PhysicalStorageForm.DedicatedDocumentTable);
        var route = fixture.Model.Target.Routes.Single();
        var storage = fixture.Model.Manifest.StorageUnits.Single().PhysicalStorage!;
        var plan = PhysicalMutationPlanCompiler.Compile(
                route,
                storage,
                RelationalPhysicalQueryRuntime.Capabilities(fixture.Provider, provider))
            .Plans.Single(candidate => candidate.MutationIdentity == "revoke-pending");
        var primary = drift == "wrong-target"
            ? "wrong_primary"
            : route.PrimaryStorage.Name.Identifier;
        var linkedIndex = drift == "wrong-index"
            ? "wrong_index"
            : plan.Predicate.IndexName!.Identifier;
        var includePrimary = drift != "missing-primary";

        if (provider == "postgresql")
        {
            var nodes = new List<Dictionary<string, object?>>();
            if (includePrimary)
            {
                nodes.Add(new Dictionary<string, object?>
                {
                    ["Node Type"] = "Index Scan",
                    ["Relation Name"] = primary,
                    ["Index Name"] = "primary_identity_index"
                });
            }
            nodes.Add(new Dictionary<string, object?>
            {
                ["Node Type"] = "Index Scan",
                ["Relation Name"] = route.LinkedIndexStorage!.Name.Identifier,
                ["Index Name"] = linkedIndex
            });
            var content = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new Dictionary<string, object?>
                {
                    ["Plan"] = new Dictionary<string, object?>
                    {
                        ["Node Type"] = "Nested Loop",
                        ["Plans"] = nodes
                    }
                }
            });
            Assert.Throws<InvalidOperationException>(() =>
                PostgreSqlNativeMutationPlanInspector.Inspect(
                    new RelationalPhysicalNativeQueryPlan("postgresql-json", content),
                    plan,
                    route));
        }
        else
        {
            var primaryNode = includePrimary
                ? $"""<RelOp PhysicalOp="Index Seek"><Object Table="[{primary}]" Index="[primary_identity_index]" /></RelOp>"""
                : string.Empty;
            var content =
                $"""<ShowPlanXML><BatchSequence><Batch><Statements><StmtSimple><QueryPlan>{primaryNode}<RelOp PhysicalOp="Index Seek"><Object Table="[{route.LinkedIndexStorage!.Name.Identifier}]" Index="[{linkedIndex}]" /></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerNativeMutationPlanInspector.Inspect(
                    new RelationalPhysicalNativeQueryPlan("sqlserver-statistics-xml", content),
                    plan,
                    route));
        }
    }

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
            normalizer: normalizer,
            mutationOptions: new(
                IncludeCategoryTransition: transitionField is null,
                IncludeTypedTransitions: transitionField is not null,
                TypedTransitions: new RelationalTypedTransitionTestOptions(
                    priorityTransitionSource ?? "1",
                    priorityTransitionTarget ?? "2",
                    transitionField is null or "priority"
                        ? null
                        : new Dictionary<string, (string Source, string Target)>
                        {
                            [transitionField] = (priorityTransitionSource!, priorityTransitionTarget!)
                        })));
        var other = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            identity,
            includePriority: false,
            normalizer: normalizer,
            mutationOptions: new(IncludeCategoryTransition: true));
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
