using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

/// <summary>
/// Connection-independent precompilation of one storage route's bounded queries. Plan compilation and
/// handler certification run once here; <see cref="Bind"/> then attaches a per-session connection-bound
/// store cheaply. Consumers that admit a route once and open a session per operation compile a plan set
/// at admission and reuse it across every session, instead of recompiling the admitted catalog on each
/// open. Instances are immutable and safe for concurrent <see cref="Bind"/> calls.
/// </summary>
public sealed class RelationalPhysicalQueryPlanSet
{
    private readonly PhysicalQueryPlannerCapabilities capabilities;
    private readonly IReadOnlyList<PhysicalQueryPlan> plans;
    private readonly RelationalPhysicalQueryExplainExecutor? explain;
    private readonly IReadOnlyList<HandlerBlueprint> handlerBlueprints;

    private RelationalPhysicalQueryPlanSet(
        PhysicalQueryPlannerCapabilities capabilities,
        IReadOnlyList<PhysicalQueryPlan> plans,
        RelationalPhysicalQueryExplainExecutor? explain,
        IReadOnlyList<HandlerBlueprint> handlerBlueprints)
    {
        this.capabilities = capabilities;
        this.plans = plans;
        this.explain = explain;
        this.handlerBlueprints = handlerBlueprints;
    }

    /// <summary>The compiled bounded-query plans this route admits. Empty when the route declares none.</summary>
    public IReadOnlyList<PhysicalQueryPlan> Plans => plans;

    internal static RelationalPhysicalQueryPlanSet Compile(
        StorageManifest manifest,
        ExecutableStorageRoute route,
        PhysicalQueryPlannerCapabilities capabilities,
        RelationalPhysicalQueryExplainExecutor? explain) =>
        Compile(manifest, route, capabilities, explain, PhysicalQueryPlanCompiler.Compile);

    internal static RelationalPhysicalQueryPlanSet Compile(
        StorageManifest manifest,
        ExecutableStorageRoute route,
        PhysicalQueryPlannerCapabilities capabilities,
        RelationalPhysicalQueryExplainExecutor? explain,
        Func<ExecutableStorageRoute, StorageUnitPhysicalStorage, PhysicalQueryPlannerCapabilities, PhysicalQueryPlanCompilationResult> compile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(compile);
        var unit = manifest.StorageUnits.Single(candidate => candidate.Identity == route.StorageUnit);
        var storage = unit.PhysicalStorage ?? throw new InvalidOperationException(
            $"Storage unit '{route.StorageUnit.Value}' has no physical query declarations.");
        var compilation = compile(route, storage, capabilities);
        if (!compilation.IsValid)
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var blueprints = capabilities.HandlerIdentities.Select(registration => new HandlerBlueprint(
            registration.Value,
            registration.Key,
            compilation.Plans
                .Where(plan => plan.HandlerIdentity == registration.Value)
                .Select(RelationalPhysicalDocumentQueryHandler.Certify)
                .ToArray())).ToArray();
        return new RelationalPhysicalQueryPlanSet(capabilities, compilation.Plans, explain, blueprints);
    }

    /// <summary>
    /// Binds these precompiled plans to a connection-bound store, producing a runtime that reuses the
    /// single compilation. Cheap enough to call on every session open.
    /// </summary>
    public IBoundedDocumentStore Bind(RelationalPhysicalDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var handlers = handlerBlueprints.Select(blueprint => (IPhysicalDocumentQueryHandler)
            new RelationalPhysicalDocumentQueryHandler(
                blueprint.Identity,
                blueprint.Source,
                store,
                blueprint.Certifications,
                explain)).ToArray();
        return PhysicalQueryDocumentStore.FromCompiledPlans(plans, capabilities, handlers);
    }

    private sealed record HandlerBlueprint(
        string Identity,
        PhysicalQuerySourceKind Source,
        IReadOnlyList<PhysicalQueryHandlerCertification> Certifications);
}
