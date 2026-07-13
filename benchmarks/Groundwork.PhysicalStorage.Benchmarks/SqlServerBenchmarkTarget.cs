using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class SqlServerBenchmarkTarget(
    PhysicalStorageForm storageForm,
    string instance,
    string serverConnectionString,
    int migrationDatasetSize,
    string sourceDescription) : RelationalServerBenchmarkTarget(
        BenchmarkProvider.SqlServer,
        storageForm,
        instance,
        migrationDatasetSize)
{
    private readonly string serverConnectionString = serverConnectionString;
    private readonly string databaseName = $"groundwork_bench_{instance}_{storageForm}".ToLowerInvariant();
    private readonly string sourceDescription = sourceDescription;
    private string connectionString = string.Empty;

    public override string ProviderVersion { get; protected set; } = "unknown";
    public override IReadOnlyDictionary<string, string> ProviderConfiguration => new Dictionary<string, string>
    {
        ["source"] = sourceDescription,
        ["database_per_form"] = "true",
        ["pooling"] = new SqlConnectionStringBuilder(serverConnectionString).Pooling.ToString(),
        ["connection_lifetime"] = "per-operation concurrent production sessions"
    };

    protected override ProviderIdentity GroundworkProvider => SqlServerGroundworkCapabilities.Provider;
    protected override IProviderPhysicalNameNormalizer PhysicalNames => SqlServerGroundworkCapabilities.PhysicalNames;
    protected override string HandlerPrefix => "sqlserver";
    protected override string ConnectionString => connectionString;

    public override async Task<NativePlanEvidence> RunNativePlanGateAsync(CancellationToken cancellationToken)
    {
        var rendered = RenderQuery(Query(take: 20));
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = Model.Route.LinkedIndexStorage is null
                ? $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)};"
                : $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)}; UPDATE STATISTICS {Q(Model.Route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {rendered.CommandText} SET STATISTICS XML OFF;";
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (reader.GetValue(ordinal) is System.Data.SqlTypes.SqlXml xml)
                        plans.Add(xml.Value);
                }
            }
        } while (await reader.NextResultAsync(cancellationToken));
        var plan = plans.SingleOrDefault() ?? throw new InvalidOperationException("SQL Server returned no native XML plan.");
        var indexName = Model.Route.Indexes.Single().Name.Identifier;
        if (!plan.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
            plan.Contains("PhysicalOp=\"Table Scan\"", StringComparison.OrdinalIgnoreCase) ||
            plan.Contains("PhysicalOp=\"Index Scan\"", StringComparison.OrdinalIgnoreCase) ||
            plan.Contains("PhysicalOp=\"Clustered Index Scan\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQL Server native-plan gate rejected the scale-bearing query. Expected index '{indexName}'.");
        }
        return new NativePlanEvidence(
            Provider.ToString(), StorageForm.ToString(), BenchmarkModelFactory.QueryIdentity,
            (Model.Route.LinkedIndexStorage ?? Model.Route.PrimaryStorage).Name.Identifier,
            indexName, plan,
            ["declared index is selected", "table and index scans are absent", "query is rendered by the certified production handler"]);
    }

    public override async Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var tables = new[] { Model.Route.PrimaryStorage.Name.Identifier, Model.Route.LinkedIndexStorage?.Name.Identifier }
            .Where(name => name is not null).Cast<string>().ToArray();
        long totalBytes = 0;
        long indexBytes = 0;
        long primaryRows = 0;
        long linkedRows = 0;
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(reserved_page_count), 0) * 8192,
                    COALESCE(SUM(CASE WHEN index_id > 0 THEN reserved_page_count ELSE 0 END), 0) * 8192,
                    COALESCE(SUM(CASE WHEN index_id IN (0, 1) THEN row_count ELSE 0 END), 0)
                FROM sys.dm_db_partition_stats
                WHERE object_id = OBJECT_ID(@table);
                """;
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalBytes += reader.GetInt64(0);
            indexBytes += reader.GetInt64(1);
            if (table == Model.Route.PrimaryStorage.Name.Identifier)
                primaryRows = reader.GetInt64(2);
            else
                linkedRows = reader.GetInt64(2);
        }
        return new StorageSnapshot(totalBytes, indexBytes, primaryRows, linkedRows,
            new Dictionary<string, long> { ["reserved_bytes"] = totalBytes });
    }

    public override async ValueTask DisposeAsync() => await DisposeServerAsync();

    protected override async Task CreateIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {Q(databaseName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        connectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    protected override async Task DropIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connectionString))
            return;
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE {Q(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(databaseName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override IPhysicalSchemaExecutor CreateExecutor() => new SqlServerPhysicalSchemaExecutor(ConnectionString);

    protected override RelationalPhysicalDocumentStore CreateStore(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access) => new SqlServerPhysicalDocumentStore(ConnectionString, manifest, routes, access);

    protected override async Task<string> ReadProviderVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
    }

    protected override void ClearPools() => SqlConnection.ClearAllPools();

    private static string Q(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}
