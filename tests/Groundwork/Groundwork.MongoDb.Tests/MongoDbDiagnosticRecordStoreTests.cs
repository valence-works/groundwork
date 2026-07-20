using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.Core.Manifests;
using Groundwork.MongoDb.DiagnosticRecords;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Driver.Core.Events;
using System.Collections.Concurrent;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoDbDiagnosticRecordApiCollection
{
    public const string Name = "MongoDB diagnostic record API";
}

[Collection(MongoDbDiagnosticRecordApiCollection.Name)]
public sealed class MongoDbDiagnosticRecordStoreContractTests
{
    [Fact]
    public void Factory_exposes_an_admission_gated_native_plan_inspector()
    {
        var inspector = MongoDbDiagnosticRecordStoreFactory.CreatePlanInspector(
            "mongodb://localhost:27017",
            "groundwork_contract");

        Assert.Equal("mongodb", inspector.Provider);
        Assert.IsAssignableFrom<IDiagnosticRecordPlanInspector>(inspector);
    }

    [Fact]
    public async Task Store_reports_that_atomic_diagnostics_require_transaction_capable_mongodb()
    {
        var database = new MongoClient("mongodb://localhost:27017").GetDatabase("groundwork_contract");

        var store = new MongoDbDiagnosticRecordStore(database, Definition);

        Assert.True(store.RequiresMultiDocumentTransactions);
        await DiagnosticRecordInstrumentationAssertions.AssertProviderRoutesAsync(
            store,
            "mongodb",
            new(
                request => store.AppendAsync(request),
                request => store.QueryAsync(request),
                request => store.InspectAsync(request),
                request => store.TrimAsync(request)));
    }

    private static DiagnosticRecordStreamDefinition Definition { get; } = new(
        new("logs"), 1, "diagnostic_logs", [], new(), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
}

[CollectionDefinition(Name)]
public sealed class MongoDbDiagnosticRecordCollection : ICollectionFixture<MongoDbReplicaSetFixture>
{
    public const string Name = "MongoDB diagnostic records";
}

public sealed class MongoDbReplicaSetFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("rs0")
        .WithCommand("--setParameter", "enableTestCommands=1")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public MongoClient PrimaryClient { get; private set; } = null!;
    public MongoClient SecondaryClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        PrimaryClient = new(ConnectionString);
        SecondaryClient = new(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        (PrimaryClient as IDisposable)?.Dispose();
        (SecondaryClient as IDisposable)?.Dispose();
        await _container.DisposeAsync();
    }
}

[Collection(MongoDbDiagnosticRecordCollection.Name)]
public sealed class MongoDbDiagnosticRecordStoreConformanceTests(MongoDbReplicaSetFixture replicaSet)
    : DiagnosticRecordStoreConformanceTests
{
    protected override IDiagnosticRecordStoreConformanceFixture CreateFixture() =>
        new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);

    [Fact]
    public async Task Native_cursor_explain_uses_the_scoped_cursor_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-cursor"), [new("record-1", fixture.GetUtcNow(), "{}")]));

        var explain = await store.ExplainQueryAsync(new(scope, TestDefinition.Stream, 10));

        Assert.Contains("ix_groundwork_diagnostic_records_scope_cursor", WinningIndexNames(explain));
    }

    [Fact]
    public async Task Native_long_unicode_equality_explain_uses_the_bounded_hash_field_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var value = new string('Å', 32_766) + "😀";
        Assert.Equal(65_536, System.Text.Encoding.UTF8.GetByteCount(value));
        var record = new DiagnosticRecordInput("record-1", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["unicode"] = [DiagnosticFieldValue.String(value)] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-field"), [record]));

        var explain = await store.ExplainQueryAsync(new(scope, TestDefinition.Stream, 10,
            Predicate: DiagnosticRecordPredicate.Equal("unicode", DiagnosticFieldValue.String(value.ToLowerInvariant()))));

        Assert.Contains("ix_groundwork_diagnostic_records_scope_fields", WinningIndexNames(explain));
    }

    [Fact]
    public async Task Native_contains_explain_uses_the_scope_and_stream_bounded_field_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var record = new DiagnosticRecordInput("record-1", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
            {
                ["unicode"] = [DiagnosticFieldValue.String("before-Å😀-after")]
            });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-contains"), [record]));

        var explain = await store.ExplainQueryAsync(new(
            scope,
            TestDefinition.Stream,
            10,
            Predicate: DiagnosticRecordPredicate.Contains("unicode", "å😀")));

        Assert.Contains("ix_groundwork_diagnostic_records_scope_fields", WinningIndexNames(explain));
        Assert.DoesNotContain("COLLSCAN", explain.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_long_unicode_keys_keep_only_bounded_values_in_native_indexes()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var value = new string('Å', 32_766) + "😀";
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "unicode-bounded-keys"),
            [new("record-1", fixture.GetUtcNow(), "{}", new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
            {
                ["unicode"] = [DiagnosticFieldValue.String(value)]
            })]));

        var document = await fixture.Database
            .GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.Records)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .SingleAsync();
        var queryValue = document["query_values"].AsBsonArray.Single().AsBsonDocument;
        var prefix = document[MongoDbDiagnosticRecordStore.SortPrefixPath("unicode").Split('.')[0]]
            .AsBsonDocument[MongoDbDiagnosticRecordStore.SortPrefixKey("unicode")].AsString;

        Assert.True(queryValue["comparison_key"].AsString.Length > DiagnosticStringComparisonKey.BoundedPrefixLength);
        Assert.Equal(DiagnosticStringComparisonKey.BoundedPrefixLength, queryValue["comparison_key_prefix"].AsString.Length);
        Assert.Equal(64, queryValue["comparison_key_hash"].AsString.Length);
        Assert.Equal(DiagnosticStringComparisonKey.BoundedPrefixLength, prefix.Length);
    }

    [Fact]
    public async Task Native_order_explain_uses_the_declared_field_plus_cursor_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var record = new DiagnosticRecordInput("record-1", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["sequence"] = [DiagnosticFieldValue.Int64(1)] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-order"), [record]));

        var explain = await store.ExplainQueryAsync(new(scope, TestDefinition.Stream, 10, new("sequence")));

        Assert.Contains(WinningIndexNames(explain),
            name => name.StartsWith("ix_groundwork_diagnostic_records_order_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Native_latest_explain_uses_the_mixed_key_ascending_cursor_descending_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        DiagnosticRecordInput Record(string id, string service) => new(id, fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["service"] = [DiagnosticFieldValue.String(service)] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-latest"), [Record("a-old", "a"), Record("b", "b"), Record("a-new", "a")]));

        var explain = await store.ExplainQueryAsync(new(scope, TestDefinition.Stream, 10,
            IncludeExactCount: true, LatestPerKeyField: "service"));

        Assert.Contains(WinningIndexNames(explain),
            name => name.StartsWith("ix_groundwork_diagnostic_records_latest_", StringComparison.Ordinal));
        Assert.Contains("$facet", explain.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_exact_count_explain_is_the_actual_facet_and_uses_the_field_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var record = new DiagnosticRecordInput("record-1", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["service"] = [DiagnosticFieldValue.String("api")] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-count"), [record]));

        var explain = await store.ExplainQueryAsync(new(scope, TestDefinition.Stream, 10,
            IncludeExactCount: true,
            Predicate: DiagnosticRecordPredicate.Equal("service", DiagnosticFieldValue.String("api"))));

        Assert.Contains("ix_groundwork_diagnostic_records_scope_fields", WinningIndexNames(explain));
        Assert.Contains("$facet", explain.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_trim_explain_is_the_actual_boundary_facet_and_uses_the_cursor_index()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-trim-seed"), [new("record-1", fixture.GetUtcNow(), "{}")]));
        var trim = DiagnosticTrimRequest.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "explain-trim"), 0);

        var explain = await store.ExplainTrimAsync(trim);

        Assert.Contains("ix_groundwork_diagnostic_records_scope_cursor", WinningIndexNames(explain));
        Assert.Contains("$facet", explain.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_ascii_keys_use_the_versioned_binary_comparison_value()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var record = new DiagnosticRecordInput("record-1", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["service"] = [DiagnosticFieldValue.String("API-Z9")] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "comparison-key"), [record]));

        var keys = await store.ReadComparisonKeysAsync(scope, TestDefinition.Stream, "service");

        Assert.Equal("groundwork-ascii-lower-v1", DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId);
        Assert.Equal(["api-z9"], keys);
    }

    [Fact]
    public async Task Ordinal_keys_preserve_utf16_order_for_supplementary_and_bmp_values()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        DiagnosticRecordInput Record(string id, string category) => new(id, fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["category"] = [DiagnosticFieldValue.String(category)] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "ordinal-utf16"), [Record("bmp", "\uE000"), Record("supplementary", "😀")]));

        var page = await store.QueryAsync(new(scope, TestDefinition.Stream, 10, new("category")));
        var keys = await store.ReadComparisonKeysAsync(scope, TestDefinition.Stream, "category");

        Assert.Equal("groundwork-utf16-hex-v1", DiagnosticStringComparisonKey.OrdinalAlgorithmId);
        Assert.Equal(["supplementary", "bmp"], page.Records.Select(record => record.RecordId));
        Assert.Contains("D83DDE00", keys);
        Assert.Contains("E000", keys);
    }

    [Fact]
    public async Task Independent_clients_racing_one_operation_commit_once_without_cursor_duplication()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var first = fixture.OpenStore(TestDefinition);
        var second = fixture.OpenIndependentStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var batch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "independent-client-race"), [new("record-1", fixture.GetUtcNow(), "{}")]);

        var results = await Task.WhenAll(first.AppendAsync(batch).AsTask(), second.AppendAsync(batch).AsTask());
        var page = await second.QueryAsync(new(scope, TestDefinition.Stream, 10));

        Assert.Equal(1, results.Count(result => result.Status == DiagnosticAppendStatus.Committed));
        Assert.Equal(1, results.Count(result => result.Status == DiagnosticAppendStatus.Replayed));
        Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
    }

    [Fact]
    public async Task Materializer_creates_all_durable_collections_and_native_indexes_idempotently()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        _ = fixture.OpenStore(TestDefinition);
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, TestDefinition);

        var collections = await (await fixture.Database.ListCollectionNamesAsync()).ToListAsync();
        var indexes = await (await fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>(MongoDbDiagnosticRecordNames.Records)
            .Indexes.ListAsync()).ToListAsync();

        Assert.Contains(MongoDbDiagnosticRecordNames.Records, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.Streams, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.AppendOperations, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.TrimOperations, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.ProviderState, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.AppendOutcomes, collections);
        Assert.Contains(MongoDbDiagnosticRecordNames.StreamDefinitions, collections);
        Assert.Contains(indexes, index => index["name"] == "ux_groundwork_diagnostic_records_scope_record" && index["unique"].ToBoolean());
        Assert.Contains(indexes, index => index["name"] == "ix_groundwork_diagnostic_records_scope_fields");
        var orderedFieldNames = TestDefinition.Fields
            .Where(field => field.IsOrderable || field.SupportsLatestPerKey)
            .Select(field => field.Name)
            .Append(DiagnosticRecordFieldNames.OccurredAt)
            .ToArray();
        var fullSortPaths = orderedFieldNames
            .Select(MongoDbDiagnosticRecordStore.SortPath)
            .ToHashSet(StringComparer.Ordinal);
        var prefixSortPaths = orderedFieldNames
            .Select(MongoDbDiagnosticRecordStore.SortPrefixPath)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(indexes, index =>
        {
            var keys = index["key"].AsBsonDocument.Names;
            Assert.DoesNotContain("query_values.comparison_key", keys);
            Assert.DoesNotContain("query_values.search_key", keys);
            Assert.DoesNotContain(keys, fullSortPaths.Contains);
        });
        Assert.Contains(indexes, index => index["key"].AsBsonDocument.Names.Contains("query_values.comparison_key_hash"));
        Assert.Contains(indexes, index => index["key"].AsBsonDocument.Names.Any(prefixSortPaths.Contains));
        using var collectionMetadata = await fixture.Database.ListCollectionsAsync();
        var metadata = await collectionMetadata.ToListAsync();
        Assert.All(metadata.Where(item => item["name"].AsString.StartsWith("groundwork_diagnostic_", StringComparison.Ordinal)), item =>
        {
            var options = item["options"].AsBsonDocument;
            Assert.True(!options.TryGetValue("collation", out var collation) || collation["locale"] == "simple");
        });
    }

    [Fact]
    public async Task Descending_latest_per_key_continuation_does_not_resurrect_an_older_record()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        DiagnosticRecordInput Record(string id, string service) => new(id, fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["service"] = [DiagnosticFieldValue.String(service)] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "latest-page-seed"), [Record("a-old", "A"), Record("b", "B"), Record("a-new", "A")]));
        var query = new DiagnosticRecordQuery(scope, TestDefinition.Stream, 1,
            DiagnosticRecordOrder.CursorDescending, LatestPerKeyField: "service");

        var first = await store.QueryAsync(query);
        var second = await store.QueryAsync(query with { Continuation = first.Continuation });

        Assert.Equal("a-new", Assert.Single(first.Records).RecordId);
        Assert.Equal("b", Assert.Single(second.Records).RecordId);
        Assert.Null(second.Continuation);
    }

    [Fact]
    public async Task Empty_field_dictionary_remains_distinct_from_absent_fields_across_replay()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var batch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "empty-fields"),
            [new("record-1", fixture.GetUtcNow(), "{}", new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>())]);

        var committed = await store.AppendAsync(batch);
        var replayed = await store.AppendAsync(batch);

        Assert.NotNull(Assert.Single(committed.Records).Fields);
        Assert.Empty(Assert.Single(replayed.Records).Fields!);
        Assert.Equal(committed.Records, replayed.Records);
    }

    [Fact]
    public async Task Tombstone_cleanup_is_durable_and_bounded_per_committed_operation()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        for (var index = 0; index < 40; index++)
            await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
                new(fixture.GetUtcNow(), $"cleanup-{index}"), [new($"record-{index}", fixture.GetUtcNow(), "{}")]));
        fixture.AdvanceTime(TestDefinition.AppendIdempotencyWindow + TestDefinition.MaxOperationClockSkew * 2 + TimeSpan.FromTicks(1));

        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "cleanup-trigger"), [new("record-trigger", fixture.GetUtcNow(), "{}")]));

        Assert.Equal(9, await store.CountOperationRowsAsync(scope, TestDefinition.Stream, DiagnosticOperationKind.Append));
    }

    [Fact]
    public async Task Expired_append_outcome_becomes_a_minimal_tombstone_before_admission_horizon()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var operation = new DiagnosticOperationId(fixture.GetUtcNow() + TestDefinition.MaxOperationClockSkew, "minimal-tombstone");
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream, operation,
            [new("record-old", fixture.GetUtcNow(), "{}")]));
        fixture.AdvanceTime(TestDefinition.AppendIdempotencyWindow + TimeSpan.FromTicks(1));

        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "cleanup-trigger-minimal"), [new("record-new", fixture.GetUtcNow(), "{}")]));

        var tombstone = await fixture.Database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.AppendOperations)
            .Find(Builders<BsonDocument>.Filter.Eq("has_outcome", false)).SingleAsync();
        var outcomeCount = await fixture.Database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.AppendOutcomes)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty);
        Assert.DoesNotContain("fingerprint", tombstone.Names);
        Assert.DoesNotContain("cursor_high_water", tombstone.Names);
        Assert.Equal(1, outcomeCount);
    }

    [Fact]
    public async Task Factory_exposes_a_working_store_for_a_replica_set()
    {
        await using var handle = await MongoDbDiagnosticRecordStoreFactory.CreateAsync(
            replicaSet.ConnectionString,
            $"groundwork_factory_{Guid.NewGuid():N}",
            TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");

        var result = await handle.Store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(TimeProvider.System.GetUtcNow(), "factory-append"), [new("record-1", TimeProvider.System.GetUtcNow(), "{}")]));

        Assert.Equal(DiagnosticAppendStatus.Committed, result.Status);
    }

    [Fact]
    public async Task Latest_per_key_excludes_missing_keys_from_page_and_exact_count()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var keyed = new DiagnosticRecordInput("keyed", fixture.GetUtcNow(), "{}",
            new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>> { ["service"] = [DiagnosticFieldValue.String("api")] });
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "latest-missing"), [new("missing", fixture.GetUtcNow(), "{}"), keyed]));

        var page = await store.QueryAsync(new(scope, TestDefinition.Stream, 10,
            IncludeExactCount: true, LatestPerKeyField: "service"));

        Assert.Equal("keyed", Assert.Single(page.Records).RecordId);
        Assert.Equal(1, page.ExactCount);
    }

    [Fact]
    public async Task Append_replay_is_chunked_below_mongodb_document_limit_for_a_legal_large_batch()
    {
        var definition = TestDefinition with
        {
            Fields = [],
            Limits = TestDefinition.Limits with { MaxBatchRecords = 20, MaxPayloadBytes = 1_048_576 },
            LogicalHighWaterField = null
        };
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(definition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var payload = $"\"{new string('a', definition.Limits.MaxPayloadBytes - 2)}\"";
        var records = Enumerable.Range(0, 17)
            .Select(index => new DiagnosticRecordInput($"record-{index}", fixture.GetUtcNow(), payload))
            .ToArray();
        var batch = DiagnosticRecordBatch.Create(scope, definition.Stream, new(fixture.GetUtcNow(), "large-batch"), records);

        var committed = await store.AppendAsync(batch);
        var replayed = await store.AppendAsync(batch);
        var ledger = await fixture.Database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.AppendOperations)
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();

        Assert.Equal(17, committed.Records.Count);
        Assert.Equal(committed.Records, replayed.Records);
        Assert.True(ledger.ToBson().Length < 1_000_000, $"Ledger size was {ledger.ToBson().Length} bytes.");
    }

    [Fact]
    public async Task Invalid_definition_is_rejected_before_creating_any_collection()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        var invalid = TestDefinition with { SchemaVersion = 0 };

        await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, invalid));

        Assert.Empty(await (await fixture.Database.ListCollectionNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task Provider_string_and_document_bounds_are_rejected_before_mongodb_io()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        var oversized = TestDefinition with
        {
            LogicalHighWaterField = null,
            Fields =
            [
                new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Contains },
                    CasePolicy: DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
                    MaxStringBytes: 65_537)
            ]
        };

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, oversized));

        Assert.Contains(exception.Errors, error => error.Code == "provider.mongodb.string_bound.too_large");
        Assert.Empty(await (await fixture.Database.ListCollectionNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task Aggregate_record_document_budget_is_rejected_before_mongodb_io()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        DiagnosticFieldDefinition Field(string name) => new(
            name,
            DiagnosticFieldType.String,
            DiagnosticFieldCardinality.Scalar,
            new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal, DiagnosticPredicateOperator.Contains },
            CasePolicy: DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
            MaxStringBytes: 65_536);
        var oversized = TestDefinition with
        {
            LogicalHighWaterField = null,
            Fields = Enumerable.Range(0, 10).Select(index => Field($"message-{index}")).ToArray(),
            Limits = TestDefinition.Limits with
            {
                MaxPayloadBytes = 1_000_000,
                MaxFieldsPerRecord = 10,
                MaxPredicateValues = 1
            }
        };

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, oversized));

        Assert.Contains(exception.Errors, error => error.Code == "provider.mongodb.record_document_budget.exceeded");
        Assert.Empty(await (await fixture.Database.ListCollectionNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task Incompatible_stream_definition_is_rejected_on_reopen()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, TestDefinition);
        var incompatible = TestDefinition with { SchemaVersion = 2 };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, incompatible));

        Assert.Contains("definition", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_non_simple_collection_collation_is_rejected()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);
        await fixture.Database.CreateCollectionAsync(
            MongoDbDiagnosticRecordNames.Records,
            new CreateCollectionOptions { Collation = new Collation("en", strength: CollationStrength.Primary) });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, TestDefinition));

        Assert.Contains("simple collation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_materializers_converge_on_one_compatible_stream_definition()
    {
        var fixture = new MongoDbDiagnosticRecordStoreFixture(replicaSet.PrimaryClient, replicaSet.SecondaryClient);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            MongoDbDiagnosticRecordMaterializer.MaterializeAsync(fixture.Database, TestDefinition)));

        Assert.Equal(1, await fixture.Database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Test_hooks_and_native_evidence_are_not_public_provider_api()
    {
        var publicMethods = typeof(MongoDbDiagnosticRecordStore).GetMethods()
            .Where(method => method.DeclaringType == typeof(MongoDbDiagnosticRecordStore))
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("ExplainQueryAsync", publicMethods);
        Assert.DoesNotContain("ReadComparisonKeysAsync", publicMethods);
        Assert.DoesNotContain("CountOperationRowsAsync", publicMethods);
        Assert.Empty(typeof(MongoDbDiagnosticRecordStore).GetConstructors());
    }

    [Fact]
    public async Task Query_page_count_and_high_water_share_one_snapshot_across_concurrent_trim()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var independent = fixture.OpenIndependentStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var seed = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "snapshot-query-seed"),
            [new("record-1", fixture.GetUtcNow(), "{}"), new("record-2", fixture.GetUtcNow(), "{}"), new("record-3", fixture.GetUtcNow(), "{}")]);
        await store.AppendAsync(seed);
        fixture.InterceptProviderNext(MongoDbDiagnosticRecordExecutionPoint.QueryAfterHighWaterRead, async token =>
            await independent.TrimAsync(DiagnosticTrimRequest.Create(scope, TestDefinition.Stream,
                new(fixture.GetUtcNow(), "snapshot-query-trim"), 1), token));

        var page = await store.QueryAsync(new(scope, TestDefinition.Stream, 10, IncludeExactCount: true));
        var after = await store.InspectAsync(new(scope, TestDefinition.Stream));

        Assert.Equal(3, page.ExactCount);
        Assert.Equal(["record-1", "record-2", "record-3"], page.Records.Select(record => record.RecordId));
        Assert.Equal(1, after.RetainedCount.Value);
    }

    [Fact]
    public async Task Inspection_metadata_and_records_share_one_snapshot_across_concurrent_trim()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var independent = fixture.OpenIndependentStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "snapshot-inspect-seed"),
            [new("record-1", fixture.GetUtcNow(), "{}"), new("record-2", fixture.GetUtcNow(), "{}"), new("record-3", fixture.GetUtcNow(), "{}")]));
        fixture.InterceptProviderNext(MongoDbDiagnosticRecordExecutionPoint.InspectAfterStreamStateRead, async token =>
            await independent.TrimAsync(DiagnosticTrimRequest.Create(scope, TestDefinition.Stream,
                new(fixture.GetUtcNow(), "snapshot-inspect-trim"), 1), token));

        var snapshot = await store.InspectAsync(new(scope, TestDefinition.Stream));
        var after = await store.InspectAsync(new(scope, TestDefinition.Stream));

        Assert.Equal(3, snapshot.RetainedCount.Value);
        Assert.Equal("3", snapshot.MaxRetainedCursor!.Value.Value);
        Assert.Equal("3", snapshot.LifetimeCommittedCursorHighWater!.Value.Value);
        Assert.Equal(1, after.RetainedCount.Value);
    }

    [Fact]
    public async Task Unknown_commit_result_is_retried_without_reexecuting_append()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var batch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "unknown-commit-retry"), [new("record-1", fixture.GetUtcNow(), "{}")]);
        await ConfigureCommitFailureAsync(fixture.Database, new BsonDocument("times", 1), 91,
            ["UnknownTransactionCommitResult"]);

        var result = await store.AppendAsync(batch);
        var page = await store.QueryAsync(new(scope, TestDefinition.Stream, 10));

        Assert.Equal(DiagnosticAppendStatus.Committed, result.Status);
        Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
    }

    [Fact]
    public async Task Cancellation_after_unknown_commit_result_maps_to_acknowledgement_loss()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var batch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "unknown-commit-cancel"), [new("record-1", fixture.GetUtcNow(), "{}")]);
        using var cancellation = new CancellationTokenSource();
        fixture.InterceptProviderNext(MongoDbDiagnosticRecordExecutionPoint.CommitResultUnknown, _ =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        });
        await ConfigureCommitFailureAsync(fixture.Database, "alwaysOn", 91, ["UnknownTransactionCommitResult"]);
        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticAcknowledgementLostException>(async () =>
                await store.AppendAsync(batch, cancellation.Token));

            Assert.Equal(DiagnosticOperationKind.Append, exception.OperationKind);
            Assert.Equal(batch.OperationId, exception.OperationId);
        }
        finally
        {
            await DisableCommitFailureAsync(fixture.Database);
        }
        var retry = await store.AppendAsync(batch);
        Assert.Contains(retry.Status, new[] { DiagnosticAppendStatus.Committed, DiagnosticAppendStatus.Replayed });
    }

    [Fact]
    public async Task Terminal_commit_failure_after_unknown_result_maps_to_acknowledgement_loss()
    {
        var fixture = (MongoDbDiagnosticRecordStoreFixture)CreateFixture();
        var store = (MongoDbDiagnosticRecordStore)fixture.OpenStore(TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
        var batch = DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(fixture.GetUtcNow(), "unknown-commit-terminal"), [new("record-1", fixture.GetUtcNow(), "{}")]);
        fixture.InterceptProviderNext(MongoDbDiagnosticRecordExecutionPoint.CommitResultUnknown, async token =>
            await ConfigureCommitFailureAsync(fixture.Database, new BsonDocument("times", 1), 2, [], token));
        await ConfigureCommitFailureAsync(fixture.Database, new BsonDocument("times", 1), 91,
            ["UnknownTransactionCommitResult"]);

        var exception = await Assert.ThrowsAsync<DiagnosticAcknowledgementLostException>(async () =>
            await store.AppendAsync(batch));
        var retry = await store.AppendAsync(batch);

        Assert.Equal(DiagnosticOperationKind.Append, exception.OperationKind);
        Assert.Contains(retry.Status, new[] { DiagnosticAppendStatus.Committed, DiagnosticAppendStatus.Replayed });
    }

    private static async Task ConfigureCommitFailureAsync(
        IMongoDatabase database,
        BsonValue mode,
        int errorCode,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        var data = new BsonDocument
        {
            { "failCommands", new BsonArray { "commitTransaction" } },
            { "errorCode", errorCode }
        };
        if (labels.Count > 0)
            data["errorLabels"] = new BsonArray(labels);
        await database.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", mode },
            { "data", data }
        }, cancellationToken: cancellationToken);
    }

    private static async Task DisableCommitFailureAsync(IMongoDatabase database) =>
        await database.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", "off" }
        });

    [Fact]
    public async Task Provider_clock_and_transactions_pin_majority_durability_and_snapshot_reads()
    {
        var commands = new ConcurrentQueue<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString(replicaSet.ConnectionString);
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started =>
            commands.Enqueue(started.Command.DeepClone().AsBsonDocument));
        using var client = new MongoClient(settings);
        var database = client.GetDatabase($"groundwork_durability_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, TestDefinition);
        var store = new MongoDbDiagnosticRecordStore(database, TestDefinition);
        var scope = new DiagnosticStorageScope("tenant-a", "shell-a");

        await store.AppendAsync(DiagnosticRecordBatch.Create(scope, TestDefinition.Stream,
            new(TimeProvider.System.GetUtcNow(), "durability"), [new("record-1", TimeProvider.System.GetUtcNow(), "{}")]));

        Assert.Contains(commands, command => command.ElementCount > 0 && command.GetElement(0).Name == "findAndModify" &&
            command.TryGetValue("writeConcern", out var concern) && concern["w"] == "majority");
        Assert.Contains(commands, command => command.ElementCount > 0 && command.GetElement(0).Name == "commitTransaction" &&
            command.TryGetValue("writeConcern", out var concern) && concern["w"] == "majority");
        Assert.Contains(commands, command => command.GetValue("startTransaction", false).ToBoolean() &&
            command.TryGetValue("readConcern", out var concern) && concern["level"] == "snapshot");
    }

    private static IReadOnlyList<string> WinningIndexNames(MongoDB.Bson.BsonDocument explain)
    {
        var result = new List<string>();
        FindWinningPlans(explain, result);
        return result;

        static void FindWinningPlans(BsonValue value, ICollection<string> indexes)
        {
            if (value is BsonDocument document)
            {
                if (document.TryGetValue("winningPlan", out var winningPlan))
                    Visit(winningPlan, indexes);
                foreach (var element in document.Where(element => element.Name != "winningPlan"))
                    FindWinningPlans(element.Value, indexes);
            }
            else if (value is BsonArray array)
                foreach (var item in array)
                    FindWinningPlans(item, indexes);
        }

        static void Visit(MongoDB.Bson.BsonValue value, ICollection<string> indexes)
        {
            if (value is MongoDB.Bson.BsonDocument document)
            {
                if (document.TryGetValue("indexName", out var indexName))
                    indexes.Add(indexName.AsString);
                foreach (var element in document)
                    Visit(element.Value, indexes);
            }
            else if (value is MongoDB.Bson.BsonArray array)
                foreach (var item in array)
                    Visit(item, indexes);
        }
    }
}

public sealed class MongoDbDiagnosticRecordStandaloneTests : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7.0.24").Build();

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Session_factory_rejects_standalone_deployments_before_exposing_a_non_atomic_store_or_mutating_schema()
    {
        var databaseName = $"groundwork_{Guid.NewGuid():N}";
        var definition = new DiagnosticRecordStreamDefinition(
            new("logs"), 1, "logs", [], new(), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        var deployment = new DiagnosticRecordDeploymentManifest(
            new StorageManifest(new("diagnostic-runtime-admission-tests"), new("tests"), new("1"), [], new HashSet<string>(), []),
            [definition]);
        var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(_container.GetConnectionString(), databaseName);

        var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
            await factory.OpenAsync(deployment, new("tenant-a", "shell-a")));

        Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed, exception.Code);
        var topology = Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Contains("require multi-document transactions", topology.Message, StringComparison.Ordinal);
        Assert.Contains("Standalone", topology.Message, StringComparison.Ordinal);
        using var client = new MongoClient(_container.GetConnectionString());
        Assert.DoesNotContain(databaseName, await (await client.ListDatabaseNamesAsync()).ToListAsync());
    }
}

[Collection(MongoDbDiagnosticRecordCollection.Name)]
public sealed class MongoDbDiagnosticRecordRuntimeAdmissionTests(MongoDbReplicaSetFixture replicaSet)
{
    [Fact]
    public async Task Session_factory_rejects_a_missing_database_without_creating_collections()
    {
        var databaseName = $"groundwork_runtime_admission_{Guid.NewGuid():N}";
        var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(replicaSet.ConnectionString, databaseName);

        var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
            await factory.OpenAsync(Deployment(Definition), new("tenant-a", "shell-a")));

        Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
        Assert.DoesNotContain(databaseName, await (await replicaSet.PrimaryClient.ListDatabaseNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task Session_factory_rejects_definition_drift_without_repairing_the_persisted_definition()
    {
        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);
        var changed = Definition with { SchemaVersion = Definition.SchemaVersion + 1 };
        var before = await ReadDefinitionFingerprintAsync(database, Definition.Stream);

        try
        {
            var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(replicaSet.ConnectionString, database.DatabaseNamespace.DatabaseName);
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await factory.OpenAsync(Deployment(changed), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            Assert.Equal(before, await ReadDefinitionFingerprintAsync(database, Definition.Stream));
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_algorithm_state_drift_without_repairing_it()
    {
        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);
        var definitions = database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions);
        await definitions.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Empty,
            Builders<BsonDocument>.Update.Set("algorithm_manifest_fingerprint", "drifted"));

        try
        {
            var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(
                replicaSet.ConnectionString,
                database.DatabaseNamespace.DatabaseName);
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await factory.OpenAsync(Deployment(Definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            Assert.Equal(
                "drifted",
                (await definitions.Find(Builders<BsonDocument>.Filter.Empty).SingleAsync())["algorithm_manifest_fingerprint"].AsString);
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_an_incompatible_existing_index_without_repairing_it()
    {
        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);
        const string indexName = "ix_groundwork_diagnostic_records_scope_cursor";
        var records = database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.Records);
        await records.Indexes.DropOneAsync(indexName);
        await records.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("payload_json", 1),
            new CreateIndexOptions { Name = indexName }));

        try
        {
            var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(
                replicaSet.ConnectionString,
                database.DatabaseNamespace.DatabaseName);
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await factory.OpenAsync(Deployment(Definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            using var indexes = await records.Indexes.ListAsync();
            var actual = (await indexes.ToListAsync()).Single(index => index["name"].AsString == indexName);
            Assert.Equal(new BsonDocument("payload_json", 1), actual["key"].AsBsonDocument);
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task First_store_open_reinspects_and_does_not_recreate_schema_removed_after_session_admission()
    {
        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);
        var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(
            replicaSet.ConnectionString,
            database.DatabaseNamespace.DatabaseName);
        await using var session = await factory.OpenAsync(Deployment(Definition), new("tenant-a", "shell-a"));
        await database.DropCollectionAsync(MongoDbDiagnosticRecordNames.Records);

        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await session.OpenStoreAsync(Definition.Stream));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            using var collections = await database.ListCollectionNamesAsync();
            Assert.DoesNotContain(MongoDbDiagnosticRecordNames.Records, await collections.ToListAsync());
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Session_factory_opens_a_ready_deployment()
    {
        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);

        try
        {
            var factory = MongoDbDiagnosticRecordStoreFactory.CreateSessionFactory(replicaSet.ConnectionString, database.DatabaseNamespace.DatabaseName);
            await using var session = await factory.OpenAsync(Deployment(Definition), new("tenant-a", "shell-a"));

            Assert.Equal(new DiagnosticStorageScope("tenant-a", "shell-a"), session.Scope);
            Assert.NotNull(await session.OpenStoreAsync(Definition.Stream));
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Plan_inspector_rejects_a_missing_database_without_creating_collections_and_returns_native_query_and_trim_plans_when_ready()
    {
        var missingDatabaseName = $"groundwork_runtime_admission_{Guid.NewGuid():N}";
        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(() =>
                MongoDbDiagnosticRecordStoreFactory.CreatePlanInspector(replicaSet.ConnectionString, missingDatabaseName)
                    .InspectQueryAsync(Deployment(Definition), new(new("tenant-a", "shell-a"), Definition.Stream, 10)).AsTask());

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            Assert.DoesNotContain(missingDatabaseName, await (await replicaSet.PrimaryClient.ListDatabaseNamesAsync()).ToListAsync());
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(missingDatabaseName);
        }

        var database = replicaSet.PrimaryClient.GetDatabase($"groundwork_runtime_admission_{Guid.NewGuid():N}");
        try
        {
            await MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, Definition);
            var inspector = MongoDbDiagnosticRecordStoreFactory.CreatePlanInspector(
                replicaSet.ConnectionString,
                database.DatabaseNamespace.DatabaseName);
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var query = await inspector.InspectQueryAsync(Deployment(Definition), new(scope, Definition.Stream, 10));
            var trim = await inspector.InspectTrimSelectionAsync(Deployment(Definition),
                DiagnosticTrimRequest.Create(scope, Definition.Stream, new(DateTimeOffset.UtcNow, "plan-trim"), 10));

            Assert.Equal(DiagnosticRecordNativePlanFormats.MongoDbExplainJson, query.Format);
            Assert.NotEmpty(query.RawPlans);
            Assert.Equal(DiagnosticRecordNativePlanFormats.MongoDbExplainJson, trim.Format);
            Assert.NotEmpty(trim.RawPlans);
        }
        finally
        {
            await replicaSet.PrimaryClient.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    private static readonly DiagnosticRecordStreamDefinition Definition = new(
        new("logs"), 1, "diagnostic_logs", [], new(), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    private static DiagnosticRecordDeploymentManifest Deployment(DiagnosticRecordStreamDefinition definition) => new(
        new StorageManifest(
            new("diagnostic-runtime-admission-tests"),
            new("tests"),
            new("1"),
            [],
            new HashSet<string>(),
            []),
        [definition]);

    private static async Task<string?> ReadDefinitionFingerprintAsync(IMongoDatabase database, DiagnosticStreamId stream)
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stream.Value)));
        var document = await database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .SingleAsync();
        return document["fingerprint"].AsString;
    }
}

internal sealed class MongoDbDiagnosticRecordStoreFixture : IDiagnosticRecordStoreConformanceFixture
{
    private readonly string _databaseName = $"groundwork_diagnostics_{Guid.NewGuid():N}";
    private readonly MongoClient _client;
    private readonly MongoClient _independentClient;
    private readonly ManualMongoTimeProvider _timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> _interceptors = [];
    private readonly Dictionary<MongoDbDiagnosticRecordExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> _providerInterceptors = [];
    private bool _materialized;

    public MongoDbDiagnosticRecordStoreFixture(MongoClient client, MongoClient independentClient)
    {
        _client = client;
        _independentClient = independentClient;
    }

    internal IMongoDatabase Database => _client.GetDatabase(_databaseName);

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) => Open(_client, definition);

    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) =>
        Open(_independentClient, definition);

    public void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor)
    {
        lock (_interceptors)
        {
            if (!_interceptors.TryGetValue(point, out var queue))
                _interceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    internal void InterceptProviderNext(
        MongoDbDiagnosticRecordExecutionPoint point,
        Func<CancellationToken, ValueTask> interceptor)
    {
        lock (_providerInterceptors)
        {
            if (!_providerInterceptors.TryGetValue(point, out var queue))
                _providerInterceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
    public void AdvanceTime(TimeSpan duration) => _timeProvider.Advance(duration);
    public void SetWallClock(DateTimeOffset utcNow) => _timeProvider.Set(utcNow);

    private MongoDbDiagnosticRecordStore Open(MongoClient client, DiagnosticRecordStreamDefinition definition)
    {
        var database = client.GetDatabase(_databaseName);
        lock (this)
        {
            if (!_materialized)
            {
                MongoDbDiagnosticRecordMaterializer.MaterializeAsync(database, definition).GetAwaiter().GetResult();
                _materialized = true;
            }
        }
        return new(database, definition, _timeProvider, InterceptAsync);
    }

    private ValueTask InterceptAsync(MongoDbDiagnosticRecordExecutionPoint point, CancellationToken cancellationToken)
    {
        var contractPoint = point switch
        {
            MongoDbDiagnosticRecordExecutionPoint.AppendBeforeCommit => DiagnosticExecutionPoint.AppendBeforeCommit,
            MongoDbDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit => DiagnosticExecutionPoint.AppendAfterRecordStagedBeforeCommit,
            MongoDbDiagnosticRecordExecutionPoint.AppendAfterCommitBeforeAcknowledgement => DiagnosticExecutionPoint.AppendAfterCommitBeforeAcknowledgement,
            MongoDbDiagnosticRecordExecutionPoint.TrimBeforeCommit => DiagnosticExecutionPoint.TrimBeforeCommit,
            MongoDbDiagnosticRecordExecutionPoint.TrimAfterRecordDeletedBeforeCommit => DiagnosticExecutionPoint.TrimAfterRecordDeletedBeforeCommit,
            MongoDbDiagnosticRecordExecutionPoint.TrimAfterCommitBeforeAcknowledgement => DiagnosticExecutionPoint.TrimAfterCommitBeforeAcknowledgement,
            _ => (DiagnosticExecutionPoint?)null
        };
        Func<CancellationToken, ValueTask>? interceptor = null;
        if (contractPoint is { } contract)
            lock (_interceptors)
                if (_interceptors.TryGetValue(contract, out var queue) && queue.Count > 0)
                    interceptor = queue.Dequeue();
        lock (_providerInterceptors)
            if (interceptor is null && _providerInterceptors.TryGetValue(point, out var queue) && queue.Count > 0)
                interceptor = queue.Dequeue();
        return interceptor?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }
}

internal sealed class ManualMongoTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public void Advance(TimeSpan duration)
    {
        lock (_gate)
            _utcNow += duration;
    }

    public void Set(DateTimeOffset value)
    {
        lock (_gate)
            _utcNow = value;
    }
}
