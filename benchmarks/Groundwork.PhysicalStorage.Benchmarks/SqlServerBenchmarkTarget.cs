using System.Data;
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
    private const int PrimaryPlanNoiseRows = 65_536;
    private const int LinkedPlanNoiseRows = 65_536;
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

    public override async Task<IReadOnlyList<NativePlanEvidence>> RunNativePlanGatesAsync(
        IReadOnlyList<BenchmarkPlanRequest> requests,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var noise = await SeedPlanNoiseAsync(connection, cancellationToken);
        var indexName = Model.Route.Indexes.Single().Name.Identifier;
        var evidence = new List<NativePlanEvidence>(requests.Count);
        try
        {
            await UpdateStatisticsAsync(connection, cancellationToken);
            foreach (var request in requests)
            {
                var rendered = RenderPlan(request);
                await using var command = connection.CreateCommand();
                command.CommandText = $"SET STATISTICS XML ON; {rendered.CommandText} SET STATISTICS XML OFF;";
                foreach (var (name, value) in rendered.Parameters)
                    command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var plans = await SqlServerShowplanReader.ReadAsync(reader, cancellationToken);
                var plan = plans.SingleOrDefault() ?? throw new InvalidOperationException(
                    $"SQL Server returned no native XML plan for {request.Workload}/{request.Operation}.");
                try
                {
                    SqlServerShowplanReader.EnsureScaleBearingIndex(plan, indexName);
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException(
                        $"SQL Server native-plan gate rejected {request.Workload}/{request.Operation}.",
                        exception);
                }
                evidence.Add(new NativePlanEvidence(
                    request,
                    Provider.ToString(), StorageForm.ToString(), BenchmarkModelFactory.QueryIdentity,
                    (Model.Route.LinkedIndexStorage ?? Model.Route.PrimaryStorage).Name.Identifier,
                    indexName, plan,
                    ["declared index is selected", "table and index scans are absent", "query shape is rendered by the certified production handler"]));
            }
        }
        finally
        {
            await RemovePlanNoiseAsync(connection, noise, CancellationToken.None);
            await UpdateStatisticsAsync(connection, CancellationToken.None);
        }
        return evidence;
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

    protected override async Task<long> CountProjectedRowsAsync(
        ExecutableStorageRoute route,
        ExecutableProjectedColumnRoute projection,
        string value,
        CancellationToken cancellationToken)
    {
        var table = projection.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {Q(table)} WHERE {Q(projection.Column.Identifier)} = @value;";
        command.Parameters.AddWithValue("@value", value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    protected override void ClearPools() => SqlConnection.ClearAllPools();

    private async Task<PlanNoise> SeedPlanNoiseAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var status = Model.Route.ProjectedColumns.Single(column => column.Definition.Path == "status");
        // Linked document queries join back to primary storage, so matched pairs need a larger
        // cardinality before SQL Server prefers the selective compound index over a narrow heap scan.
        var noiseRows = status.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? PrimaryPlanNoiseRows
            : LinkedPlanNoiseRows;
        var prefix = $"plan-noise-{Guid.NewGuid():N}-";
        var primaryTable = Model.Route.PrimaryStorage.Name.Identifier;
        var primaryId = Model.Route.Envelope.Id.Identifier;
        var linkedTable = Model.Route.LinkedIndexStorage?.Name.Identifier;
        var linkedId = Model.Route.LinkedRelationship?.DocumentId.Identifier;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var primaryColumns = await ReadWritableColumnsAsync(
                connection,
                transaction,
                primaryTable,
                cancellationToken);
            if (status.Target == ExecutableStorageObjectRole.PrimaryStorage)
            {
                var inserted = await ClonePlanRowsAsync(
                    connection,
                    transaction,
                    primaryTable,
                    primaryColumns,
                    Model.Route.Envelope.Identity,
                    $"s.{Q(status.Column.Identifier)} = @queryStatus",
                    new Dictionary<string, object> { ["queryStatus"] = "open" },
                    status.Column.Identifier,
                    Model.Route.Envelope.CanonicalJson.Identifier,
                    prefix,
                    noiseRows,
                    cancellationToken);
                EnsureNoiseRowCount(inserted, noiseRows, primaryTable);
            }
            else
            {
                var relationship = Model.Route.LinkedRelationship!;
                var source = await ReadLinkedSourceAsync(
                    connection,
                    transaction,
                    linkedTable!,
                    relationship,
                    status.Column.Identifier,
                    cancellationToken);
                var sourceParameters = new Dictionary<string, object>
                {
                    ["sourceKind"] = source.DocumentKind,
                    ["sourceScope"] = source.StorageScope,
                    ["sourceId"] = source.DocumentId
                };
                var primaryInserted = await ClonePlanRowsAsync(
                    connection,
                    transaction,
                    primaryTable,
                    primaryColumns,
                    Model.Route.Envelope.Identity,
                    $"s.{Q(Model.Route.Envelope.DocumentKind.Identifier)} = @sourceKind AND " +
                    $"s.{Q(Model.Route.Envelope.StorageScope.Identifier)} = @sourceScope AND " +
                    $"s.{Q(primaryId)} = @sourceId",
                    sourceParameters,
                    null,
                    Model.Route.Envelope.CanonicalJson.Identifier,
                    prefix,
                    noiseRows,
                    cancellationToken);
                EnsureNoiseRowCount(primaryInserted, noiseRows, primaryTable);

                var linkedColumns = await ReadWritableColumnsAsync(
                    connection,
                    transaction,
                    linkedTable!,
                    cancellationToken);
                var linkedInserted = await ClonePlanRowsAsync(
                    connection,
                    transaction,
                    linkedTable!,
                    linkedColumns,
                    relationship.Identity,
                    $"s.{Q(relationship.DocumentKind.Identifier)} = @sourceKind AND " +
                    $"s.{Q(relationship.StorageScope.Identifier)} = @sourceScope AND " +
                    $"s.{Q(linkedId!)} = @sourceId",
                    sourceParameters,
                    status.Column.Identifier,
                    null,
                    prefix,
                    noiseRows,
                    cancellationToken);
                EnsureNoiseRowCount(linkedInserted, noiseRows, linkedTable!);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        return new PlanNoise(primaryTable, primaryId, linkedTable, linkedId, prefix);
    }

    private static async Task RemovePlanNoiseAsync(
        SqlConnection connection,
        PlanNoise noise,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (noise.LinkedTable is not null)
                await DeletePlanRowsAsync(connection, transaction, noise.LinkedTable, noise.LinkedIdColumn!, noise.IdPrefix, cancellationToken);
            await DeletePlanRowsAsync(connection, transaction, noise.PrimaryTable, noise.PrimaryIdColumn, noise.IdPrefix, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadWritableColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND is_computed = 0 ORDER BY column_id;";
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<LinkedSource> ReadLinkedSourceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        ExecutableLinkedRelationshipRoute relationship,
        string statusColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT TOP (1) {Q(relationship.DocumentKind.Identifier)}, {Q(relationship.StorageScope.Identifier)}, {Q(relationship.DocumentId.Identifier)} " +
                              $"FROM {Q(table)} WHERE {Q(statusColumn)} = @queryStatus;";
        command.Parameters.AddWithValue("@queryStatus", "open");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("SQL Server plan gate requires one indexed source document.");
        return new LinkedSource(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<int> ClonePlanRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<string> columns,
        ExecutableDocumentIdentityRoute identity,
        string sourcePredicate,
        IReadOnlyDictionary<string, object> sourceParameters,
        string? statusColumn,
        string? canonicalJsonColumn,
        string prefix,
        int noiseRows,
        CancellationToken cancellationToken)
    {
        // The physical primary key uses the projected lookup identity, so cloned rows must replace
        // the original, comparison, and lookup fields as one canonical identity.
        const string identityTable = "#groundwork_plan_noise_identity";
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"""
                CREATE TABLE {identityTable} (
                    original_id nvarchar(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    comparison_key varbinary(1350) NOT NULL,
                    lookup_key binary(32) NOT NULL,
                    PRIMARY KEY NONCLUSTERED (lookup_key)
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var identities = new DataTable();
        identities.Columns.Add("original_id", typeof(string));
        identities.Columns.Add("comparison_key", typeof(byte[]));
        identities.Columns.Add("lookup_key", typeof(byte[]));
        for (var sequence = 1; sequence <= noiseRows; sequence++)
        {
            var projected = BenchmarkPlanNoiseIdentity.Create(identity, prefix, sequence);
            identities.Rows.Add(
                projected.OriginalId,
                Convert.FromHexString(projected.Projection.ComparisonKey),
                Convert.FromHexString(projected.Projection.LookupKey));
        }

        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
               {
                   DestinationTableName = identityTable,
                   BatchSize = 4096
               })
        {
            foreach (DataColumn column in identities.Columns)
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulkCopy.WriteToServerAsync(identities, cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH source AS (
                SELECT TOP (1) * FROM {Q(table)} s WHERE {sourcePredicate}
            )
            INSERT INTO {Q(table)} ({string.Join(", ", columns.Select(Q))})
            SELECT {string.Join(", ", columns.Select(column => column == identity.OriginalId.Identifier
                ? "n.original_id"
                : column == identity.ComparisonKey.Identifier ? "n.comparison_key"
                : column == identity.LookupKey.Identifier ? "n.lookup_key"
                : column == statusColumn ? "@noiseStatus"
                : column == canonicalJsonColumn ? $"JSON_MODIFY(s.{Q(column)}, '$.status', @noiseStatus)"
                : $"s.{Q(column)}"))}
            FROM source s CROSS JOIN {identityTable} n;
            DROP TABLE {identityTable};
            """;
        foreach (var (name, value) in sourceParameters)
            command.Parameters.AddWithValue($"@{name}", value);
        command.Parameters.AddWithValue("@noiseStatus", "__groundwork_plan_noise__");
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeletePlanRowsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        string idColumn,
        string prefix,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {Q(table)} WHERE {Q(idColumn)} LIKE @prefix + N'%';";
        command.Parameters.AddWithValue("@prefix", prefix);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void EnsureNoiseRowCount(int actual, int expected, string table)
    {
        if (actual != expected)
            throw new InvalidOperationException($"SQL Server plan gate seeded {actual} rows into '{table}'; expected {expected}.");
    }

    private async Task UpdateStatisticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var statistics = connection.CreateCommand();
        statistics.CommandText = Model.Route.LinkedIndexStorage is null
            ? $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)};"
            : $"UPDATE STATISTICS {Q(Model.Route.PrimaryStorage.Name.Identifier)}; UPDATE STATISTICS {Q(Model.Route.LinkedIndexStorage.Name.Identifier)};";
        await statistics.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Q(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed record PlanNoise(
        string PrimaryTable,
        string PrimaryIdColumn,
        string? LinkedTable,
        string? LinkedIdColumn,
        string IdPrefix);

    private sealed record LinkedSource(string DocumentKind, string StorageScope, string DocumentId);
}
