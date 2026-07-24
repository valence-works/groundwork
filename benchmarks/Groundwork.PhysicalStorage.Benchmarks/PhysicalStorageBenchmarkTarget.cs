using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<BenchmarkWorkload, BenchmarkObservableResultVector> observableResults = new();
    private readonly string instance;
    private long generatedId;
    private int seededCount;
    private int selectedDocumentCount;
    private int payloadPaddingBytes;

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

    public virtual Task SeedAsync(int seed, int count, CancellationToken cancellationToken) =>
        SeedAsync(seed, new BenchmarkDataShape(count, 0, 5_000), cancellationToken);

    public virtual async Task SeedAsync(
        int seed,
        BenchmarkDataShape shape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shape);
        shape.Validate();
        var count = shape.DatasetSize;
        payloadPaddingBytes = shape.PayloadPaddingBytes;
        selectedDocumentCount = shape.GetSelectedDocumentCount();
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
                var status = shape.IsSelectedDocument(index) ? "open" : "closed";
                var rank = random.Next(0, Math.Max(1, count));
                var payload = Payload(
                    status,
                    rank,
                    index % 5 == 0 ? "priority" : "ordinary",
                    payloadPaddingBytes);
                var result = await unitOfWork.SaveAsync(
                    Save($"seed-{index:D8}", payload),
                    cancellationToken);
                RequireStatus(result, DocumentStoreWriteStatus.Saved, "seed insert");
            }
            await unitOfWork.CommitAsync(cancellationToken);
        }
        seededCount = count;
        await FinalizeSeedAsync(cancellationToken);
    }

    public async Task<CorrectnessGateResult> RunCorrectnessGateAsync(CancellationToken cancellationToken)
    {
        const string sharedId = "gate-same-id";
        const string tenantBSentinelId = "gate-tenant-b-open-sentinel";
        RequireStatus(await TenantA.SaveAsync(Save(sharedId, Payload("open", 9, "gate")), cancellationToken),
            DocumentStoreWriteStatus.Saved, "tenant A gate insert");
        RequireStatus(await TenantB.SaveAsync(Save(sharedId, Payload("closed", 3, "gate")), cancellationToken),
            DocumentStoreWriteStatus.Saved, "tenant B gate insert");
        RequireStatus(await TenantB.SaveAsync(
                Save(tenantBSentinelId, Payload("open", int.MaxValue, "tenant-b-scope-sentinel")),
                cancellationToken),
            DocumentStoreWriteStatus.Saved,
            "tenant B bounded-query sentinel insert");
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

        var expectedTenantAOpenCount = selectedDocumentCount + 1L;
        var query = Query(skip: 0, take: (int)Math.Min(25, Math.Max(1, expectedTenantAOpenCount)));
        var page = await QueriesA.QueryAsync(query, cancellationToken);
        var count = await QueriesA.CountAsync(query.Select(BoundedQueryResultOperation.Count), cancellationToken);
        BoundedScopeGate.EnsureExpectedTenantPage(page, count, expectedTenantAOpenCount, tenantBSentinelId);
        var ranks = page.Documents.Select(document => ReadRank(document.ContentJson)).ToArray();
        var mixedOrder = ranks.SequenceEqual(ranks.OrderDescending());
        if (!mixedOrder)
            throw new InvalidOperationException("Correctness gate failed: compound descending rank order is not preserved.");

        RequireStatus(await TenantA.DeleteAsync(
                new DeleteDocumentRequest(BenchmarkModelFactory.DocumentKind, sharedId, ExpectedVersion: 1),
                cancellationToken),
            DocumentStoreWriteStatus.Deleted,
            "tenant A correctness-gate cleanup");
        RequireStatus(await TenantB.DeleteAsync(
                new DeleteDocumentRequest(BenchmarkModelFactory.DocumentKind, sharedId, ExpectedVersion: 1),
                cancellationToken),
            DocumentStoreWriteStatus.Deleted,
            "tenant B correctness-gate cleanup");
        RequireStatus(await TenantB.DeleteAsync(
                new DeleteDocumentRequest(BenchmarkModelFactory.DocumentKind, tenantBSentinelId, ExpectedVersion: 1),
                cancellationToken),
            DocumentStoreWriteStatus.Deleted,
            "tenant B correctness-gate sentinel cleanup");
        await FinalizeSeedAsync(cancellationToken);

        return new CorrectnessGateResult(true, true, true, true, true);
    }

    public abstract Task<IReadOnlyList<NativePlanEvidence>> RunNativePlanGatesAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        CancellationToken cancellationToken);

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
            RequireStatus(await TenantA.SaveAsync(
                    Save(id, Payload("open", index, "prepared", payloadPaddingBytes)),
                    cancellationToken),
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
        CancellationToken cancellationToken)
    {
        var operationLatencies = new List<long>();
        var execution = workload switch
        {
            BenchmarkWorkload.ClientResetPointReadBatch => await PointReadBatchAsync(
                operations, resetClient: true, operationLatencies, cancellationToken),
            BenchmarkWorkload.ReusedClientPointReadBatch => await PointReadBatchAsync(
                operations, resetClient: false, operationLatencies, cancellationToken),
            BenchmarkWorkload.IndexedQuery => await IndexedQueriesAsync(
                operations, includeOrdering: false, operationLatencies, cancellationToken),
            BenchmarkWorkload.MixedCompoundOrdering => await IndexedQueriesAsync(
                operations, includeOrdering: true, operationLatencies, cancellationToken),
            BenchmarkWorkload.Insert => await InsertsAsync(
                operations, payloadPaddingBytes, operationLatencies, cancellationToken),
            BenchmarkWorkload.Update => await UpdatesAsync(operations, operationLatencies, cancellationToken),
            BenchmarkWorkload.Delete => await DeletesAsync(operations, operationLatencies, cancellationToken),
            BenchmarkWorkload.UnitOfWork => await UnitOfWorkAsync(operations, operationLatencies, cancellationToken),
            BenchmarkWorkload.ConcurrentCreate => await ConcurrentCreatesAsync(
                operations, concurrency, operationLatencies, cancellationToken),
            BenchmarkWorkload.OptimisticConcurrency => await ConflictsAsync(
                operations, operationLatencies, cancellationToken),
            BenchmarkWorkload.PaginationAndCount => await PaginationAndCountAsync(
                operations, iteration, operationLatencies, cancellationToken),
            BenchmarkWorkload.BackfillMigration => await ObserveAsync(
                () => ExecuteBackfillMigrationAsync(cancellationToken),
                operationLatencies),
            BenchmarkWorkload.ClientRestartValidation => await ObserveAsync(
                () => ExecuteClientRestartValidationAsync(operations, cancellationToken),
                operationLatencies),
            BenchmarkWorkload.StorageGrowth => await InsertsAsync(
                operations,
                payloadPadding: payloadPaddingBytes == 0 ? 1_024 : payloadPaddingBytes,
                operationLatencies,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, null)
        };
        var observed = execution.ObservableResultVector ??
                       throw new InvalidOperationException(
                           $"Workload '{workload}' returned no observable result vector.");
        return execution with
        {
            OperationLatencyNanoseconds = operationLatencies,
            ObservableResultVector = observableResults.GetOrAdd(workload, observed)
        };
    }

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

    protected virtual Task FinalizeSeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    protected static BenchmarkObservableResultVector BackfillObservableResult(
        string outcome,
        long rows)
    {
        var results = new BenchmarkObservableResultBuilder();
        results.Add(
            "backfill-migration",
            LowerCamel(outcome),
            version: null,
            count: rows,
            payload: """{"category":"migration"}""");
        return results.Build();
    }

    protected static BenchmarkObservableResultVector RestartObservableResult(
        string outcome,
        IReadOnlyList<DocumentEnvelope> documents)
    {
        var results = new BenchmarkObservableResultBuilder();
        results.Add(
            "client-restart-validation",
            LowerCamel(outcome),
            version: null,
            count: documents.Count,
            payload: null);
        foreach (var document in documents)
            results.AddDocument(document, "loaded");
        return results.Build();
    }

    private async Task<WorkloadExecution> PointReadBatchAsync(
        int operations,
        bool resetClient,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var observedDocuments = new List<DocumentEnvelope>(operations);
        await ObserveAsync(async () =>
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
                observedDocuments.Add(document);
            }
        }, operationLatencies);
        var results = new BenchmarkObservableResultBuilder();
        results.Add(
            "point-read-batch",
            resetClient ? "client-reset-completed" : "reused-client-completed",
            version: null,
            count: observedDocuments.Count,
            payload: null);
        foreach (var document in observedDocuments)
            results.AddDocument(document, "loaded");
        return Execution(1, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> IndexedQueriesAsync(
        int operations,
        bool includeOrdering,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var result = await ObserveAsync(
                () => QueriesA.QueryAsync(Query(take: 20, includeOrdering: includeOrdering), cancellationToken),
                operationLatencies);
            if (result.TotalCount <= 0)
                throw new InvalidOperationException("Indexed query workload returned no seeded documents.");
            AddQueryResults(
                results,
                $"query-{index:D4}",
                result,
                ordered: includeOrdering);
        }
        return Execution(operations, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> InsertsAsync(
        int operations,
        int payloadPadding,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        long payloadBytes = 0;
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var payload = Payload("open", index, "write", payloadPadding);
            payloadBytes += Encoding.UTF8.GetByteCount(payload);
            var result = await ObserveAsync(
                () => TenantA.SaveAsync(Save(NewId("insert"), payload), cancellationToken),
                operationLatencies);
            var document = RequireSavedDocument(
                result,
                DocumentStoreWriteStatus.Saved,
                "insert workload",
                payload);
            results.AddDocument(document, Status(result.Status), $"insert-{index:D4}");
        }
        return Execution(operations, payloadBytes, operations, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> UpdatesAsync(
        int operations,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.Update);
        long payloadBytes = 0;
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.Update);
            var payload = Payload("closed", index, "updated", payloadPaddingBytes);
            payloadBytes += Encoding.UTF8.GetByteCount(payload);
            var result = await ObserveAsync(
                () => TenantA.SaveAsync(Save(id, payload, expectedVersion: 1), cancellationToken),
                operationLatencies);
            var document = RequireSavedDocument(
                result,
                DocumentStoreWriteStatus.Saved,
                "update workload",
                payload);
            results.AddDocument(document, Status(result.Status), $"update-{index:D4}");
        }
        return Execution(operations, payloadBytes, operations, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> DeletesAsync(
        int operations,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.Delete);
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.Delete);
            var result = await ObserveAsync(
                () => TenantA.DeleteAsync(
                    new DeleteDocumentRequest(BenchmarkModelFactory.DocumentKind, id, ExpectedVersion: 1),
                    cancellationToken),
                operationLatencies);
            RequireStatus(
                result,
                DocumentStoreWriteStatus.Deleted,
                "delete workload");
            if (!string.Equals(result.AuthoritativeId, id, StringComparison.Ordinal))
                throw new InvalidOperationException("Delete workload returned a different authoritative identity.");
            results.Add(
                $"delete-{index:D4}",
                Status(result.Status),
                version: null,
                count: 1,
                payload: null);
        }
        return Execution(
            operations,
            logicalMutations: operations,
            observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> UnitOfWorkAsync(
        int operations,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        long payloadBytes = 0;
        var results = new BenchmarkObservableResultBuilder();
        await ObserveAsync(async () =>
        {
            await using var unitOfWork = await TenantA.BeginAsync(
                DocumentCommitScope.Of(BenchmarkModelFactory.DocumentKind),
                cancellationToken);
            for (var index = 0; index < operations; index++)
            {
                var payload = Payload("open", index, "uow", payloadPaddingBytes);
                payloadBytes += Encoding.UTF8.GetByteCount(payload);
                var result = await unitOfWork.SaveAsync(
                    Save(NewId("uow"), payload),
                    cancellationToken);
                var document = RequireSavedDocument(
                    result,
                    DocumentStoreWriteStatus.Saved,
                    "unit-of-work workload",
                    payload);
                results.AddDocument(document, Status(result.Status), $"unit-of-work-{index:D4}");
            }
            await unitOfWork.CommitAsync(cancellationToken);
        }, operationLatencies);
        return Execution(1, payloadBytes, operations, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> ConflictsAsync(
        int operations,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var queue = RequiredQueue(BenchmarkWorkload.OptimisticConcurrency);
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var id = Dequeue(queue, BenchmarkWorkload.OptimisticConcurrency);
            var result = await ObserveAsync(
                () => TenantA.SaveAsync(
                    Save(id, Payload("closed", index, "stale", payloadPaddingBytes), expectedVersion: 0),
                    cancellationToken),
                operationLatencies);
            RequireStatus(
                result,
                DocumentStoreWriteStatus.ConcurrencyConflict,
                "optimistic-concurrency workload");
            if (result.Document is not null)
                throw new InvalidOperationException("Concurrency-conflict workload returned a document as if it were saved.");
            results.Add(
                $"optimistic-concurrency-{index:D4}",
                Status(result.Status),
                version: null,
                count: 1,
                payload: null);
        }
        return Execution(operations, observableResultVector: results.Build());
    }

    private async Task<WorkloadExecution> ConcurrentCreatesAsync(
        int batches,
        int concurrency,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        long payloadBytes = 0;
        var observed = new BenchmarkObservableResultBuilder();
        for (var batch = 0; batch < batches; batch++)
        {
            var id = NewId("concurrent");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, concurrency).Select(async index =>
            {
                await start.Task.WaitAsync(cancellationToken);
                var payload = Payload("open", index, "concurrent", payloadPaddingBytes);
                var timestamp = Stopwatch.GetTimestamp();
                var result = await TenantA.SaveAsync(Save(id, payload), cancellationToken);
                return (Attempt: index, Result: result, Payload: payload, Latency: ElapsedNanoseconds(timestamp));
            }).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);
            if (results.Count(result => result.Result.Status == DocumentStoreWriteStatus.Saved) != 1 ||
                results.Count(result => result.Result.Status == DocumentStoreWriteStatus.ConcurrencyConflict) != concurrency - 1)
            {
                throw new InvalidOperationException("Concurrent-create workload did not converge to one successful create.");
            }
            var winner = results.Single(result => result.Result.Status == DocumentStoreWriteStatus.Saved);
            var winnerDocument = RequireSavedDocument(
                winner.Result,
                DocumentStoreWriteStatus.Saved,
                "concurrent-create winner",
                winner.Payload);
            payloadBytes += results.Sum(result => Encoding.UTF8.GetByteCount(result.Payload));
            foreach (var result in results)
                operationLatencies.Add(result.Latency);
            observed.Add(
                $"concurrent-create-{batch:D4}",
                $"saved:1;concurrency-conflict:{concurrency - 1}",
                winnerDocument.Version,
                concurrency,
                NormalizeConcurrentPayload(winnerDocument.ContentJson, concurrency));
        }
        return Execution(
            batches * concurrency,
            payloadBytes,
            batches,
            observableResultVector: observed.Build());
    }

    private async Task<WorkloadExecution> PaginationAndCountAsync(
        int operations,
        int iteration,
        ICollection<long> operationLatencies,
        CancellationToken cancellationToken)
    {
        var results = new BenchmarkObservableResultBuilder();
        for (var index = 0; index < operations; index++)
        {
            var skip = (iteration + index) % Math.Max(1, seededCount / 4);
            var query = Query(skip, take: 20);
            var page = await ObserveAsync(
                () => QueriesA.QueryAsync(query, cancellationToken),
                operationLatencies);
            var count = await ObserveAsync(
                () => QueriesA.CountAsync(query.Select(BoundedQueryResultOperation.Count), cancellationToken),
                operationLatencies);
            if (page.TotalCount != count)
                throw new InvalidOperationException("Pagination/count workload observed inconsistent totals.");
            AddQueryResults(results, $"page-{index:D4}", page, ordered: true);
            results.Add(
                $"count-{index:D4}",
                "counted",
                version: null,
                count: count,
                payload: null);
        }
        return Execution(operations * 2, observableResultVector: results.Build());
    }

    private static int ReadRank(string json) => JsonDocument.Parse(json).RootElement.GetProperty("rank").GetInt32();

    private static void AddQueryResults(
        BenchmarkObservableResultBuilder results,
        string identity,
        DocumentQueryResult query,
        bool ordered)
    {
        results.Add(
            identity,
            "returned",
            version: null,
            count: query.TotalCount,
            payload: JsonSerializer.Serialize(
                new { returnedCount = query.Documents.Count },
                JsonOptions));

        if (!ordered)
        {
            for (var index = 0; index < query.Documents.Count; index++)
            {
                var document = query.Documents[index];
                results.Add(
                    $"{identity}/match-{index:D4}",
                    "selected",
                    document.Version,
                    count: 1,
                    payload: NormalizeUnorderedQueryPayload(document.ContentJson));
            }
            return;
        }

        var documents = query.Documents
            .Select(document => (Document: document, Rank: ReadRank(document.ContentJson)))
            .ToArray();
        if (!documents.Select(item => item.Rank).SequenceEqual(
                documents.Select(item => item.Rank).OrderDescending()))
        {
            throw new InvalidOperationException("Ordered query workload returned documents out of rank order.");
        }
        foreach (var item in documents
                     .OrderByDescending(item => item.Rank)
                     .ThenBy(item => item.Document.Id, StringComparer.Ordinal))
        {
            results.AddDocument(item.Document, "selected");
        }
    }

    private static string NormalizeUnorderedQueryPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString();
        if (!string.Equals(status, "open", StringComparison.Ordinal))
            throw new InvalidOperationException("Indexed query returned a document outside the open-status predicate.");
        var padding = root.GetProperty("padding");
        return JsonSerializer.Serialize(
            new
            {
                status,
                paddingBytes = padding.ValueKind == JsonValueKind.Null
                    ? 0
                    : padding.GetString()!.Length
            },
            JsonOptions);
    }

    private static string NormalizeConcurrentPayload(string payload, int concurrency)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString();
        var rank = root.GetProperty("rank").GetInt32();
        var category = root.GetProperty("category").GetString();
        var padding = root.GetProperty("padding");
        if (!string.Equals(status, "open", StringComparison.Ordinal) ||
            !string.Equals(category, "concurrent", StringComparison.Ordinal) ||
            rank < 0 ||
            rank >= concurrency)
        {
            throw new InvalidOperationException(
                "Concurrent-create winner payload was not one of the competing canonical payloads.");
        }
        return JsonSerializer.Serialize(
            new
            {
                status,
                category,
                winnerRankRange = $"0..{concurrency - 1}",
                paddingBytes = padding.ValueKind == JsonValueKind.Null
                    ? 0
                    : padding.GetString()!.Length
            },
            JsonOptions);
    }

    private static DocumentEnvelope RequireSavedDocument(
        DocumentStoreWriteResult result,
        DocumentStoreWriteStatus expected,
        string operation,
        string expectedPayload)
    {
        RequireStatus(result, expected, operation);
        var document = result.Document ??
                       throw new InvalidOperationException(
                           $"{operation} returned no saved document outcome.");
        if (!string.Equals(document.ContentJson, expectedPayload, StringComparison.Ordinal))
            throw new InvalidOperationException($"{operation} returned a different payload than it persisted.");
        return document;
    }

    private static string Status(DocumentStoreWriteStatus status)
        => LowerCamel(status.ToString());

    private static string LowerCamel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return $"{char.ToLowerInvariant(value[0])}{value[1..]}";
    }

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
        IReadOnlyDictionary<string, long>? providerWork = null,
        BenchmarkObservableResultVector? observableResultVector = null) =>
        new(
            operations,
            logicalPayloadBytes,
            logicalMutations,
            roundTrips,
            providerWork ?? new Dictionary<string, long>(),
            [],
            observableResultVector);

    private static async Task<T> ObserveAsync<T>(
        Func<Task<T>> operation,
        ICollection<long> operationLatencies)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var result = await operation();
        operationLatencies.Add(ElapsedNanoseconds(timestamp));
        return result;
    }

    private static async Task ObserveAsync(
        Func<Task> operation,
        ICollection<long> operationLatencies)
    {
        var timestamp = Stopwatch.GetTimestamp();
        await operation();
        operationLatencies.Add(ElapsedNanoseconds(timestamp));
    }

    private static long ElapsedNanoseconds(long timestamp) =>
        Math.Max(1, (long)Math.Round(Stopwatch.GetElapsedTime(timestamp).TotalNanoseconds));

    private sealed record BenchmarkPayload(string Status, int Rank, string Category, string? Padding);
}
