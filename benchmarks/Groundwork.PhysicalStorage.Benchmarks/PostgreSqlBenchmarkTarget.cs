using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed class PostgreSqlBenchmarkTarget(
    PhysicalStorageForm storageForm,
    string instance,
    string serverConnectionString,
    int migrationDatasetSize,
    string sourceDescription) : RelationalServerBenchmarkTarget(
        BenchmarkProvider.PostgreSql,
        storageForm,
        instance,
        migrationDatasetSize)
{
    private readonly string serverConnectionString = serverConnectionString;
    private readonly string schemaName = $"groundwork_bench_{instance}_{storageForm}".ToLowerInvariant();
    private readonly string sourceDescription = sourceDescription;
    private string connectionString = string.Empty;

    public override string ProviderVersion { get; protected set; } = "unknown";
    public override IReadOnlyDictionary<string, string> ProviderConfiguration => new Dictionary<string, string>
    {
        ["source"] = sourceDescription,
        ["schema_per_form"] = "true",
        ["pooling"] = new NpgsqlConnectionStringBuilder(serverConnectionString).Pooling.ToString(),
        ["connection_lifetime"] = "per-operation concurrent production sessions",
        ["factory"] = nameof(PostgreSqlDocumentStoreFactory.OpenPhysicalAsync)
    };

    protected override ProviderIdentity GroundworkProvider => PostgreSqlGroundworkCapabilities.Provider;
    protected override IProviderPhysicalNameNormalizer PhysicalNames => PostgreSqlGroundworkCapabilities.PhysicalNames;
    protected override string HandlerPrefix => "postgresql";
    protected override string ConnectionString => connectionString;

    public override async Task<IReadOnlyList<NativePlanEvidence>> RunNativePlanGatesAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        CancellationToken cancellationToken)
    {
        var store = RelationalTenantA;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var indexName = Model.Route.Indexes.Single().Name.Identifier;
        var evidence = new List<NativePlanEvidence>(requests.Count);
        foreach (var request in requests)
        {
            var rendered = RenderPlan(request, store);
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXPLAIN (FORMAT JSON) {rendered.CommandText}";
            foreach (var (name, value) in rendered.Parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var plan = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
            if (!plan.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
                plan.Contains("\"Node Type\": \"Seq Scan\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL native-plan gate rejected {request.Workload}/{request.Operation}. Expected index '{indexName}'.{Environment.NewLine}{plan}");
            }
            evidence.Add(new NativePlanEvidence(
                request,
                Provider.ToString(), StorageForm.ToString(), BenchmarkModelFactory.QueryIdentity,
                (Model.Route.LinkedIndexStorage ?? Model.Route.PrimaryStorage).Name.Identifier,
                indexName, plan,
                ["declared index is selected", "Seq Scan is absent", "query shape is rendered by the certified production handler"]));
        }
        return evidence;
    }

    public override async Task<StorageSnapshot> CaptureStorageAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var tables = new[] { Model.Route.PrimaryStorage.Name.Identifier, Model.Route.LinkedIndexStorage?.Name.Identifier }
            .Where(name => name is not null).Cast<string>().ToArray();
        long totalBytes = 0;
        long indexBytes = 0;
        long primaryRows = 0;
        long linkedRows = 0;
        foreach (var table in tables)
        {
            await using (var size = connection.CreateCommand())
            {
                size.CommandText = "SELECT pg_total_relation_size(to_regclass(quote_ident(@table))), " +
                                   "pg_indexes_size(to_regclass(quote_ident(@table)));";
                size.Parameters.AddWithValue("table", table);
                await using var reader = await size.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                totalBytes += reader.GetInt64(0);
                indexBytes += reader.GetInt64(1);
            }
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {Q(table)};";
            var rows = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            if (table == Model.Route.PrimaryStorage.Name.Identifier)
                primaryRows = rows;
            else
                linkedRows = rows;
        }
        return new StorageSnapshot(totalBytes, indexBytes, primaryRows, linkedRows,
            new Dictionary<string, long> { ["total_relation_bytes"] = totalBytes });
    }

    public override async ValueTask DisposeAsync() => await DisposeServerAsync();

    protected override async Task CreateIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA {Q(schemaName)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { SearchPath = schemaName }.ConnectionString;
    }

    protected override async Task DropIsolationBoundaryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connectionString))
            return;
        await using var connection = new NpgsqlConnection(serverConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {Q(schemaName)} CASCADE;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override IPhysicalSchemaExecutor CreateExecutor() => new PostgreSqlPhysicalSchemaExecutor(ConnectionString);

    protected override async Task<RelationalPhysicalDocumentStore> OpenStoreAsync(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        DocumentStoreAccess access,
        CancellationToken cancellationToken) =>
        await PostgreSqlDocumentStoreFactory.OpenPhysicalAsync(
            ConnectionString,
            manifest,
            GroundworkProvider,
            access,
            namePolicy,
            cancellationToken: cancellationToken);

    protected override async Task<string> ReadProviderVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW server_version;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "unknown";
    }

    protected override async Task<long> CountProjectedRowsAsync(
        ExecutableStorageRoute route,
        ExecutableProjectedColumnRoute projection,
        string value,
        CancellationToken cancellationToken)
    {
        var table = projection.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Q(table)} WHERE {Q(projection.Column.Identifier)} = @value;";
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    protected override void ClearPools() => NpgsqlConnection.ClearAllPools();

    protected override async Task FinalizeSeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var statistics = connection.CreateCommand();
        statistics.CommandText = Model.Route.LinkedIndexStorage is null
            ? $"SET default_statistics_target = 10000; ANALYZE {Q(Model.Route.PrimaryStorage.Name.Identifier)};"
            : $"SET default_statistics_target = 10000; " +
              $"ANALYZE {Q(Model.Route.PrimaryStorage.Name.Identifier)}; " +
              $"ANALYZE {Q(Model.Route.LinkedIndexStorage.Name.Identifier)};";
        await statistics.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
