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
    public static async Task<DiagnosticRecordDeploymentStatus> InspectAsync(
        string provider,
        string connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentStatus.Ready(deployment);

        return provider.ToLowerInvariant() switch
        {
            "sqlite" => await InspectSqliteAsync(connectionString, deployment, cancellationToken),
            "sqlserver" => await InspectSqlServerAsync(connectionString, deployment, cancellationToken),
            "postgresql" => await InspectPostgreSqlAsync(connectionString, deployment, cancellationToken),
            "mongodb" => await InspectMongoDbAsync(connectionString, database, deployment, cancellationToken),
            _ => throw new SchemaToolConfigurationException("GW-CLI-002", "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.")
        };
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

    private static async Task<DiagnosticRecordDeploymentStatus> InspectSqliteAsync(
        string connectionString,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var isInMemory = builder.DataSource == ":memory:" ||
                          builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                          builder.Mode == SqliteOpenMode.Memory;
        if (!isInMemory && !File.Exists(builder.DataSource))
            return DiagnosticRecordDeploymentStatus.Missing(deployment, "SQLite diagnostic-record schema is not materialized.");

        builder.Mode = SqliteOpenMode.ReadOnly;
        try
        {
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var objects = await ReadSqliteObjectsAsync(connection, cancellationToken);
            return await ReadRelationalDefinitionsAsync(
                deployment,
                objects.Tables,
                objects.Indexes,
                async stream =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
                    command.Parameters.AddWithValue("@stream", stream.Stream.Value);
                    return await command.ExecuteScalarAsync(cancellationToken) as string;
                });
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 14)
        {
            return DiagnosticRecordDeploymentStatus.Missing(deployment, "SQLite diagnostic-record schema is not materialized.");
        }
    }

    private static async Task<DiagnosticRecordDeploymentStatus> InspectSqlServerAsync(
        string connectionString,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var (tables, indexes) = await ReadSqlServerObjectsAsync(connection, cancellationToken);
        return await ReadRelationalDefinitionsAsync(
            deployment,
            tables,
            indexes,
            async stream =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
                command.Parameters.AddWithValue("@stream", stream.Stream.Value);
                return await command.ExecuteScalarAsync(cancellationToken) as string;
            });
    }

    private static async Task<DiagnosticRecordDeploymentStatus> InspectPostgreSqlAsync(
        string connectionString,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var (tables, indexes) = await ReadPostgreSqlObjectsAsync(connection, cancellationToken);
        return await ReadRelationalDefinitionsAsync(
            deployment,
            tables,
            indexes,
            async stream =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
                command.Parameters.AddWithValue("stream", stream.Stream.Value);
                return await command.ExecuteScalarAsync(cancellationToken) as string;
            });
    }

    private static async Task<DiagnosticRecordDeploymentStatus> InspectMongoDbAsync(
        string connectionString,
        string? database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        var url = MongoUrl.Create(connectionString);
        var databaseName = database ?? url.DatabaseName;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new SchemaToolConfigurationException("GW-CLI-006", "MongoDB requires '--database' or a database name in the connection URI.");
        var client = new MongoClient(url);
        var target = client.GetDatabase(databaseName);
        using var cursor = await target.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collections = (await cursor.ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var expected = new[]
        {
            MongoDbDiagnosticRecordNames.Records, MongoDbDiagnosticRecordNames.Streams,
            MongoDbDiagnosticRecordNames.AppendOperations, MongoDbDiagnosticRecordNames.AppendOutcomes,
            MongoDbDiagnosticRecordNames.TrimOperations, MongoDbDiagnosticRecordNames.ProviderState,
            MongoDbDiagnosticRecordNames.StreamDefinitions
        };
        if (expected.Any(name => !collections.Contains(name)))
            return DiagnosticRecordDeploymentStatus.Missing(deployment, "MongoDB diagnostic-record collections are not materialized.");
        if (!await HasRequiredMongoIndexesAsync(target, cancellationToken))
            return DiagnosticRecordDeploymentStatus.Missing(deployment, "MongoDB diagnostic-record indexes or operation ledgers are not materialized.");

        var definitions = target.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions);
        var drifted = new List<string>();
        var missing = new List<string>();
        foreach (var stream in deployment.Streams)
        {
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stream.Stream.Value)));
            var actual = await definitions.Find(Builders<BsonDocument>.Filter.Eq("_id", id))
                .FirstOrDefaultAsync(cancellationToken);
            if (actual is null)
            {
                missing.Add(stream.Stream.Value);
                continue;
            }
            var expectedFingerprint = DiagnosticRecordPhysicalSchemaState.Capture(stream).DefinitionFingerprint;
            if (!actual.TryGetValue("fingerprint", out var fingerprint) ||
                !StringComparer.Ordinal.Equals(fingerprint.AsString, expectedFingerprint))
                drifted.Add(stream.Stream.Value);
        }
        return DiagnosticRecordDeploymentStatus.FromDefinitionCheck(deployment, missing, drifted);
    }

    private static async Task<bool> HasRequiredMongoIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [MongoDbDiagnosticRecordNames.Records] =
            [
                "ux_groundwork_diagnostic_records_scope_record",
                "ix_groundwork_diagnostic_records_scope_cursor",
                "ix_groundwork_diagnostic_records_scope_fields",
                "ix_groundwork_diagnostic_records_scope_field_native"
            ],
            [MongoDbDiagnosticRecordNames.AppendOperations] =
            [
                "ix_groundwork_diagnostic_append_operations_outcome_cleanup",
                "ix_groundwork_diagnostic_append_operations_tombstone_cleanup"
            ],
            [MongoDbDiagnosticRecordNames.TrimOperations] =
            [
                "ix_groundwork_diagnostic_trim_operations_outcome_cleanup",
                "ix_groundwork_diagnostic_trim_operations_tombstone_cleanup"
            ],
            [MongoDbDiagnosticRecordNames.AppendOutcomes] =
            ["ux_groundwork_diagnostic_append_outcomes_operation_ordinal"]
        };
        foreach (var (collectionName, names) in expected)
        {
            using var cursor = await database.GetCollection<BsonDocument>(collectionName)
                .Indexes.ListAsync(cancellationToken);
            var indexes = (await cursor.ToListAsync(cancellationToken))
                .Select(index => index.GetValue("name", string.Empty).AsString)
                .ToHashSet(StringComparer.Ordinal);
            if (names.Any(name => !indexes.Contains(name)))
                return false;
        }
        return true;
    }

    private static async Task<DiagnosticRecordDeploymentStatus> ReadRelationalDefinitionsAsync(
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlySet<string> tables,
        IReadOnlySet<string> indexes,
        Func<DiagnosticRecordStreamDefinition, Task<string?>> readFingerprint)
    {
        var expectedTables = RelationalDiagnosticRecordSchema.Standard.Tables.Select(table => table.Name);
        var expectedIndexes = RelationalDiagnosticRecordSchema.Standard.Indexes.Select(index => index.Name);
        if (expectedTables.Any(name => !tables.Contains(name)) || expectedIndexes.Any(name => !indexes.Contains(name)))
            return DiagnosticRecordDeploymentStatus.Missing(deployment, "Diagnostic-record tables, indexes, or operation ledgers are not materialized.");

        var missing = new List<string>();
        var drifted = new List<string>();
        foreach (var stream in deployment.Streams)
        {
            var actual = await readFingerprint(stream);
            if (actual is null)
            {
                missing.Add(stream.Stream.Value);
                continue;
            }
            if (!StringComparer.Ordinal.Equals(actual, DiagnosticRecordPhysicalSchemaState.Capture(stream).DefinitionFingerprint))
                drifted.Add(stream.Stream.Value);
        }
        return DiagnosticRecordDeploymentStatus.FromDefinitionCheck(deployment, missing, drifted);
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
}

internal sealed record DiagnosticRecordDeploymentStatus(
    string Fingerprint,
    IReadOnlyList<string> DeclaredStreams,
    IReadOnlyList<string> PendingStreams,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsApplied => PendingStreams.Count == 0 && Diagnostics.All(item => !item.IsError);

    public static DiagnosticRecordDeploymentStatus Ready(DiagnosticRecordDeploymentManifest deployment) =>
        new(deployment.Fingerprint, deployment.Streams.Select(stream => stream.Stream.Value).ToArray(), [], []);

    public static DiagnosticRecordDeploymentStatus Missing(DiagnosticRecordDeploymentManifest deployment, string message) =>
        new(deployment.Fingerprint, deployment.Streams.Select(stream => stream.Stream.Value).ToArray(), deployment.Streams.Select(stream => stream.Stream.Value).ToArray(),
            [GroundworkDiagnostic.Warning("GW-DIAG-DEPLOY-001", message, "diagnosticRecords")]);

    public static DiagnosticRecordDeploymentStatus FromDefinitionCheck(
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> drifted)
    {
        var diagnostics = drifted.Select(stream => GroundworkDiagnostic.Error(
            "GW-DIAG-DEPLOY-002",
            $"Diagnostic stream '{stream}' has an incompatible persisted definition.",
            $"diagnosticRecords.{stream}")).ToArray();
        return new(
            deployment.Fingerprint,
            deployment.Streams.Select(stream => stream.Stream.Value).ToArray(),
            missing.OrderBy(stream => stream, StringComparer.Ordinal).ToArray(),
            diagnostics);
    }
}
