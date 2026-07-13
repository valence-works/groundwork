using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class MongoDbBenchmarkTarget(
    PhysicalStorageForm storageForm,
    string instance,
    string connectionString,
    int migrationDatasetSize,
    string sourceDescription) : PhysicalStorageBenchmarkTarget(
        BenchmarkProvider.MongoDb,
        storageForm,
        instance)
{
    private readonly string connectionString = connectionString;
    private readonly string databaseName = $"groundwork_bench_{instance}_{storageForm}".ToLowerInvariant();
    private readonly int migrationDatasetSize = migrationDatasetSize;
    private readonly string sourceDescription = sourceDescription;
    private MongoDbPhysicalDocumentStoreHandle? tenantAHandle;
    private MongoDbPhysicalDocumentStoreHandle? tenantBHandle;
    private MigrationState? migration;

    public override string ProviderVersion { get; protected set; } = "unknown";
    public override IReadOnlyDictionary<string, string> ProviderConfiguration => new Dictionary<string, string>
    {
        ["source"] = sourceDescription,
        ["database_per_form"] = "true",
        ["required_topology"] = "replica-set",
        ["factory"] = nameof(MongoDbDocumentStoreFactory.CreatePhysicalAsync)
    };

    private StorageManifest Manifest { get; set; } = null!;
    private MongoDbPhysicalStorageModel Model => tenantAHandle?.Model
        ?? throw new InvalidOperationException("MongoDB target is not initialized.");

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Manifest = BenchmarkModelFactory.CreateManifest(StorageForm, Instance);
        await OpenStoresAsync(cancellationToken);
        using var client = new MongoClient(connectionString);
        var buildInfo = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument("buildInfo", 1),
            cancellationToken: cancellationToken);
        ProviderVersion = buildInfo.GetValue("version", "unknown").AsString;
    }

    public override async Task<NativePlanEvidence> RunNativePlanGateAsync(CancellationToken cancellationToken)
    {
        var planDocument = await tenantAHandle!.Store.ExplainAsync(Query(take: 20), cancellationToken);
        var plan = planDocument.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
        var route = Model.Routes.Single();
        var indexName = route.Indexes.Single().Name.Identifier;
        if (!plan.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
            !plan.Contains("IXSCAN", StringComparison.OrdinalIgnoreCase) ||
            plan.Contains("COLLSCAN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MongoDB native-plan gate rejected the scale-bearing query. Expected IXSCAN '{indexName}'.{Environment.NewLine}{plan}");
        }
        return new NativePlanEvidence(
            Provider.ToString(), StorageForm.ToString(), BenchmarkModelFactory.QueryIdentity,
            (route.LinkedIndexStorage ?? route.PrimaryStorage).Name.Identifier,
            indexName, plan,
            ["IXSCAN is present", $"index {indexName} is selected", "COLLSCAN is absent"]);
    }

    public override async Task PrepareIterationAsync(
        BenchmarkWorkload workload,
        int iteration,
        CancellationToken cancellationToken)
    {
        if (workload != BenchmarkWorkload.BackfillMigration)
            return;
        var suffix = $"migration_{iteration}_{Guid.NewGuid():N}"[..32];
        var initialManifest = BenchmarkModelFactory.CreateManifest(StorageForm, suffix, includeCategory: false);
        var additiveManifest = BenchmarkModelFactory.CreateManifest(StorageForm, suffix, includeCategory: true);
        var initialHandle = await MongoDbDocumentStoreFactory.CreatePhysicalAsync(
            connectionString,
            databaseName,
            initialManifest,
            MongoDbGroundworkCapabilities.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            BenchmarkModelFactory.NamePolicy(suffix),
            cancellationToken: cancellationToken);
        const int batchSize = 100;
        for (var offset = 0; offset < migrationDatasetSize; offset += batchSize)
        {
            await using var unitOfWork = await initialHandle.Store.BeginAsync(
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
        migration = new MigrationState(
            initialHandle,
            MongoDbPhysicalStorageModel.Compile(
                additiveManifest,
                MongoDbGroundworkCapabilities.Provider,
                BenchmarkModelFactory.NamePolicy(suffix)),
            migrationDatasetSize);
    }

    public override async Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken)
    {
        using var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        var route = Model.Routes.Single();
        var collections = new[] { route.PrimaryStorage.Name.Identifier, route.LinkedIndexStorage?.Name.Identifier }
            .Where(name => name is not null).Cast<string>().ToArray();
        long totalBytes = 0;
        long indexBytes = 0;
        long primaryRows = 0;
        long linkedRows = 0;
        foreach (var collection in collections)
        {
            var stats = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument { ["collStats"] = collection, ["scale"] = 1 },
                cancellationToken: cancellationToken);
            var storageBytes = stats.GetValue("storageSize", 0).ToInt64();
            var collectionIndexBytes = stats.GetValue("totalIndexSize", 0).ToInt64();
            var rows = stats.GetValue("count", 0).ToInt64();
            totalBytes += storageBytes + collectionIndexBytes;
            indexBytes += collectionIndexBytes;
            if (collection == route.PrimaryStorage.Name.Identifier)
                primaryRows = rows;
            else
                linkedRows = rows;
        }
        return new StorageSnapshot(totalBytes, indexBytes, primaryRows, linkedRows,
            new Dictionary<string, long>
            {
                ["collection_and_index_storage_bytes"] = totalBytes,
                ["index_storage_bytes"] = indexBytes
            });
    }

    public override async ValueTask DisposeAsync()
    {
        if (tenantAHandle is not null)
            await tenantAHandle.DisposeAsync();
        if (tenantBHandle is not null)
            await tenantBHandle.DisposeAsync();
        if (migration is not null)
            await migration.InitialHandle.DisposeAsync();
        using var client = new MongoClient(connectionString);
        await client.DropDatabaseAsync(databaseName);
    }

    protected override async Task ResetClientStateAsync(CancellationToken cancellationToken)
    {
        await DisposeHandlesAsync();
        await OpenStoresAsync(cancellationToken);
    }

    protected override async Task<WorkloadExecution> ExecuteBackfillMigrationAsync(CancellationToken cancellationToken)
    {
        var state = migration ?? throw new InvalidOperationException("Migration iteration was not prepared.");
        using var client = new MongoClient(connectionString);
        var result = await new MongoDbGroundworkMaterializer(client.GetDatabase(databaseName))
            .MaterializeAsync(state.Additive, cancellationToken: cancellationToken);
        if (result.Outcome != PhysicalSchemaApplicationOutcome.Applied)
            throw new InvalidOperationException($"Backfill migration returned {result.Outcome}; expected Applied.");
        await state.InitialHandle.DisposeAsync();
        migration = null;
        return Execution(1, logicalMutations: state.Rows, providerWork: new Dictionary<string, long>
        {
            ["backfilled_documents"] = state.Rows
        });
    }

    protected override async Task<WorkloadExecution> ExecuteRestartRecoveryAsync(
        int operations,
        CancellationToken cancellationToken)
    {
        await DisposeHandlesAsync();
        await OpenStoresAsync(cancellationToken);
        for (var index = 0; index < operations; index++)
        {
            if (await TenantA.LoadAsync(
                    BenchmarkModelFactory.DocumentKind,
                    $"seed-{index:D8}",
                    cancellationToken) is null)
            {
                throw new InvalidOperationException("Restart/recovery workload could not load durable seeded data.");
            }
        }
        return Execution(operations, providerWork: new Dictionary<string, long> { ["factory_restart_validations"] = 1 });
    }

    private async Task OpenStoresAsync(CancellationToken cancellationToken)
    {
        tenantAHandle = await CreateHandleAsync(DocumentStoreAccess.Scoped(new("tenant-a")), cancellationToken);
        tenantBHandle = await CreateHandleAsync(DocumentStoreAccess.Scoped(new("tenant-b")), cancellationToken);
        SetStores(tenantAHandle.Store, tenantBHandle.Store, tenantAHandle.Store);
    }

    private Task<MongoDbPhysicalDocumentStoreHandle> CreateHandleAsync(
        DocumentStoreAccess access,
        CancellationToken cancellationToken) =>
        MongoDbDocumentStoreFactory.CreatePhysicalAsync(
            connectionString,
            databaseName,
            Manifest,
            MongoDbGroundworkCapabilities.Provider,
            access,
            BenchmarkModelFactory.NamePolicy(Instance),
            cancellationToken: cancellationToken);

    private async Task DisposeHandlesAsync()
    {
        if (tenantAHandle is not null)
            await tenantAHandle.DisposeAsync();
        if (tenantBHandle is not null)
            await tenantBHandle.DisposeAsync();
        tenantAHandle = null;
        tenantBHandle = null;
    }

    private sealed record MigrationState(
        MongoDbPhysicalDocumentStoreHandle InitialHandle,
        MongoDbPhysicalStorageModel Additive,
        int Rows);
}
