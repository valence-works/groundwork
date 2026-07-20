using Groundwork.Core.Validation;
using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.MongoDb.DiagnosticRecords;
using Groundwork.PostgreSql.DiagnosticRecords;
using Groundwork.SqlServer.DiagnosticRecords;
using Groundwork.Sqlite.DiagnosticRecords;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.SchemaTool;

/// <summary>
/// Provider adapter used only by the deployment tool. The application-facing declaration remains
/// provider-neutral; connections and provider SDK types are intentionally contained here.
/// </summary>
internal static class DiagnosticRecordDeploymentCoordinator
{
    public static async Task<DiagnosticRecordDeploymentStatus> AdmitAsync(
        string provider,
        string? connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        bool includeTopology,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentStatus.Ready(provider, deployment);
        try
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlite":
                    foreach (var stream in deployment.Streams)
                        SqliteDiagnosticRecordStoreFactory.ValidateDefinition(stream);
                    break;
                case "postgresql":
                    foreach (var stream in deployment.Streams)
                        PostgreSqlDiagnosticRecordStoreFactory.ValidateDefinition(stream);
                    break;
                case "sqlserver":
                    if (includeTopology)
                        await SqlServerDiagnosticRecordStoreFactory.ValidateAdmissionAsync(
                            connectionString!, deployment.Streams, cancellationToken);
                    else
                        foreach (var stream in deployment.Streams)
                            SqlServerDiagnosticRecordStoreFactory.ValidateDefinition(stream);
                    break;
                case "mongodb":
                    if (includeTopology)
                    {
                        var databaseName = ResolveMongoDatabase(connectionString!, database);
                        await MongoDbDiagnosticRecordStoreFactory.ValidateAdmissionAsync(
                            connectionString!, databaseName, deployment.Streams, cancellationToken);
                    }
                    else
                        foreach (var stream in deployment.Streams)
                            MongoDbDiagnosticRecordStoreFactory.ValidateDefinition(stream);
                    break;
                default:
                    throw new SchemaToolConfigurationException("GW-CLI-002", "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.");
            }
            return DiagnosticRecordDeploymentStatus.Uninspected(provider, deployment);
        }
        catch (Exception exception) when (exception is not SchemaToolConfigurationException &&
                                          (exception is DiagnosticRecordValidationException or
                                              InvalidOperationException or
                                              NotSupportedException))
        {
            return DiagnosticRecordDeploymentStatus.Rejected(provider, deployment);
        }
    }

    public static async Task<DiagnosticRecordDeploymentStatus> InspectAsync(
        string provider,
        string connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentStatus.Ready(provider, deployment);

        var inspector = CreateInspector(provider, connectionString, database);
        try
        {
            // Runtime admission is the sole authority for whether the deployed schema is
            // usable. The inventory below is deliberately limited to preserving the CLI's
            // operation-level plan display; it must never downgrade an admission result.
            var inspection = await inspector.InspectAsync(deployment, cancellationToken);
            var inventory = await ReadOperationInventoryAsync(
                inspector.Provider,
                connectionString,
                database,
                deployment,
                cancellationToken);
            return DiagnosticRecordDeploymentStatus.FromInspection(
                inspection,
                deployment,
                inventory.PhysicalStores,
                inventory.Indexes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep inspection failures semantically aligned with runtime admission: neither
            // validation, topology, connectivity, nor a raced catalog read may be mistaken for
            // a generic schema-tool failure.
            return DiagnosticRecordDeploymentStatus.Rejected(inspector.Provider, deployment);
        }
    }

    public static async Task ApplyAsync(
        string provider,
        string connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        foreach (var stream in deployment.Streams)
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlite":
                    await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, stream, cancellationToken: cancellationToken);
                    break;
                case "sqlserver":
                    await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, stream, cancellationToken: cancellationToken);
                    break;
                case "postgresql":
                    await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, stream, cancellationToken: cancellationToken);
                    break;
                case "mongodb":
                    var databaseName = database ?? MongoUrl.Create(connectionString).DatabaseName;
                    if (string.IsNullOrWhiteSpace(databaseName))
                        throw new SchemaToolConfigurationException("GW-CLI-006", "MongoDB requires '--database' or a database name in the connection URI.");
                    await using (var handle = await MongoDbDiagnosticRecordStoreFactory.CreateAsync(
                                     connectionString, databaseName, stream, cancellationToken: cancellationToken))
                    {
                        // The factory is the topology gate. Disposal releases the client even when
                        // this command is only establishing the declared stream schema.
                    }
                    break;
                default:
                    throw new SchemaToolConfigurationException("GW-CLI-002", "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.");
            }
        }
    }

    private static IDiagnosticRecordDeploymentInspector CreateInspector(
        string provider,
        string connectionString,
        string? database) =>
        provider.ToLowerInvariant() switch
        {
            "sqlite" => new SqliteDiagnosticRecordDeploymentInspector(connectionString),
            "sqlserver" => new SqlServerDiagnosticRecordDeploymentInspector(connectionString),
            "postgresql" => new PostgreSqlDiagnosticRecordDeploymentInspector(connectionString),
            "mongodb" => new MongoDbDiagnosticRecordDeploymentInspector(
                connectionString,
                ResolveMongoDatabase(connectionString, database)),
            _ => throw new SchemaToolConfigurationException("GW-CLI-002", "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.")
        };

    private static async Task<DiagnosticRecordOperationInventory> ReadOperationInventoryAsync(
        string provider,
        string connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case "sqlite":
            {
                var builder = new SqliteConnectionStringBuilder(connectionString);
                var isMemory = builder.DataSource == ":memory:" ||
                               builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                               builder.Mode == SqliteOpenMode.Memory;
                if (!isMemory && !builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !File.Exists(builder.DataSource))
                    return DiagnosticRecordOperationInventory.Empty;
                builder.Mode = SqliteOpenMode.ReadOnly;
                await using var connection = new SqliteConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                var (tables, indexes) = await ReadSqliteObjectsAsync(connection, cancellationToken);
                return new(tables, indexes);
            }
            case "sqlserver":
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                var (tables, indexes) = await ReadSqlServerObjectsAsync(connection, cancellationToken);
                return new(tables, indexes);
            }
            case "postgresql":
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                var (tables, indexes) = await ReadPostgreSqlObjectsAsync(connection, cancellationToken);
                return new(tables, indexes);
            }
            case "mongodb":
            {
                using var client = new MongoClient(connectionString);
                var target = client.GetDatabase(ResolveMongoDatabase(connectionString, database));
                using var cursor = await target.ListCollectionNamesAsync(cancellationToken: cancellationToken);
                var collections = (await cursor.ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
                var expected = DiagnosticRecordDeploymentOperation.Create("mongodb", deployment)
                    .Where(operation => operation.Kind == "CreateDiagnosticCollection")
                    .Select(operation => operation.SubjectIdentity)
                    .Where(collections.Contains)
                    .ToHashSet(StringComparer.Ordinal);
                return new(collections, await ReadMongoIndexesAsync(target, expected, cancellationToken));
            }
            default:
                throw new SchemaToolConfigurationException("GW-CLI-002", "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.");
        }
    }

    private static async Task<IReadOnlySet<string>> ReadMongoIndexesAsync(
        IMongoDatabase database,
        IReadOnlySet<string> collections,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collectionName in collections)
        {
            using var cursor = await database.GetCollection<BsonDocument>(collectionName)
                .Indexes.ListAsync(cancellationToken);
            foreach (var index in await cursor.ToListAsync(cancellationToken))
                result.Add($"{collectionName}/{index.GetValue("name", string.Empty).AsString}");
        }
        return result;
    }


    private static async Task<(IReadOnlySet<string> Tables, IReadOnlySet<string> Indexes)> ReadSqliteObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('table', 'index');";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            (reader.GetString(0) == "table" ? tables : indexes).Add(reader.GetString(1));
        return (tables, indexes);
    }

    private static async Task<(IReadOnlySet<string> Tables, IReadOnlySet<string> Indexes)> ReadSqlServerObjectsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.tables; SELECT name FROM sys.indexes WHERE name IS NOT NULL;";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) indexes.Add(reader.GetString(0));
        return (tables, indexes);
    }

    private static async Task<(IReadOnlySet<string> Tables, IReadOnlySet<string> Indexes)> ReadPostgreSqlObjectsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = current_schema(); SELECT indexname FROM pg_indexes WHERE schemaname = current_schema();";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) indexes.Add(reader.GetString(0));
        return (tables, indexes);
    }

    private static string ResolveMongoDatabase(string connectionString, string? database)
    {
        var databaseName = database ?? MongoUrl.Create(connectionString).DatabaseName;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new SchemaToolConfigurationException("GW-CLI-006", "MongoDB requires '--database' or a database name in the connection URI.");
        return databaseName;
    }
}

internal sealed record DiagnosticRecordDeploymentStatus(
    string Fingerprint,
    IReadOnlyList<string> DeclaredStreams,
    IReadOnlyList<string> PendingStreams,
    IReadOnlyList<DiagnosticRecordDeploymentOperation> Operations,
    IReadOnlyList<DiagnosticRecordDeploymentOperation> PendingOperations,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsApplied => PendingOperations.Count == 0 && Diagnostics.All(item => !item.IsError);

    public IReadOnlyList<DiagnosticRecordDeploymentOperation> AppliedOperations =>
        Operations.ExceptBy(PendingOperations.Select(item => item.Identity), item => item.Identity).ToArray();

    public static DiagnosticRecordDeploymentStatus Ready(string provider, DiagnosticRecordDeploymentManifest deployment)
    {
        var operations = DiagnosticRecordDeploymentOperation.Create(provider, deployment);
        return new(TargetFingerprint(provider, deployment, operations), Streams(deployment), [], operations, [], []);
    }

    public static DiagnosticRecordDeploymentStatus Uninspected(string provider, DiagnosticRecordDeploymentManifest deployment)
    {
        var operations = DiagnosticRecordDeploymentOperation.Create(provider, deployment);
        return new(TargetFingerprint(provider, deployment, operations), Streams(deployment), Streams(deployment), operations, operations, []);
    }

    public static DiagnosticRecordDeploymentStatus Rejected(string provider, DiagnosticRecordDeploymentManifest deployment)
    {
        var status = Uninspected(provider, deployment);
        return status with
        {
            Diagnostics =
            [
                GroundworkDiagnostic.Error(
                    "GW-DIAG-DEPLOY-003",
                    "Diagnostic-record definitions or provider topology are incompatible with the selected provider.",
                    "diagnosticRecords")
            ]
        };
    }

    /// <summary>
    /// Projects the provider admission result into the CLI report. Catalog names are used only to
    /// retain the existing per-operation plan detail; admission itself has already checked full
    /// physical shape, persisted definition, and comparison-algorithm state.
    /// </summary>
    public static DiagnosticRecordDeploymentStatus FromInspection(
        DiagnosticRecordDeploymentInspection inspection,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlySet<string> physicalStores,
        IReadOnlySet<string> indexes)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(physicalStores);
        ArgumentNullException.ThrowIfNull(indexes);

        var provider = inspection.Provider;
        var operations = DiagnosticRecordDeploymentOperation.Create(provider, deployment);
        var missing = inspection.MissingStreams;
        var drifted = inspection.DriftedStreams;
        var pending = operations.Where(operation => operation.Kind switch
        {
            "CreateDiagnosticTable" or "CreateDiagnosticCollection" =>
                !physicalStores.Contains(operation.SubjectIdentity),
            "CreateDiagnosticIndex" => !indexes.Contains(operation.SubjectIdentity),
            "RegisterDiagnosticStream" =>
                missing.Contains(operation.SubjectIdentity, StringComparer.Ordinal) ||
                drifted.Contains(operation.SubjectIdentity, StringComparer.Ordinal),
            _ => true
        }).ToArray();
        var diagnostics = new List<GroundworkDiagnostic>();
        switch (inspection.Status)
        {
            case DiagnosticRecordDeploymentAdmissionStatus.Drifted:
                diagnostics.AddRange(drifted.Select(stream => GroundworkDiagnostic.Error(
                    DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted,
                    $"Diagnostic stream '{stream}' has incompatible physical schema, persisted definition, or comparison-algorithm state.",
                    $"diagnosticRecords.{stream}")));
                break;
            case DiagnosticRecordDeploymentAdmissionStatus.Rejected:
                diagnostics.Add(GroundworkDiagnostic.Error(
                    DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed,
                    "Diagnostic-record definitions or provider topology are incompatible with the selected provider.",
                    "diagnosticRecords"));
                break;
        }
        var hasMissingPhysicalOperation = pending.Any(operation => operation.Kind is
            "CreateDiagnosticTable" or "CreateDiagnosticCollection" or "CreateDiagnosticIndex");
        if (inspection.Status == DiagnosticRecordDeploymentAdmissionStatus.Missing ||
            inspection.Status == DiagnosticRecordDeploymentAdmissionStatus.Ready && hasMissingPhysicalOperation)
        {
            diagnostics.Add(GroundworkDiagnostic.Warning(
                DiagnosticRecordDeploymentAdmissionErrorCodes.Missing,
                "Diagnostic-record deployment remains incomplete.",
                "diagnosticRecords"));
        }
        return new(
            TargetFingerprint(provider, deployment, operations),
            Streams(deployment),
            missing.Concat(drifted).Distinct(StringComparer.Ordinal)
                .OrderBy(stream => stream, StringComparer.Ordinal)
                .ToArray(),
            operations,
            pending,
            diagnostics.ToArray());
    }

    private static string[] Streams(DiagnosticRecordDeploymentManifest deployment) =>
        deployment.Streams.Select(stream => stream.Stream.Value).ToArray();

    private static string TargetFingerprint(
        string provider,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyList<DiagnosticRecordDeploymentOperation> operations) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            new[]
                {
                    "groundwork-diagnostic-target-v1",
                    provider.ToLowerInvariant(),
                    deployment.DiagnosticFingerprint
                }
                .Concat(operations.SelectMany(operation => new[] { operation.Identity, operation.Fingerprint }))))));
}

internal sealed record DiagnosticRecordOperationInventory(
    IReadOnlySet<string> PhysicalStores,
    IReadOnlySet<string> Indexes)
{
    public static readonly DiagnosticRecordOperationInventory Empty = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

internal sealed record DiagnosticRecordDeploymentOperation(
    string Identity,
    string Fingerprint,
    string Kind,
    string SubjectIdentity)
{
    public static IReadOnlyList<DiagnosticRecordDeploymentOperation> Create(
        string provider,
        DiagnosticRecordDeploymentManifest deployment)
    {
        var operations = new List<DiagnosticRecordDeploymentOperation>();
        if (provider.Equals("mongodb", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var collection in MongoCollections)
                operations.Add(Create("CreateDiagnosticCollection", collection, collection));
            foreach (var index in MongoIndexes(deployment))
                operations.Add(Create("CreateDiagnosticIndex", index, index));
        }
        else
        {
            foreach (var table in RelationalDiagnosticRecordSchema.Standard.Tables)
                operations.Add(Create(
                    "CreateDiagnosticTable",
                    table.Name,
                    provider.ToLowerInvariant() + "|" + string.Join('|', table.Columns.Select(column =>
                        $"{column.Name}:{column.Type}:{column.IsNullable}:{column.UsesBinaryTextSemantics}"))
                    + "|" + string.Join(',', table.PrimaryKey)));
            foreach (var index in RelationalDiagnosticRecordSchema.Standard.Indexes)
                operations.Add(Create(
                    "CreateDiagnosticIndex",
                    index.Name,
                    $"{provider.ToLowerInvariant()}|{index.Table}|{string.Join(',', index.Columns)}|{index.IsUnique}"));
        }
        operations.AddRange(deployment.Streams.Select(stream =>
            Create(
                "RegisterDiagnosticStream",
                stream.Stream.Value,
                DiagnosticRecordPhysicalSchemaState.Capture(stream).DefinitionFingerprint)));
        return operations.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
    }

    private static DiagnosticRecordDeploymentOperation Create(string kind, string subject, string payload)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"groundwork-diagnostic-operation-v1\n{kind}\n{subject}\n{payload}")));
        return new(
            $"diagnostic-record:{ToKebab(kind)}:{subject}:{fingerprint[..16]}",
            fingerprint,
            kind,
            subject);
    }

    private static string ToKebab(string value) =>
        string.Concat(value.Select((character, index) =>
            index != 0 && char.IsUpper(character)
                ? $"-{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    private static string[] MongoCollections =>
    [
        MongoDbDiagnosticRecordNames.Records,
        MongoDbDiagnosticRecordNames.Streams,
        MongoDbDiagnosticRecordNames.AppendOperations,
        MongoDbDiagnosticRecordNames.AppendOutcomes,
        MongoDbDiagnosticRecordNames.TrimOperations,
        MongoDbDiagnosticRecordNames.ProviderState,
        MongoDbDiagnosticRecordNames.StreamDefinitions
    ];

    private static IEnumerable<string> MongoIndexes(DiagnosticRecordDeploymentManifest deployment)
    {
        yield return $"{MongoDbDiagnosticRecordNames.Records}/ux_groundwork_diagnostic_records_scope_record";
        yield return $"{MongoDbDiagnosticRecordNames.Records}/ix_groundwork_diagnostic_records_scope_cursor";
        yield return $"{MongoDbDiagnosticRecordNames.Records}/ix_groundwork_diagnostic_records_scope_fields";
        yield return $"{MongoDbDiagnosticRecordNames.Records}/ix_groundwork_diagnostic_records_scope_field_native";
        yield return $"{MongoDbDiagnosticRecordNames.AppendOperations}/ix_groundwork_diagnostic_append_operations_outcome_cleanup";
        yield return $"{MongoDbDiagnosticRecordNames.AppendOperations}/ix_groundwork_diagnostic_append_operations_tombstone_cleanup";
        yield return $"{MongoDbDiagnosticRecordNames.TrimOperations}/ix_groundwork_diagnostic_trim_operations_outcome_cleanup";
        yield return $"{MongoDbDiagnosticRecordNames.TrimOperations}/ix_groundwork_diagnostic_trim_operations_tombstone_cleanup";
        yield return $"{MongoDbDiagnosticRecordNames.AppendOutcomes}/ux_groundwork_diagnostic_append_outcomes_operation_ordinal";
        foreach (var field in deployment.Streams.SelectMany(stream =>
                     stream.Fields.Where(field => field.IsOrderable || field.SupportsLatestPerKey)
                         .Select(field => (Name: field.Name, SupportsLatestPerKey: field.SupportsLatestPerKey))
                         .Append((Name: DiagnosticRecordFieldNames.OccurredAt, SupportsLatestPerKey: false)))
                 .GroupBy(field => field.Name, StringComparer.Ordinal)
                 .Select(group => (
                     Name: group.Key,
                     SupportsLatestPerKey: group.Any(field => field.SupportsLatestPerKey))))
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(field.Name)))[..24];
            yield return $"{MongoDbDiagnosticRecordNames.Records}/ix_groundwork_diagnostic_records_order_{hash}";
            if (field.SupportsLatestPerKey)
                yield return $"{MongoDbDiagnosticRecordNames.Records}/ix_groundwork_diagnostic_records_latest_{hash}";
        }
    }
}
