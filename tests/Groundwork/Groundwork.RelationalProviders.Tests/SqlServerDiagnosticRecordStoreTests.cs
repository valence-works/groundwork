using System.Collections.Concurrent;
using System.Xml.Linq;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.Provider.Relational;
using Groundwork.SqlServer.DiagnosticRecords;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[Collection(SqlServerDiagnosticRecordCollection.Name)]
public sealed class SqlServerDiagnosticRecordStoreTests(SqlServerDiagnosticContainer container) :
    ServerDiagnosticRecordStoreConformanceTests,
    IAsyncLifetime
{
    private string? fixtureConnectionString;
    private SqlServerDiagnosticRecordStoreFixture? fixture;

    public async Task InitializeAsync()
    {
        fixtureConnectionString = await container.CreateDatabaseAsync();
        fixture = await SqlServerDiagnosticRecordStoreFixture.CreateAsync(fixtureConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (fixtureConnectionString is not null)
            await container.DropDatabaseAsync(fixtureConnectionString);
    }

    protected override IServerDiagnosticRecordStoreConformanceFixture CreateServerFixture() =>
        fixture ?? throw new InvalidOperationException("The SQL Server diagnostic fixture has not been initialized.");

    [Fact]
    public async Task Materializer_uses_native_binary_utf8_text_and_all_durable_tables()
    {
        var fixture = (SqlServerDiagnosticRecordStoreFixture)CreateServerFixture();
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sys.tables WHERE name LIKE 'groundwork_diagnostic_%' ORDER BY name;";
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }
        await using var collation = connection.CreateCommand();
        collation.CommandText = "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'groundwork_diagnostic_fields') AND name = N'comparison_key';";

        Assert.Equal(
            [
                "groundwork_diagnostic_append_operations",
                "groundwork_diagnostic_fields",
                "groundwork_diagnostic_provider_state",
                "groundwork_diagnostic_records",
                "groundwork_diagnostic_streams",
                "groundwork_diagnostic_trim_operations"
            ],
            names);
        Assert.Equal("Latin1_General_100_BIN2_UTF8", await collation.ExecuteScalarAsync());

    }

    [Fact]
    public async Task Materializer_rejects_a_database_without_row_versioned_read_committed_isolation()
    {
        var connectionString = await container.CreateDatabaseAsync(enableReadCommittedSnapshot: false);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString));

            Assert.Contains("READ_COMMITTED_SNAPSHOT ON", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Concurrent_database_lifecycles_are_isolated_and_leave_no_databases_behind()
    {
        const int workerCount = 4;
        const int iterationsPerWorker = 4;
        var databaseNames = new ConcurrentBag<string>();
        using var stressTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            try
            {
                for (var iteration = 0; iteration < iterationsPerWorker; iteration++)
                {
                    var connectionString = await container.CreateDatabaseAsync(cancellationToken: stressTimeout.Token);
                    var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                    databaseNames.Add(databaseName);
                    try
                    {
                        await using var connection = new SqlConnection(connectionString);
                        await connection.OpenAsync(stressTimeout.Token);
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT DB_NAME(), is_read_committed_snapshot_on
                            FROM sys.databases
                            WHERE name = DB_NAME();
                            """;
                        await using var reader = await command.ExecuteReaderAsync(stressTimeout.Token);

                        Assert.True(await reader.ReadAsync(stressTimeout.Token));
                        Assert.Equal(databaseName, reader.GetString(0));
                        Assert.True(reader.GetBoolean(1));
                    }
                    finally
                    {
                        await container.DropDatabaseAsync(connectionString);
                    }
                }
            }
            catch
            {
                await stressTimeout.CancelAsync();
                throw;
            }
        }).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(workerCount * iterationsPerWorker, databaseNames.Distinct(StringComparer.Ordinal).Count());
        await using var master = new SqlConnection(container.ConnectionString);
        await master.OpenAsync(stressTimeout.Token);
        await using var leaked = master.CreateCommand();
        var parameters = databaseNames.Select((name, index) =>
        {
            var parameter = leaked.Parameters.AddWithValue($"@name{index}", name);
            return parameter.ParameterName;
        });
        leaked.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name IN ({string.Join(", ", parameters)});";

        Assert.Equal(0, Convert.ToInt32(
            await leaked.ExecuteScalarAsync(stressTimeout.Token),
            System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class SqlServerDiagnosticRecordStoreFixture : IServerDiagnosticRecordStoreConformanceFixture
{
    private readonly RelationalSessionFactory sessions;
    private readonly ManualServerTimeProvider timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> interceptors = [];
    private readonly SemaphoreSlim planSeedGate = new(1, 1);
    private bool planSeeded;
    private bool latestPlanNoiseSeeded;

    private SqlServerDiagnosticRecordStoreFixture(string connectionString)
    {
        ConnectionString = connectionString;
        sessions = RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString));
    }

    public static async Task<SqlServerDiagnosticRecordStoreFixture> CreateAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(
            connectionString,
            cancellationToken: cancellationToken);
        return new(connectionString);
    }

    public string ConnectionString { get; }

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition) =>
        new SqlServerDiagnosticRecordStore(sessions, definition, timeProvider, InterceptAsync);

    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition) =>
        new SqlServerDiagnosticRecordStore(ConnectionString, definition, timeProvider);

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
        if (query.LatestPerKeyField is not null)
            await EnsureLatestPlanNoiseAsync(cancellationToken);
        return await SqlServerDiagnosticRecordStoreFactory.ExplainQueryAsync(ConnectionString, definition, query, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePlanSeedAsync(definition, cancellationToken);
        return await SqlServerDiagnosticRecordStoreFactory.ExplainTrimAsync(ConnectionString, definition, request, cancellationToken);
    }

    public bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns)
    {
        var acceptablePaths = accessPath == "ix_groundwork_diagnostic_records_scope_cursor"
            ? new[] { accessPath, "pk_groundwork_diagnostic_records" }
            : [accessPath];
        foreach (var xml in plan)
        {
            var document = XDocument.Parse(xml);
            var ns = document.Root!.Name.Namespace;
            foreach (var operation in document.Descendants(ns + "RelOp")
                         .Where(node => node.Attribute("PhysicalOp")?.Value.Contains("Seek", StringComparison.OrdinalIgnoreCase) == true))
            {
                var index = operation.Descendants(ns + "IndexScan").Descendants(ns + "Object")
                    .FirstOrDefault(node => acceptablePaths.Any(path =>
                        node.Attribute("Index")?.Value.Contains(path, StringComparison.Ordinal) == true));
                if (index is null)
                    continue;
                var seekColumns = operation.Descendants(ns + "SeekPredicates")
                    .Descendants(ns + "ColumnReference")
                    .Select(node => node.Attribute("Column")?.Value.Trim('[', ']'))
                    .Where(name => name is not null)
                    .ToHashSet(StringComparer.Ordinal);
                var effectiveColumns = accessPath == "ix_groundwork_diagnostic_fields_scope_latest"
                    ? constrainedColumns.Where(column => column != "value_ordinal")
                    : constrainedColumns;
                if (effectiveColumns.All(seekColumns.Contains))
                    return true;
            }
        }
        return false;
    }

    public ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default) =>
        SqlServerDiagnosticRecordStoreFactory.ReadComparisonKeysAsync(ConnectionString, scope, stream, field, cancellationToken);

    public ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        SqlServerDiagnosticRecordStoreFactory.CountOperationRowsAsync(ConnectionString, kind, cancellationToken);

    public async Task MaterializeConcurrentlyAsync(DiagnosticRecordStreamDefinition definition, int count)
    {
        var stores = await Task.WhenAll(Enumerable.Range(0, count).Select(_ =>
            SqlServerDiagnosticRecordStoreFactory.CreateAsync(ConnectionString, definition)));
        Assert.Equal(count, stores.Length);
    }

    public async Task AssertPoolPressureAsync(DiagnosticRecordStreamDefinition definition)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 2 };
        SqlConnection.ClearPool(new SqlConnection(builder.ConnectionString));
        var openedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var intercepted = 0;
        var store = await SqlServerDiagnosticRecordStoreFactory.CreateAsync(
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
                var connection = new SqlConnection(builder.ConnectionString);
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
            planSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }

    private async Task EnsureLatestPlanNoiseAsync(CancellationToken cancellationToken)
    {
        if (latestPlanNoiseSeeded)
            return;
        await planSeedGate.WaitAsync(cancellationToken);
        try
        {
            if (latestPlanNoiseSeeded)
                return;
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {RelationalDiagnosticRecordSchema.FieldsTable}
                    (tenant_id, scope_id, stream_id, [cursor], field_name, value_ordinal, field_type, canonical_value, comparison_key)
                SELECT 'noise-tenant', CONCAT('noise-scope-', n % 100), 'logs', n, 'service', 0, 0, 'noise', 'noise'
                FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) a(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) b(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) c(digit)
                CROSS JOIN (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(digit)
                CROSS APPLY (VALUES (a.digit + 10 * b.digit + 100 * c.digit + 1000 * d.digit + 1)) number(n);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            latestPlanNoiseSeeded = true;
        }
        finally
        {
            planSeedGate.Release();
        }
    }
}
