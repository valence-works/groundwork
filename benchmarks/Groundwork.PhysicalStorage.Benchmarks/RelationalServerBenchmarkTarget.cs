using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;

namespace Groundwork.PhysicalStorage.Benchmarks;

public abstract class RelationalServerBenchmarkTarget : PhysicalStorageBenchmarkTarget
{
    private static readonly IReadOnlySet<IndexValueKind> CanonicalJsonValueKinds =
        new HashSet<IndexValueKind> { IndexValueKind.String, IndexValueKind.Keyword };
    private readonly int migrationDatasetSize;
    private MigrationState? migration;

    protected RelationalServerBenchmarkTarget(
        BenchmarkProvider benchmarkProvider,
        PhysicalStorageForm storageForm,
        string instance,
        int migrationDatasetSize) : base(benchmarkProvider, storageForm, instance)
    {
        this.migrationDatasetSize = migrationDatasetSize;
    }

    protected BenchmarkPhysicalModel Model { get; private set; } = null!;
    protected abstract ProviderIdentity GroundworkProvider { get; }
    protected abstract IProviderPhysicalNameNormalizer PhysicalNames { get; }
    protected abstract string HandlerPrefix { get; }
    protected abstract string ConnectionString { get; }

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await CreateIsolationBoundaryAsync(cancellationToken);
        Model = Compile(Instance, includeCategory: true);
        await PhysicalSchemaApplication.ApplyAsync(
            Model.Target,
            CreateExecutor(),
            cancellationToken: cancellationToken);
        ProviderVersion = await ReadProviderVersionAsync(cancellationToken);
        OpenStores();
    }

    public override async Task PrepareIterationAsync(
        BenchmarkWorkload workload,
        int iteration,
        CancellationToken cancellationToken)
    {
        if (workload != BenchmarkWorkload.BackfillMigration)
            return;
        var suffix = $"migration_{iteration}_{Guid.NewGuid():N}"[..32];
        var initial = Compile(suffix, includeCategory: false);
        var additive = Compile(suffix, includeCategory: true);
        await PhysicalSchemaApplication.ApplyAsync(
            initial.Target,
            CreateExecutor(),
            cancellationToken: cancellationToken);
        var store = CreateStore(initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Scoped(new("tenant-a")));
        const int batchSize = 100;
        for (var offset = 0; offset < migrationDatasetSize; offset += batchSize)
        {
            await using var unitOfWork = await store.BeginAsync(
                DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind),
                cancellationToken);
            for (var index = offset; index < Math.Min(migrationDatasetSize, offset + batchSize); index++)
            {
                RequireStatus(await unitOfWork.SaveAsync(
                        Save($"migration-{index:D8}", Payload("open", index, "migration")),
                        cancellationToken),
                    DocumentStoreWriteStatus.Saved,
                    "migration seed");
            }
            await unitOfWork.CommitAsync(cancellationToken);
        }
        migration = new MigrationState(additive, migrationDatasetSize);
    }

    protected override async Task<WorkloadExecution> ExecuteBackfillMigrationAsync(CancellationToken cancellationToken)
    {
        var state = migration ?? throw new InvalidOperationException("Migration iteration was not prepared.");
        var result = await PhysicalSchemaApplication.ApplyAsync(
            state.Additive.Target,
            CreateExecutor(),
            cancellationToken: cancellationToken);
        if (result.Outcome != PhysicalSchemaApplicationOutcome.Applied)
            throw new InvalidOperationException($"Backfill migration returned {result.Outcome}; expected Applied.");
        return Execution(1, logicalMutations: state.Rows, providerWork: new Dictionary<string, long>
        {
            ["backfilled_documents"] = state.Rows
        });
    }

    protected override async Task ValidateBackfillMigrationAsync(CancellationToken cancellationToken)
    {
        var state = migration ?? throw new InvalidOperationException("Migration iteration was not executed.");
        var route = state.Additive.Route;
        var store = CreateStore(
            state.Additive.Manifest,
            state.Additive.Target.Routes,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var queries = RelationalPhysicalQueryRuntime.Create(
            store,
            state.Additive.Manifest,
            route,
            GroundworkProvider,
            HandlerPrefix,
            CanonicalJsonValueKinds);
        var queryCount = await queries.CountAsync(
            Query().Select(BoundedQueryResultOperation.Count),
            cancellationToken);
        var category = route.ProjectedColumns.Single(column => column.Definition.Path == "category");
        var projectionCount = await CountProjectedRowsAsync(route, category, "migration", cancellationToken);
        if (queryCount != state.Rows || projectionCount != state.Rows)
        {
            throw new InvalidOperationException(
                $"Backfill validation expected {state.Rows} queryable projected rows; query returned {queryCount} and category projection returned {projectionCount}.");
        }
        migration = null;
    }

    protected override async Task<WorkloadExecution> ExecuteClientRestartValidationAsync(
        int operations,
        CancellationToken cancellationToken)
    {
        ClearPools();
        var result = await PhysicalSchemaApplication.ApplyAsync(
            Model.Target,
            CreateExecutor(),
            cancellationToken: cancellationToken);
        if (result.Outcome != PhysicalSchemaApplicationOutcome.NoChanges)
            throw new InvalidOperationException($"Restart validation returned {result.Outcome}; expected NoChanges.");
        OpenStores();
        for (var index = 0; index < operations; index++)
        {
            if (await TenantA.LoadAsync(
                    BenchmarkModelFactory.DocumentKind,
                    $"seed-{index:D8}",
                    cancellationToken) is null)
            {
                throw new InvalidOperationException("Client-restart validation could not load durable seeded data.");
            }
        }
        return Execution(1, providerWork: new Dictionary<string, long> { ["schema_restart_validations"] = 1 });
    }

    protected override Task ResetClientStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearPools();
        OpenStores();
        return Task.CompletedTask;
    }

    private protected RelationalPhysicalQueryCommand RenderPlan(BenchmarkPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = Query(request.Skip, request.Take, request.Ordered);
        var store = CreateStore(Model.Manifest, Model.Target.Routes, DocumentStoreAccess.Scoped(new("tenant-a")));
        return request.Operation == NativePlanOperation.Selection
            ? RelationalPhysicalQueryRuntime.BuildQueryCommand(
                store,
                Model.Manifest,
                Model.Route,
                GroundworkProvider,
                HandlerPrefix,
                query,
                CanonicalJsonValueKinds)
            : RelationalPhysicalQueryRuntime.BuildCountCommand(
                store,
                Model.Manifest,
                Model.Route,
                GroundworkProvider,
                HandlerPrefix,
                query.Select(BoundedQueryResultOperation.Count),
                CanonicalJsonValueKinds);
    }

    protected abstract Task CreateIsolationBoundaryAsync(CancellationToken cancellationToken);
    protected abstract Task DropIsolationBoundaryAsync(CancellationToken cancellationToken);
    protected abstract IPhysicalSchemaExecutor CreateExecutor();
    protected abstract RelationalPhysicalDocumentStore CreateStore(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access);
    protected abstract Task<string> ReadProviderVersionAsync(CancellationToken cancellationToken);
    protected abstract Task<long> CountProjectedRowsAsync(
        ExecutableStorageRoute route,
        ExecutableProjectedColumnRoute projection,
        string value,
        CancellationToken cancellationToken);
    protected abstract void ClearPools();

    protected async ValueTask DisposeServerAsync()
    {
        ClearPools();
        await DropIsolationBoundaryAsync(CancellationToken.None);
    }

    private BenchmarkPhysicalModel Compile(string instance, bool includeCategory) =>
        BenchmarkModelFactory.CompileRelational(
            StorageForm,
            instance,
            GroundworkProvider,
            PhysicalNames,
            includeCategory);

    private void OpenStores()
    {
        var tenantA = CreateStore(Model.Manifest, Model.Target.Routes, DocumentStoreAccess.Scoped(new("tenant-a")));
        var tenantB = CreateStore(Model.Manifest, Model.Target.Routes, DocumentStoreAccess.Scoped(new("tenant-b")));
        SetStores(
            tenantA,
            tenantB,
            RelationalPhysicalQueryRuntime.Create(
                tenantA,
                Model.Manifest,
                Model.Route,
                GroundworkProvider,
                HandlerPrefix,
                CanonicalJsonValueKinds));
    }

    private sealed record MigrationState(BenchmarkPhysicalModel Additive, int Rows);
}
