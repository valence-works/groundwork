using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.PostgreSql;
using Groundwork.SchemaTool;
using Microsoft.Data.SqlClient;
using MongoDB.Driver;
using Npgsql;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.SchemaTool.ProviderTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SchemaToolProviderParityCollection
{
    public const string Name = "schema-tool-provider-parity";
}

[Collection(SchemaToolProviderParityCollection.Name)]
public sealed class SchemaToolProviderParityTests
{
    [Fact]
    [Trait("Category", "SchemaToolProviderParity")]
    public async Task SqlServer_cli_lifecycle_is_live_restart_safe_authorized_and_secret_safe()
    {
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();
        await container.StartAsync();
        var connection = container.GetConnectionString();
        await ExerciseAsync(
            "sqlserver",
            connection,
            database: null,
            () => CountSqlServerInfrastructureAsync(connection),
            () => DropSqlServerIndexAsync(connection),
            () => ReadSqlServerIndexStateAsync(connection),
            "Server=127.0.0.1,1;Database=none;User Id=none;Password=provider-secret;Connect Timeout=1;Encrypt=false");
    }

    [Fact]
    [Trait("Category", "SchemaToolProviderParity")]
    public async Task PostgreSql_cli_lifecycle_is_live_restart_safe_authorized_and_secret_safe()
    {
        await using var container = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
            .WithDatabase("groundwork")
            .WithUsername("groundwork")
            .WithPassword("groundwork")
            .Build();
        await container.StartAsync();
        var connection = container.GetConnectionString();
        await ExerciseAsync(
            "postgresql",
            connection,
            database: null,
            () => CountPostgreSqlInfrastructureAsync(connection),
            () => DropPostgreSqlIndexAsync(connection),
            () => ReadPostgreSqlIndexStateAsync(connection),
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=provider-secret;Timeout=1");
    }

    [Fact]
    [Trait("Category", "SchemaToolProviderParity")]
    public async Task MongoDb_cli_lifecycle_is_live_restart_safe_authorized_and_secret_safe()
    {
        await using var container = new MongoDbBuilder("mongo:7.0.24")
            .WithReplicaSet("groundwork-rs")
            .Build();
        await container.StartAsync();
        var connection = container.GetConnectionString();
        const string database = "groundwork_schema_tool_provider_tests";
        await ExerciseAsync(
            "mongodb",
            connection,
            database,
            () => CountMongoDbInfrastructureAsync(connection, database),
            () => DropMongoDbIndexAsync(connection, database),
            () => ReadMongoDbIndexStateAsync(connection, database),
            "mongodb://none:provider-secret@127.0.0.1:1/?serverSelectionTimeoutMS=200&connectTimeoutMS=200");
    }

    [Fact]
    [Trait("Category", "SchemaToolProviderParity")]
    public async Task MongoDb_cli_plans_applies_and_live_validates_bounded_mutation_bindings()
    {
        await using var container = new MongoDbBuilder("mongo:7.0.24")
            .WithReplicaSet("groundwork-mutation-cli-rs")
            .Build();
        await container.StartAsync();
        var connection = container.GetConnectionString();
        const string database = "groundwork_schema_tool_mutation_tests";
        const string selectorIdentity = "schema_tool_provider_mutation_documents-by-category";

        var plan = await RunAsync(
            "plan",
            "mongodb",
            connection,
            database,
            typeof(MutationManifestSource));

        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.ExitCode);
        Assert.Contains(
            plan.Report.GetProperty("pendingOperations").EnumerateArray(),
            operation =>
                operation.GetProperty("kind").GetString() == "ApplyProviderDefinition" &&
                operation.GetProperty("subjectIdentity").GetString() == "prune-by-category");
        Assert.Contains(
            plan.Report.GetProperty("pendingOperations").EnumerateArray(),
            operation =>
                operation.GetProperty("kind").GetString() == "ApplyProviderDefinition" &&
                operation.GetProperty("subjectIdentity").GetString() == selectorIdentity);

        var apply = await RunAsync(
            "apply",
            "mongodb",
            connection,
            database,
            typeof(MutationManifestSource),
            "--safe");
        var validate = await RunAsync(
            "validate",
            "mongodb",
            connection,
            database,
            typeof(MutationManifestSource));

        Assert.Equal(SchemaToolExitCodes.Success, apply.ExitCode);
        Assert.Contains(
            apply.Report.GetProperty("appliedOperations").EnumerateArray(),
            operation =>
                operation.GetProperty("kind").GetString() == "ApplyProviderDefinition" &&
                operation.GetProperty("subjectIdentity").GetString() == "prune-by-category");
        Assert.Contains(
            apply.Report.GetProperty("appliedOperations").EnumerateArray(),
            operation =>
                operation.GetProperty("kind").GetString() == "ApplyProviderDefinition" &&
                operation.GetProperty("subjectIdentity").GetString() == selectorIdentity);
        Assert.Equal(SchemaToolExitCodes.Success, validate.ExitCode);
        Assert.Equal("ready", validate.Report.GetProperty("outcome").GetString());
        Assert.Empty(validate.Report.GetProperty("pendingOperations").EnumerateArray());
    }

    private static async Task ExerciseAsync(
        string provider,
        string connection,
        string? database,
        Func<Task<int>> countInfrastructure,
        Func<Task> driftAppliedSchema,
        Func<Task<(bool AppliedIndexExists, bool PendingIndexExists)>> readIndexState,
        string failingConnection)
    {
        Assert.Equal(0, await countInfrastructure());
        var validate = await RunAsync("validate", provider, connection, database, typeof(SafeManifestSource));
        Assert.Equal(SchemaToolExitCodes.Success, validate.ExitCode);
        Assert.Equal("live", validate.Report.GetProperty("inspectionMode").GetString());
        Assert.True(validate.Report.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.Equal(0, await countInfrastructure());

        var plan = await RunAsync("plan", provider, connection, database, typeof(SafeManifestSource));
        var status = await RunAsync("status", provider, connection, database, typeof(SafeManifestSource));
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.ExitCode);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, status.ExitCode);
        Assert.Equal(
            plan.Report.GetProperty("planFingerprint").GetString(),
            status.Report.GetProperty("planFingerprint").GetString());
        Assert.Equal(0, await countInfrastructure());

        var applied = await RunAsync(
            "apply",
            provider,
            connection,
            database,
            typeof(SafeManifestSource),
            "--safe");
        Assert.Equal(SchemaToolExitCodes.Success, applied.ExitCode);
        Assert.Equal("applied", applied.Report.GetProperty("outcome").GetString());

        var liveRestart = await RunAsync("validate", provider, connection, database, typeof(SafeManifestSource));
        var statusRestart = await RunAsync("status", provider, connection, database, typeof(SafeManifestSource));
        var applyRestart = await RunAsync(
            "apply",
            provider,
            connection,
            database,
            typeof(SafeManifestSource),
            "--safe");
        Assert.Equal(SchemaToolExitCodes.Success, liveRestart.ExitCode);
        Assert.Equal(0, liveRestart.Report.GetProperty("pendingOperations").GetArrayLength());
        Assert.Equal(SchemaToolExitCodes.Success, statusRestart.ExitCode);
        Assert.Equal("ready", statusRestart.Report.GetProperty("outcome").GetString());
        Assert.Equal(SchemaToolExitCodes.Success, applyRestart.ExitCode);
        Assert.Equal("ready", applyRestart.Report.GetProperty("outcome").GetString());

        await driftAppliedSchema();
        var driftedWithPendingDiff = await RunAsync(
            "validate",
            provider,
            connection,
            database,
            typeof(AdditiveManifestSource));
        Assert.Equal(SchemaToolExitCodes.ValidationFailed, driftedWithPendingDiff.ExitCode);
        Assert.Contains(
            driftedWithPendingDiff.Report.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "GW-CLI-012");
        Assert.Contains(
            driftedWithPendingDiff.Report.GetProperty("pendingOperations").EnumerateArray(),
            operation => operation.GetProperty("kind").GetString() == "AddProjectedColumn");
        Assert.NotNull(driftedWithPendingDiff.Report.GetProperty("appliedTargetFingerprint").GetString());
        Assert.False(driftedWithPendingDiff.Report.GetProperty("targetMutated").GetBoolean());
        var indexState = await readIndexState();
        Assert.False(indexState.AppliedIndexExists);
        Assert.False(indexState.PendingIndexExists);

        var authorizedPlan = await RunAsync(
            "plan",
            provider,
            connection,
            database,
            typeof(AuthorizedManifestSource));
        Assert.Equal(SchemaToolExitCodes.PendingChanges, authorizedPlan.ExitCode);
        var safeRejected = await RunAsync(
            "apply",
            provider,
            connection,
            database,
            typeof(AuthorizedManifestSource),
            "--safe");
        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired, safeRejected.ExitCode);
        Assert.False(safeRejected.Report.GetProperty("targetMutated").GetBoolean());

        var planFingerprint = authorizedPlan.Report.GetProperty("planFingerprint").GetString()!;
        var operationIdentity = Assert.Single(
                authorizedPlan.Report.GetProperty("authorization")
                    .GetProperty("destructiveOperationsRequired")
                    .EnumerateArray())
            .GetString()!;
        var exact = await RunAsync(
            "apply",
            provider,
            connection,
            database,
            typeof(AuthorizedManifestSource),
            "--expected-plan", planFingerprint,
            "--allow-destructive", operationIdentity,
            "--allow-semantic", "provider-reclassify-v2");
        Assert.Equal(SchemaToolExitCodes.Success, exact.ExitCode);
        Assert.Equal("applied", exact.Report.GetProperty("outcome").GetString());

        var failure = await RunAsync(
            "validate",
            provider,
            failingConnection,
            database,
            typeof(SafeManifestSource));
        Assert.Equal(SchemaToolExitCodes.ExecutionFailed, failure.ExitCode);
        Assert.Equal("GW-CLI-010", Assert.Single(failure.Report.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.DoesNotContain("provider-secret", failure.Report.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, failure.Error);
    }

    private static async Task<CliResult> RunAsync(
        string command,
        string provider,
        string connection,
        string? database,
        Type source,
        params string[] extra)
    {
        var arguments = new List<string>
        {
            command,
            "--manifest-assembly", typeof(SchemaToolProviderParityTests).Assembly.Location,
            "--manifest-type", source.FullName!,
            "--provider", provider,
            "--connection", connection,
            "--output", "json"
        };
        if (database is not null)
        {
            arguments.Add("--database");
            arguments.Add(database);
        }
        arguments.AddRange(extra);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var report = JsonDocument.Parse(output.ToString());
        return new CliResult(exit, report.RootElement.Clone(), error.ToString());
    }

    private static async Task<int> CountSqlServerInfrastructureAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'groundwork_physical_schema_%';";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountPostgreSqlInfrastructureAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE 'groundwork_physical_schema_%';";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountMongoDbInfrastructureAsync(string connectionString, string database)
    {
        var names = await (await new MongoClient(connectionString)
                .GetDatabase(database)
                .ListCollectionNamesAsync())
            .ToListAsync();
        return names.Count(name => name.StartsWith("groundwork_physical_schema_", StringComparison.Ordinal));
    }

    private static async Task DropSqlServerIndexAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX [schema_tool_provider_documents-by-category] ON [schema_tool_provider_documents];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(bool AppliedIndexExists, bool PendingIndexExists)> ReadSqlServerIndexStateAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sys.indexes
            WHERE object_id = OBJECT_ID('schema_tool_provider_documents')
              AND name IN ('schema_tool_provider_documents-by-category', 'schema_tool_provider_documents-by-priority');
            """;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return (
            names.Contains("schema_tool_provider_documents-by-category"),
            names.Contains("schema_tool_provider_documents-by-priority"));
    }

    private static async Task DropPostgreSqlIndexAsync(string connectionString)
    {
        var indexName = GetPostgreSqlIndexName("schema_tool_provider_documents-by-category");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP INDEX {new NpgsqlCommandBuilder().QuoteIdentifier(indexName)};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(bool AppliedIndexExists, bool PendingIndexExists)> ReadPostgreSqlIndexStateAsync(
        string connectionString)
    {
        var appliedIndexName = GetPostgreSqlIndexName("schema_tool_provider_documents-by-category");
        var pendingIndexName = GetPostgreSqlIndexName("schema_tool_provider_documents-by-priority");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = current_schema()
              AND indexname IN (@applied, @pending);
            """;
        command.Parameters.AddWithValue("applied", appliedIndexName);
        command.Parameters.AddWithValue("pending", pendingIndexName);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return (
            names.Contains(appliedIndexName),
            names.Contains(pendingIndexName));
    }

    private static string GetPostgreSqlIndexName(string logicalName) =>
        PostgreSqlGroundworkCapabilities.PhysicalNames.Normalize(new ProviderPhysicalNameContext(
            new StorageUnitIdentity("documents"),
            PhysicalObjectKind.PhysicalIndex,
            logicalName));

    private static async Task DropMongoDbIndexAsync(string connectionString, string database)
    {
        var collection = new MongoClient(connectionString)
            .GetDatabase(database)
            .GetCollection<MongoDB.Bson.BsonDocument>("schema_tool_provider_documents");
        await collection.Indexes.DropOneAsync("schema_tool_provider_documents-by-category");
    }

    private static async Task<(bool AppliedIndexExists, bool PendingIndexExists)> ReadMongoDbIndexStateAsync(
        string connectionString,
        string database)
    {
        var collection = new MongoClient(connectionString)
            .GetDatabase(database)
            .GetCollection<MongoDB.Bson.BsonDocument>("schema_tool_provider_documents");
        var names = (await (await collection.Indexes.ListAsync()).ToListAsync())
            .Select(index => index.GetValue("name").AsString)
            .ToHashSet(StringComparer.Ordinal);
        return (
            names.Contains("schema_tool_provider_documents-by-category"),
            names.Contains("schema_tool_provider_documents-by-priority"));
    }

    public sealed class SafeManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => SchemaToolProviderParityTests.CreateManifest(
            "schema-tool-provider-tests",
            "schema_tool_provider_documents");
    }

    public sealed class AuthorizedManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => SchemaToolProviderParityTests.CreateManifest(
            "schema-tool-provider-authorized-tests",
            "schema_tool_provider_authorized_documents",
            new PhysicalEvolutionMetadata(
                IsDestructive: true,
                SemanticMigrationIdentity: "provider-reclassify-v2"));
    }

    public sealed class AdditiveManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => SchemaToolProviderParityTests.CreateManifest(
            "schema-tool-provider-tests",
            "schema_tool_provider_documents",
            manifestVersion: "2",
            includePriority: true);
    }

    public sealed class MutationManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest()
        {
            var manifest = SchemaToolProviderParityTests.CreateManifest(
                "schema-tool-provider-mutation-tests",
                "schema_tool_provider_mutation_documents");
            var unit = Assert.Single(manifest.StorageUnits);
            var storage = unit.PhysicalStorage!;
            const string indexIdentity = "schema_tool_provider_mutation_documents-by-category";
            var logicalIndex = new LogicalIndexDeclaration(
                indexIdentity,
                [new IndexField("category", IndexValueKind.Keyword)],
                IndexValueKind.Keyword,
                isUnique: false,
                MissingValueBehavior.Excluded);
            var query = new BoundedQueryDeclaration(
                "list-by-category",
                indexIdentity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true,
                resultOperations: new HashSet<BoundedQueryResultOperation>
                {
                    BoundedQueryResultOperation.Count
                });
            return manifest with
            {
                StorageUnits =
                [
                    unit with
                    {
                        PhysicalStorage = new StorageUnitPhysicalStorage(
                            storage.ProvisioningMode,
                            storage.Policy,
                            [logicalIndex],
                            [query],
                            storage.NameOverrides,
                            [new BoundedMutationDeclaration(
                                "prune-by-category",
                                query.Identity,
                                BoundedMutationAction.Delete())])
                    }
                ]
            };
        }
    }

    private static StorageManifest CreateManifest(
        string identity,
        string table,
        PhysicalEvolutionMetadata? evolution = null,
        string manifestVersion = "1",
        bool includePriority = false)
    {
        var columns = new List<ProjectedColumnDefinition>
        {
            new("category", "category", PortablePhysicalType.String, Length: 128, IsNullable: false)
        };
        var indexes = new List<PhysicalIndexDefinition>
        {
            new(
                $"{table}-by-category",
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("category", 1)
                ],
                missingValueBehavior: MissingValueBehavior.Excluded)
        };
        if (includePriority)
        {
            columns.Add(new ProjectedColumnDefinition("priority", "priority", PortablePhysicalType.Int32));
            indexes.Add(new PhysicalIndexDefinition(
                $"{table}-by-priority",
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("priority", 1)
                ],
                missingValueBehavior: MissingValueBehavior.Excluded));
        }
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            table,
            columns,
            indexes: indexes,
            evolution: evolution);
        var unit = new StorageUnit(
            new StorageUnitIdentity("documents"),
            "document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition))
        };
        return new StorageManifest(
            new StorageManifestIdentity(identity),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion(manifestVersion),
            [unit],
            new HashSet<string>(),
            []);
    }

    private sealed record CliResult(int ExitCode, JsonElement Report, string Error);
}
