using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class SqliteBenchmarkTarget(
    PhysicalStorageForm storageForm,
    string instance,
    string scratchDirectory,
    int migrationDatasetSize) : PhysicalStorageBenchmarkTarget(BenchmarkProvider.Sqlite, storageForm, instance)
{
    private readonly string databasePath = Path.Combine(scratchDirectory, $"sqlite-{instance}-{storageForm}.db");
    private readonly int migrationDatasetSize = migrationDatasetSize;
    private BenchmarkPhysicalModel model = null!;
    private MigrationState? migration;

    public override string ProviderVersion { get; protected set; } = "unknown";

    public override IReadOnlyDictionary<string, string> ProviderConfiguration => new Dictionary<string, string>
    {
        ["mode"] = "ReadWriteCreate",
        ["cache"] = "Shared",
        ["journal_mode"] = "WAL",
        ["synchronous"] = "provider default per operation connection",
        ["connection_lifetime"] = "per-operation serialized production sessions"
    };

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ConnectionString;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.Delete(databasePath);
        model = BenchmarkModelFactory.CompileRelational(
            StorageForm,
            Instance,
            SqliteGroundworkCapabilities.Provider,
            ProviderPhysicalNameNormalizer.Identity);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureAsync(connection, cancellationToken);
            await PhysicalSchemaApplication.ApplyAsync(
                model.Target,
                new SqlitePhysicalSchemaExecutor(connection),
                cancellationToken: cancellationToken);
            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT sqlite_version();";
            ProviderVersion = Convert.ToString(await version.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
        }
        OpenStores();
    }

    public override async Task<NativePlanEvidence> RunNativePlanGateAsync(CancellationToken cancellationToken)
    {
        var store = CreateStore(DocumentStoreAccess.Scoped(new("tenant-a")));
        var query = Query(take: 20);
        var rendered = RelationalPhysicalQueryRuntime.BuildQueryCommand(
            store,
            model.Manifest,
            model.Route,
            model.Target.Provider,
            "sqlite",
            query,
            new HashSet<Groundwork.Core.Indexing.IndexValueKind>
            {
                Groundwork.Core.Indexing.IndexValueKind.String,
                Groundwork.Core.Indexing.IndexValueKind.Keyword,
                Groundwork.Core.Indexing.IndexValueKind.Boolean
            });
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var analyze = connection.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            await analyze.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {rendered.CommandText}";
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            lines.Add(reader.GetString(3));
        var plan = string.Join(Environment.NewLine, lines);
        var indexName = model.Route.Indexes.Single().Name.Identifier;
        if (!plan.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
            !plan.Contains("SEARCH", StringComparison.OrdinalIgnoreCase) ||
            plan.Contains("SCAN ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite native-plan gate rejected the scale-bearing query. Expected index '{indexName}'.{Environment.NewLine}{plan}");
        }
        return new NativePlanEvidence(
            Provider.ToString(),
            StorageForm.ToString(),
            BenchmarkModelFactory.QueryIdentity,
            (model.Route.LinkedIndexStorage ?? model.Route.PrimaryStorage).Name.Identifier,
            indexName,
            plan,
            [
                "indexed SEARCH is present",
                $"index {indexName} is selected",
                "full SCAN is absent",
                plan.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)
                    ? "ordering remains server-side with a temporary B-tree for the stable identity suffix"
                    : "ordering is satisfied directly by the selected index"
            ]);
    }

    public override async Task PrepareIterationAsync(
        BenchmarkWorkload workload,
        int iteration,
        CancellationToken cancellationToken)
    {
        if (workload != BenchmarkWorkload.BackfillMigration)
            return;
        var suffix = $"migration_{iteration}_{Guid.NewGuid():N}"[..32];
        var initial = BenchmarkModelFactory.CompileRelational(
            StorageForm,
            suffix,
            SqliteGroundworkCapabilities.Provider,
            ProviderPhysicalNameNormalizer.Identity,
            includeCategory: false);
        var additive = BenchmarkModelFactory.CompileRelational(
            StorageForm,
            suffix,
            SqliteGroundworkCapabilities.Provider,
            ProviderPhysicalNameNormalizer.Identity,
            includeCategory: true);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlitePhysicalSchemaExecutor(connection), cancellationToken: cancellationToken);
        var store = new SqlitePhysicalDocumentStore(
            ConnectionString,
            initial.Manifest,
            initial.Target.Routes,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        for (var index = 0; index < migrationDatasetSize; index++)
        {
            RequireStatus(await store.SaveAsync(
                    Save($"migration-{index:D8}", Payload("open", index, "migration")),
                    cancellationToken),
                DocumentStoreWriteStatus.Saved,
                "migration seed");
        }
        migration = new MigrationState(additive, migrationDatasetSize);
    }

    public override async Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var pageCount = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
        var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
        var primaryRows = await CountAsync(connection, model.Route.PrimaryStorage.Name.Identifier, cancellationToken);
        var linkedRows = model.Route.LinkedIndexStorage is null
            ? 0
            : await CountAsync(connection, model.Route.LinkedIndexStorage.Name.Identifier, cancellationToken);
        long indexBytes = 0;
        var indexBytesObservable = 1L;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name = @name;";
            command.Parameters.AddWithValue("@name", model.Route.Indexes.Single().Name.Identifier);
            indexBytes = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqliteException)
        {
            indexBytesObservable = 0;
        }
        return new StorageSnapshot(
            pageCount * pageSize,
            indexBytes,
            primaryRows,
            linkedRows,
            new Dictionary<string, long>
            {
                ["page_count"] = pageCount,
                ["page_size"] = pageSize,
                ["index_bytes_observable"] = indexBytesObservable
            });
    }

    public override ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(databasePath);
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");
        return ValueTask.CompletedTask;
    }

    protected override Task ResetClientStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteConnection.ClearAllPools();
        OpenStores();
        return Task.CompletedTask;
    }

    protected override async Task<WorkloadExecution> ExecuteBackfillMigrationAsync(CancellationToken cancellationToken)
    {
        var state = migration ?? throw new InvalidOperationException("Migration iteration was not prepared.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var result = await PhysicalSchemaApplication.ApplyAsync(
            state.Additive.Target,
            new SqlitePhysicalSchemaExecutor(connection),
            cancellationToken: cancellationToken);
        if (result.Outcome != PhysicalSchemaApplicationOutcome.Applied)
            throw new InvalidOperationException($"Backfill migration returned {result.Outcome}; expected Applied.");
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
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var result = await PhysicalSchemaApplication.ApplyAsync(
                model.Target,
                new SqlitePhysicalSchemaExecutor(connection),
                cancellationToken: cancellationToken);
            if (result.Outcome != PhysicalSchemaApplicationOutcome.NoChanges)
                throw new InvalidOperationException($"Restart validation returned {result.Outcome}; expected NoChanges.");
        }
        OpenStores();
        for (var index = 0; index < operations; index++)
        {
            if (await TenantA.LoadAsync(
                    BenchmarkModelFactory.DocumentKind,
                    $"seed-{index:D8}",
                    cancellationToken) is null)
            {
                throw new InvalidOperationException("Restart/recovery gate could not load durable seeded data.");
            }
        }
        return Execution(operations, providerWork: new Dictionary<string, long> { ["schema_restart_validations"] = 1 });
    }

    private void OpenStores()
    {
        var tenantA = CreateStore(DocumentStoreAccess.Scoped(new("tenant-a")));
        var tenantB = CreateStore(DocumentStoreAccess.Scoped(new("tenant-b")));
        SetStores(
            tenantA,
            tenantB,
            SqlitePhysicalQueryRuntime.Create(tenantA, model.Manifest, model.Route, model.Target.Provider));
    }

    private SqlitePhysicalDocumentStore CreateStore(DocumentStoreAccess access) =>
        new(ConnectionString, model.Manifest, model.Target.Routes, access);

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken) =>
        ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";", cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed record MigrationState(BenchmarkPhysicalModel Additive, int Rows);
}
