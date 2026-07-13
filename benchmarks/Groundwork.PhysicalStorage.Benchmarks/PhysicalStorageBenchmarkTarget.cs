using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;

namespace Groundwork.PhysicalStorage.Benchmarks;

public abstract class PhysicalStorageBenchmarkTarget : IPhysicalStorageBenchmarkTarget
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly ConcurrentDictionary<BenchmarkWorkload, ConcurrentQueue<string>> preparedIds = new();
    private readonly string instance;
    private long generatedId;
    private int seededCount;

    protected PhysicalStorageBenchmarkTarget(
        BenchmarkProvider provider,
        PhysicalStorageForm storageForm,
        string instance)
    {
        Provider = provider;
        StorageForm = storageForm;
        this.instance = instance;
    }

    public BenchmarkProvider Provider { get; }
    public PhysicalStorageForm StorageForm { get; }
    public abstract string ProviderVersion { get; protected set; }
    public abstract IReadOnlyDictionary<string, string> ProviderConfiguration { get; }

    protected IDocumentStore TenantA { get; private set; } = null!;
    protected IDocumentStore TenantB { get; private set; } = null!;
    protected IBoundedDocumentStore QueriesA { get; private set; } = null!;
    protected string Instance => instance;

    public abstract Task InitializeAsync(CancellationToken cancellationToken);

    public virtual async Task SeedAsync(int seed, int count, CancellationToken cancellationToken)
    {
        var random = new Random(seed);
        const int batchSize = 100;
        for (var offset = 0; offset < count; offset += batchSize)
        {
            await using var unitOfWork = await TenantA.BeginAsync(
                DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind),
                cancellationToken);
            var upper = Math.Min(count, offset + batchSize);
            for (var index = offset; index < upper; index++)
            {
                var status = index % 2 == 0 ? "open" : "closed";
                var rank = random.Next(0, Math.Max(1, count));
                var payload = Payload(status, rank, index % 5 == 0 ? "priority" : "ordinary");
                var result = await unitOfWork.SaveAsync(
                    Save($"seed-{index:D8}", payload),
                    cancellationToken);
                RequireStatus(result, DocumentStoreWriteStatus.Saved, "seed insert");
            }
            await unitOfWork.CommitAsync(cancellationToken);
        }
        seededCount = count;
    }

    public async Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken)
    {
        const string sharedId = "gate-same-id";
        RequireStatus(await TenantA.SaveAsync(Save(sharedId, Payload("open", 9, "gate")), cancellationToken),
            DocumentStoreWriteStatus.Saved, "tenant A gate insert");
        RequireStatus(await TenantB.SaveAsync(Save(sharedId, Payload("closed", 3, "gate")), cancellationToken),
            DocumentStoreWriteStatus.Saved, "tenant B gate insert");
        var tenantA = await TenantA.LoadAsync(BenchmarkModelFactory.DocumentKind, sharedId, cancellationToken);
        var tenantB = await TenantB.LoadAsync(BenchmarkModelFactory.DocumentKind, sharedId, cancellationToken);
        var scopeIsolation = tenantA?.ContentJson.Contains("\"open\"", StringComparison.Ordinal) == true &&
                             tenantB?.ContentJson.Contains("\"closed\"", StringComparison.Ordinal) == true;
        if (!scopeIsolation)
            throw new InvalidOperationException("Correctness gate failed: storage scope is not part of document identity.");

        var conflict = await TenantA.SaveAsync(
            Save(sharedId, Payload("closed", 1, "stale"), expectedVersion: 0),
            cancellationToken);
        if (conflict.Status != DocumentStoreWriteStatus.ConcurrencyConflict)
            throw new InvalidOperationException($"Correctness gate failed: stale write returned {conflict.Status}.");

        const string rolledBackId = "gate-rolled-back";
        await using (var unitOfWork = await TenantA.BeginAsync(
                         DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind),
                         cancellationToken))
        {
            RequireStatus(await unitOfWork.SaveAsync(
                    Save(rolledBackId, Payload("open", 1, "rollback")),
                    cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "rollback gate insert");
            await unitOfWork.RollbackAsync(cancellationToken);
        }
        var rollbackWorked = await TenantA.LoadAsync(
            BenchmarkModelFactory.DocumentKind,
            rolledBackId,
            cancellationToken) is null;
        if (!rollbackWorked)
            throw new InvalidOperationException("Correctness gate failed: rolled-back write is visible.");

        var query = Query(skip: 0, take: Math.Min(25, Math.Max(1, seededCount)));
        var page = await QueriesA.QueryAsync(query, cancellationToken);
        var count = await QueriesA.CountAsync(query.Select(BoundedQueryResultOperation.Count), cancellationToken);
        var ranks = page.Documents.Select(document => ReadRank(document.ContentJson)).ToArray();
        var mixedOrder = ranks.SequenceEqual(ranks.OrderDescending());
        if (page.TotalCount <= 0 || count != page.TotalCount)
            throw new InvalidOperationException("Correctness gate failed: bounded query count and page disagree.");
        if (!mixedOrder)
            throw new InvalidOperationException("Correctness gate failed: compound descending rank order is not preserved.");

        return new CorrectnessGateResult(true, true, true, true, true);
    }

    public abstract Task<NativePlanEvidence> RunNativePlanGateAsync(CancellationToken cancellationToken);

    public virtual async Task PrepareWorkloadAsync(
        BenchmarkWorkload workload,
        int totalIterations,
        int operationsPerIteration,
        CancellationToken cancellationToken)
    {
        if (workload is not (BenchmarkWorkload.Update or BenchmarkWorkload.Delete or BenchmarkWorkload.OptimisticConcurrency))
            return;

        var queue = preparedIds.GetOrAdd(workload, _ => new ConcurrentQueue<string>());
        var total = totalIterations * operationsPerIteration;
        for (var index = 0; index < total; index++)
        {
            var id = NewId($"prepare-{workload}");
            RequireStatus(await TenantA.SaveAsync(Save(id, Payload("open", index, "prepared")), cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "workload preparation");
            queue.Enqueue(id);
        }
    }

    public virtual Task PrepareIterationAsync(
        BenchmarkWorkload workload,
        int iteration,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<WorkloadExecution> ExecuteAsync(
        BenchmarkWorkload workload,
        int iteration,
        int operations,
        int concurrency,
        CancellationToken cancellationToken) => workload switch
        {
            BenchmarkWorkload.ClientResetPointReadBatch => await PointReadBatchAsync(operations, resetClient: true, cancellationToken),
            BenchmarkWorkload.ReusedClientPointReadBatch => await PointReadBatchAsync(operations, resetClient: false, cancellationToken),
            BenchmarkWorkload.IndexedQuery => await IndexedQueriesAsync(operations, includeOrdering: false, cancellationToken),
            BenchmarkWorkload.MixedCompoundOrdering => await IndexedQueriesAsync(operations, includeOrdering: true, cancellationToken),
            BenchmarkWorkload.Insert => await InsertsAsync(operations, payloadPadding: 0, cancellationToken),
            BenchmarkWorkload.Update => await UpdatesAsync(operations, cancellationToken),
            BenchmarkWorkload.Delete => await DeletesAsync(operations, cancellationToken),
            BenchmarkWorkload.UnitOfWork => await UnitOfWorkAsync(operations, cancellationToken),
            BenchmarkWorkload.ConcurrentCreate => await ConcurrentCreatesAsync(operations, concurrency, cancellationToken),
            BenchmarkWorkload.OptimisticConcurrency => await ConflictsAsync(operations, cancellationToken),
            BenchmarkWorkload.PaginationAndCount => await PaginationAndCountAsync(operations, iteration, cancellationToken),
            BenchmarkWorkload.BackfillMigration => await ExecuteBackfillMigrationAsync(cancellationToken),
            BenchmarkWorkload.ClientRestartValidation => await ExecuteClientRestartValidationAsync(operations, cancellationToken),
            BenchmarkWorkload.StorageGrowth => await InsertsAsync(operations, payloadPadding: 1_024, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, null)
        };

    public virtual Task ValidateIterationAsync(
        BenchmarkWorkload workload,
        CancellationToken cancellationToken) => workload == BenchmarkWorkload.BackfillMigration
        ? ValidateBackfillMigrationAsync(cancellationToken)
        : Task.CompletedTask;

    public abstract Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    protected void SetStores(IDocumentStore tenantA, IDocumentStore tenantB, IBoundedDocumentStore queriesA)
    {
        TenantA = tenantA ?? throw new ArgumentNullException(nameof(tenantA));
        TenantB = tenantB ?? throw new ArgumentNullException(nameof(tenantB));
        QueriesA = queriesA ?? throw new ArgumentNullException(nameof(queriesA));
    }

    protected virtual Task ResetClientStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task<WorkloadExecution> ExecuteBackfillMigrationAsync(CancellationToken cancellationToken);

    protected abstract Task ValidateBackfillMigrationAsync(CancellationToken cancellationToken);

    protected abstract Task<WorkloadExecution> ExecuteClientRestartValidationAsync(int operations, CancellationToken cancellationToken);

    protected static SaveDocumentRequest Save(string id, string content, long expectedVersion = 0) =>
        new(BenchmarkModelFactory.DocumentKind, id, "1", content, expectedVersion);

    protected static DocumentQuery Query(int? skip = null, int? take = null, bool includeOrdering = true) =>
        new(
            BenchmarkModelFactory.DocumentKind,
            BenchmarkModelFactory.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            includeOrdering ? [new DocumentQueryOrder("rank", PhysicalSortDirection.Descending)] : null,
            skip,
            take);

    protected static string Payload(string status, int rank, string category, int padding = 0) =>
        JsonSerializer.Serialize(
            new BenchmarkPayload(status, rank, category, padding == 0 ? null : new string('x', padding)),
            JsonOptions);

    protected static void RequireStatus(
        DocumentStoreWriteResult result,
        DocumentStoreWriteStatus expected,
        string operation)
    {
        if (result.Status != expected)
            throw new InvalidOperationException($"{operation} returned {result.Status}; expected {expected}.");
    }

    private async Task<WorkloadExecution> PointReadBatchAsync(int operations, bool resetClient, CancellationToken cancellationToken)
    {
        if (resetClient)
            await ResetClientStateAsync(cancellationToken);
        for (var index = 0; index < operations; index++)
        {
            var document = await TenantA.LoadAsync(
                BenchmarkModelFactory.DocumentKind,
                $"seed-{index % seededCount:D8}",
                cancellationToken);
            if (document is null)
                throw new InvalidOperationException("Point-read workload lost a seeded document.");
        }
        return Execution(operations);
    }

    private async Task<WorkloadExecution> IndexedQueriesAsync(
        int operations,
        bool includeOrdering,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < operations; index++)
        {
            var result = await QueriesA.QueryAsync(Query(take: 20, includeOrdering: includeOrdering), cancellationToken);
            if (result.TotalCount <= 0)
                throw new InvalidOperationException("Indexed query workload returned no seeded documents.");
        }
        return Execution(operations);
    }

    private async Task<WorkloadExecution> InsertsAsync(int operations, int payloadPadding, CancellationToken cancellationToken)
    {
        long payloadBytes = 0;
        for (var index = 0; index < operations; index++)
        {
            var payload = Payload("open", index, "write", payloadPadding);
            payloadBytes += Encoding.UTF8.GetByteCount(payload);
            RequireStatus(await TenantA.SaveAsync(Save(NewId("insert"), payload), cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "insert workload");
        }
        return Execution(operations, payloadBytes, operations);
    }

    private async Task<WorkloadExecution> UpdatesAsync(int operations, CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.Update);
        long payloadBytes = 0;
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.Update);
            var payload = Payload("closed", index, "updated");
            payloadBytes += Encoding.UTF8.GetByteCount(payload);
            RequireStatus(await TenantA.SaveAsync(Save(id, payload, expectedVersion: 1), cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "update workload");
        }
        return Execution(operations, payloadBytes, operations);
    }

    private async Task<WorkloadExecution> DeletesAsync(int operations, CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.Delete);
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.Delete);
            RequireStatus(await TenantA.DeleteAsync(
                    new DeleteDocumentRequest(BenchmarkModelFactory.DocumentKind, id, ExpectedVersion: 1),
                    cancellationToken),
                DocumentStoreWriteStatus.Deleted,
                "delete workload");
        }
        return Execution(operations, logicalMutations: operations);
    }

    private async Task<WorkloadExecution> UnitOfWorkAsync(int operations, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await TenantA.BeginAsync(
            DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind),
            cancellationToken);
        long payloadBytes = 0;
        for (var index = 0; index < operations; index++)
        {
            var payload = Payload("open", index, "uow");
            payloadBytes += Encoding.UTF8.GetByteCount(payload);
            RequireStatus(await unitOfWork.SaveAsync(Save(NewId("uow"), payload), cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "unit-of-work workload");
        }
        await unitOfWork.CommitAsync(cancellationToken);
        return Execution(operations, payloadBytes, operations);
    }

    private async Task<WorkloadExecution> ConflictsAsync(int operations, CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.OptimisticConcurrency);
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.OptimisticConcurrency);
            RequireStatus(await TenantA.SaveAsync(
                    Save(id, Payload("closed", index, "stale"), expectedVersion: 0),
                    cancellationToken),
                DocumentStoreWriteStatus.ConcurrencyConflict,
                "optimistic-concurrency workload");
        }
        return Execution(operations);
    }

    private async Task<WorkloadExecution> ConcurrentCreatesAsync(
        int batches,
        int concurrency,
        CancellationToken cancellationToken)
    {
        long payloadBytes = 0;
        for (var batch = 0; batch < batches; batch++)
        {
            var id = NewId("concurrent");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, concurrency).Select(async index =>
            {
                await start.Task.WaitAsync(cancellationToken);
                var payload = Payload("open", index, "concurrent");
                return (Result: await TenantA.SaveAsync(Save(id, payload), cancellationToken), Payload: payload);
            }).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);
            if (results.Count(result => result.Result.Status == DocumentStoreWriteStatus.Saved) != 1 ||
                results.Count(result => result.Result.Status == DocumentStoreWriteStatus.ConcurrencyConflict) != concurrency - 1)
            {
                throw new InvalidOperationException("Concurrent-create workload did not converge to one successful create.");
            }
            payloadBytes += results.Sum(result => Encoding.UTF8.GetByteCount(result.Payload));
        }
        return Execution(batches * concurrency, payloadBytes, batches);
    }

    private async Task<WorkloadExecution> PaginationAndCountAsync(
        int operations,
        int iteration,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < operations; index++)
        {
            var skip = (iteration + index) % Math.Max(1, seededCount / 4);
            var query = Query(skip, take: 20);
            var page = await QueriesA.QueryAsync(query, cancellationToken);
            var count = await QueriesA.CountAsync(query.Select(BoundedQueryResultOperation.Count), cancellationToken);
            if (page.TotalCount != count)
                throw new InvalidOperationException("Pagination/count workload observed inconsistent totals.");
        }
        return Execution(operations * 2);
    }

    private static int ReadRank(string json) => JsonDocument.Parse(json).RootElement.GetProperty("rank").GetInt32();

    private string NewId(string prefix) => $"{instance}-{prefix}-{Interlocked.Increment(ref generatedId):D12}";

    private ConcurrentQueue<string> RequiredQueue(BenchmarkWorkload workload) =>
        preparedIds.TryGetValue(workload, out var queue)
            ? queue
            : throw new InvalidOperationException($"Workload '{workload}' was not prepared.");

    private static string Dequeue(ConcurrentQueue<string> queue, BenchmarkWorkload workload) =>
        queue.TryDequeue(out var id)
            ? id
            : throw new InvalidOperationException($"Prepared ids for '{workload}' are exhausted.");

    protected static WorkloadExecution Execution(
        int operations,
        long logicalPayloadBytes = 0,
        long logicalMutations = 0,
        long? roundTrips = null,
        IReadOnlyDictionary<string, long>? providerWork = null) =>
        new(
            operations,
            logicalPayloadBytes,
            logicalMutations,
            roundTrips,
            providerWork ?? new Dictionary<string, long>());

    private sealed record BenchmarkPayload(string Status, int Rank, string Category, string? Padding);
}
