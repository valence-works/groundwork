using System.Text.Json;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.Provider.Relational;
using Groundwork.PostgreSql.DiagnosticRecords;
using Npgsql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[Collection(PostgreSqlDiagnosticRecordCollection.Name)]
public sealed class PostgreSqlDiagnosticRecordStoreTests(PostgreSqlDiagnosticContainer container) :
    ServerDiagnosticRecordStoreConformanceTests,
    IAsyncLifetime
{
    private string? fixtureConnectionString;
    private PostgreSqlDiagnosticRecordStoreFixture? fixture;

    public async Task InitializeAsync()
    {
        fixtureConnectionString = await container.CreateSchemaAsync();
        fixture = await PostgreSqlDiagnosticRecordStoreFixture.CreateAsync(fixtureConnectionString, TestDefinition);
    }

    public async Task DisposeAsync()
    {
        if (fixtureConnectionString is not null)
            await container.DropSchemaAsync(fixtureConnectionString);
    }

    protected override IServerDiagnosticRecordStoreConformanceFixture CreateServerFixture() =>
        fixture ?? throw new InvalidOperationException("The PostgreSQL diagnostic fixture has not been initialized.");

    [Fact]
    public async Task Materializer_uses_native_binary_text_and_all_durable_tables()
    {
        var fixture = (PostgreSqlDiagnosticRecordStoreFixture)CreateServerFixture();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE 'groundwork_diagnostic_%' ORDER BY tablename;";
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }
        await using var collation = connection.CreateCommand();
        collation.CommandText = "SELECT a.attcollation::regcollation::text FROM pg_attribute a WHERE a.attrelid = 'groundwork_diagnostic_fields'::regclass AND a.attname = 'comparison_key';";

        Assert.Equal(
            [
                "groundwork_diagnostic_append_operations",
                "groundwork_diagnostic_definitions",
                "groundwork_diagnostic_fields",
                "groundwork_diagnostic_provider_state",
                "groundwork_diagnostic_records",
                "groundwork_diagnostic_streams",
                "groundwork_diagnostic_trim_operations"
            ],
            names);
        Assert.Contains("C", Assert.IsType<string>(await collation.ExecuteScalarAsync()), StringComparison.Ordinal);
        await using var state = connection.CreateCommand();
        state.CommandText = $"SELECT algorithm_manifest FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        state.Parameters.AddWithValue("stream", TestDefinition.Stream.Value);
        Assert.Contains(
            DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId,
            Assert.IsType<string>(await state.ExecuteScalarAsync()),
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 }));

        var direct = new PostgreSqlDiagnosticRecordStore(
            fixture.ConnectionString,
            TestDefinition with { SchemaVersion = TestDefinition.SchemaVersion + 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await direct.InspectAsync(new(new("tenant-a", "shell-a"), TestDefinition.Stream)));
    }
}

internal sealed class PostgreSqlDiagnosticRecordStoreFixture : IServerDiagnosticRecordStoreConformanceFixture
{
    private readonly RelationalSessionFactory sessions;
    private readonly ManualServerTimeProvider timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> interceptors = [];
    private readonly SemaphoreSlim planSeedGate = new(1, 1);
    private bool planSeeded;

    private PostgreSqlDiagnosticRecordStoreFixture(string connectionString)
    {
        ConnectionString = connectionString;
        sessions = RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString));
    }

    public static async Task<PostgreSqlDiagnosticRecordStoreFixture> CreateAsync(
        string connectionString,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(
            connectionString,
            definition,
            cancellationToken: cancellationToken);
        return new(connectionString);
    }

    public string ConnectionString { get; }
    public string FieldsPrimaryAccessPath => "groundwork_diagnostic_fields_pkey";

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) =>
        new PostgreSqlDiagnosticRecordStore(sessions, definition, timeProvider, InterceptAsync);

    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) =>
        new PostgreSqlDiagnosticRecordStore(ConnectionString, definition, timeProvider);

    public void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor)
    {
        lock (interceptors)
        {
            if (!interceptors.TryGetValue(point, out var queue))
                interceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    public DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();
    public void AdvanceTime(TimeSpan duration) => timeProvider.Advance(duration);
    public void SetWallClock(DateTimeOffset utcNow) => timeProvider.Set(utcNow);

    public async ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await PostgreSqlDiagnosticRecordStoreFactory.ExplainQueryAsync(ConnectionString, definition, query, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await PostgreSqlDiagnosticRecordStoreFactory.ExplainTrimAsync(ConnectionString, definition, request, cancellationToken);
    }

    public bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        foreach (var json in plan)
        {
            using var document = JsonDocument.Parse(json);
            if (FindSeek(document.RootElement, accessPath, constrainedColumns))
                return true;
        }
        return false;
    }

    public ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default) =>
        PostgreSqlDiagnosticRecordStoreFactory.ReadComparisonKeysAsync(ConnectionString, scope, stream, field, cancellationToken);

    public ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        PostgreSqlDiagnosticRecordStoreFactory.CountOperationRowsAsync(ConnectionString, kind, cancellationToken);

    public async Task MaterializeConcurrentlyAsync(DiagnosticRecordStreamDefinition definition, int count)
    {
        var stores = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            PostgreSqlDiagnosticRecordStoreFactory.CreateAsync(ConnectionString, definition)));
        Assert.Equal(count, stores.Length);
    }

    public async Task AssertPoolPressureAsync(DiagnosticRecordStreamDefinition definition)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 2 };
        NpgsqlConnection.ClearPool(new NpgsqlConnection(builder.ConnectionString));
        var openedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var intercepted = 0;
        var store = await PostgreSqlDiagnosticRecordStoreFactory.CreateAsync(
            builder.ConnectionString,
            definition,
            timeProvider,
            async (point, cancellationToken) =>
            {
                if (point == RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit &&
                    Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
                    await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            () =>
            {
                var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == System.Data.ConnectionState.Open)
                    {
                        var current = Interlocked.Increment(ref active);
                        var observed = Volatile.Read(ref maximumActive);
                        while (current > observed)
                        {
                            var prior = Interlocked.CompareExchange(ref maximumActive, current, observed);
                            if (prior == observed)
                                break;
                            observed = prior;
                        }
                        if (current == 2)
                            openedTwice.TrySetResult();
                    }
                    else if (args.OriginalState == System.Data.ConnectionState.Open)
                        Interlocked.Decrement(ref active);
                };
                return connection;
            });
        var now = GetUtcNow();
        Task<DiagnosticAppendResult> Append(int index) => store.AppendAsync(DiagnosticRecordBatch.Create(
            new("tenant-a", "shell-a"),
            definition.Stream,
            new(now, $"pool-operation-{index}"),
            [new($"pool-record-{index}", now, "{}")])).AsTask();

        var first = Append(1);
        while (Volatile.Read(ref intercepted) == 0)
            await Task.Delay(10).WaitAsync(TimeSpan.FromSeconds(10));
        var second = Append(2);
        await openedTwice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var third = Append(3);
        await Task.Delay(200);

        try
        {
            Assert.Equal(2, Volatile.Read(ref maximumActive));
            Assert.False(third.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        await Task.WhenAll(first, second, third);
    }

    private ValueTask InterceptAsync(RelationalDiagnosticRecordExecutionPoint point, CancellationToken cancellationToken)
    {
        var conformancePoint = Enum.Parse<DiagnosticExecutionPoint>(point.ToString());
        Func<CancellationToken, ValueTask>? interceptor = null;
        lock (interceptors)
        {
            if (interceptors.TryGetValue(conformancePoint, out var queue) && queue.Count > 0)
                interceptor = queue.Dequeue();
        }
        return interceptor?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    private async Task EnsurePlanSeedAsync(
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken)
    {
        if (planSeeded)
            return;
        await planSeedGate.WaitAsync(cancellationToken);
        try
        {
            if (planSeeded)
                return;
            var now = GetUtcNow();
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var store = OpenStore(definition);
            for (var batchIndex = 0; batchIndex < 4; batchIndex++)
            {
                var records = Enumerable.Range(batchIndex * 100, 100).Select(index => new DiagnosticRecordInput(
                    $"plan-record-{index}",
                    now.AddTicks(index),
                    "{}",
                    new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>
                    {
                        ["service"] = [DiagnosticFieldValue.String($"service-{index % 20}")]
                    })).ToArray();
                await store.AppendAsync(DiagnosticRecordBatch.Create(
                    scope,
                    definition.Stream,
                    new(now, $"plan-seed-{batchIndex}"),
                    records),
                    cancellationToken);
            }
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var noise = connection.CreateCommand();
            noise.CommandText = $$"""
                INSERT INTO {{RelationalDiagnosticRecordSchema.RecordsTable}}
                    (tenant_id, scope_id, stream_id, cursor, record_id, occurred_at_ticks, payload_json)
                SELECT 'tenant-a', 'noise-scope-' || (n % 100), 'logs', ((n - 1) / 100) + 1, 'noise-record-' || n, n, '{}'
                FROM generate_series(1, 10000) AS n;
                INSERT INTO {{RelationalDiagnosticRecordSchema.FieldsTable}}
                    (tenant_id, scope_id, stream_id, cursor, field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key)
                SELECT 'tenant-a', 'noise-scope-' || (n % 100), 'logs', ((n - 1) / 100) + 1, 'service', 0, 0, 'bm9pc2U=', 'noise', 'noise', repeat('0', 64), '|006E|006F|0069|0073|0065'
                FROM generate_series(1, 10000) AS n;
                INSERT INTO {{RelationalDiagnosticRecordSchema.FieldsTable}}
                    (tenant_id, scope_id, stream_id, cursor, field_name, value_ordinal, field_type, canonical_value, comparison_key, comparison_key_prefix, comparison_key_hash, search_key)
                SELECT 'tenant-a', 'shell-a', 'logs', cursor_value, 'tags', value_ordinal, 0, 'dGFn', 'tag', 'tag', repeat('1', 64), '|0074|0061|0067'
                FROM generate_series(1, 500) AS cursor_value
                CROSS JOIN generate_series(0, 7) AS value_ordinal;
                """;
            await noise.ExecuteNonQueryAsync(cancellationToken);
            planSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }

    private static bool FindSeek(
        JsonElement element,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var matchesIndex = element.TryGetProperty("Index Name", out var index) && index.GetString() == accessPath;
            var matchesNode = element.TryGetProperty("Node Type", out var node) && node.GetString()?.Contains("Index", StringComparison.Ordinal) == true;
            var condition = element.TryGetProperty("Index Cond", out var indexCondition) ? indexCondition.GetString() : null;
            var effectiveColumns = accessPath == "ix_groundwork_diagnostic_fields_scope_latest"
                ? constrainedColumns.Where(column => column != "value_ordinal")
                : constrainedColumns;
            if (matchesIndex && matchesNode && condition is not null && effectiveColumns.All(column => condition.Contains(column, StringComparison.Ordinal)))
                return true;
            foreach (var property in element.EnumerateObject())
            {
                if (FindSeek(property.Value, accessPath, constrainedColumns))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindSeek(item, accessPath, constrainedColumns))
                    return true;
            }
        }
        return false;
    }
}
