using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class RelationalDocumentIdentityAcceptanceConformance : DocumentIdentityAcceptanceConformance
{
    protected abstract ProviderIdentity Provider { get; }
    protected abstract IProviderPhysicalNameNormalizer PhysicalNames { get; }
    protected abstract string DialectName { get; }
    protected abstract IPhysicalSchemaExecutor CreateSchemaExecutor();
    internal abstract Task<string> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route);
    protected abstract DocumentIdentityAcceptanceFixture CreateFixture(
        StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentIdentityAcceptanceSurface surface);

    protected sealed override async Task<DocumentIdentityAcceptanceFixture> CreateIdentityFixtureAsync(
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        DocumentIdentityAcceptanceSurface surface = DocumentIdentityAcceptanceSurface.Exact)
    {
        var manifest = DocumentIdentityAcceptanceModel.Manifest(
            form,
            stringCasePolicy,
            surface,
            Guid.NewGuid().ToString("N")[..8]);
        var resolution = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, PhysicalNames);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        var target = new PhysicalSchemaTarget(manifest.Identity, manifest.Version, Provider, compilation.Routes);
        await PhysicalSchemaApplication.ApplyAsync(target, CreateSchemaExecutor());
        return CreateFixture(manifest, target, Assert.Single(target.Routes), surface);
    }

    protected Task<DocumentIdentityNativePlanEvidence> ExplainQueryAsync(
        RelationalPhysicalDocumentStore documents,
        StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentQuery query) =>
        ExplainAsync(
            route,
            RelationalPhysicalQueryRuntime.BuildCountCommand(
                documents,
                manifest,
                route,
                target.Provider,
                DialectName,
                query.Select(BoundedQueryResultOperation.Count)));

    protected Task<DocumentIdentityNativePlanEvidence> ExplainMutationAsync(
        RelationalPhysicalDocumentStore documents,
        StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentMutation mutation) =>
        ExplainAsync(
            route,
            RelationalPhysicalMutationRuntime.BuildSelectionCommand(
                new RelationalPhysicalMutationRuntimeContext(
                    documents,
                    manifest,
                    route,
                    target.Provider,
                    Provider.Name,
                    DialectName),
                mutation));

    private async Task<DocumentIdentityNativePlanEvidence> ExplainAsync(
        ExecutableStorageRoute route,
        RelationalPhysicalQueryCommand command)
    {
        var plan = await ExplainNativeAsync(command, route);
        var index = route.Indexes.Single(candidate => candidate.Identity.StartsWith(
            DocumentIdentityAcceptanceModel.ExactIndexIdentity,
            StringComparison.Ordinal));
        var lookup = route.Envelope.Identity.LookupKey.Identifier;
        var comparison = route.Envelope.Identity.ComparisonKey.Identifier;
        return new DocumentIdentityNativePlanEvidence(
            plan.Contains(index.Name.Identifier, StringComparison.Ordinal),
            plan.Contains("Seq Scan", StringComparison.Ordinal) ||
            plan.Contains("Table Scan", StringComparison.Ordinal) ||
            DialectName == "sqlserver" && plan.Contains("Index Scan", StringComparison.Ordinal) ||
            plan.Contains("COLLSCAN", StringComparison.Ordinal),
            command.CommandText.Contains(lookup, StringComparison.Ordinal),
            command.CommandText.Contains(comparison, StringComparison.Ordinal),
            index.Columns.Any(column => column.Column.Identifier == lookup) &&
            index.Columns.Any(column => column.Column.Identifier == comparison),
            $"SQL:{Environment.NewLine}{command.CommandText}{Environment.NewLine}PLAN:{Environment.NewLine}{plan}");
    }
}

public sealed class PostgreSqlDocumentIdentityAcceptanceTests(PostgreSqlPhysicalStorageContainer fixture)
    : RelationalDocumentIdentityAcceptanceConformance, IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    protected override ProviderIdentity Provider => PostgreSqlGroundworkCapabilities.Provider;
    protected override IProviderPhysicalNameNormalizer PhysicalNames => PostgreSqlGroundworkCapabilities.PhysicalNames;
    protected override string DialectName => "postgresql";
    protected override IPhysicalSchemaExecutor CreateSchemaExecutor() =>
        new PostgreSqlPhysicalSchemaExecutor(fixture.Container.GetConnectionString());

    protected override DocumentIdentityAcceptanceFixture CreateFixture(
        StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentIdentityAcceptanceSurface surface)
    {
        var documents = new PostgreSqlPhysicalDocumentStore(
            fixture.Container.GetConnectionString(),
            manifest,
            target.Routes,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        return new DocumentIdentityAcceptanceFixture(
            documents,
            PostgreSqlPhysicalQueryRuntime.Create(documents, manifest, route, target.Provider),
            route,
            () => ValueTask.CompletedTask,
            surface == DocumentIdentityAcceptanceSurface.Mutation
                ? PostgreSqlPhysicalMutationRuntime.Create(documents, manifest, route, target.Provider)
                : null,
            query => ExplainQueryAsync(documents, manifest, target, route, query),
            mutation => ExplainMutationAsync(documents, manifest, target, route, mutation));
    }

    internal override async Task<string> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route)
    {
        await SeedPlanNoiseAsync(route);
        await using var connection = new NpgsqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        await using (var analyze = connection.CreateCommand())
        {
            analyze.CommandText = $"ANALYZE {Quote(route.PrimaryStorage.Name.Identifier)};";
            await analyze.ExecuteNonQueryAsync();
        }
        await using var explain = connection.CreateCommand();
        explain.CommandText = $"EXPLAIN (FORMAT JSON) {command.CommandText}";
        foreach (var (name, value) in command.Parameters)
            explain.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
    }

    private async Task SeedPlanNoiseAsync(ExecutableStorageRoute route)
    {
        await using var connection = new NpgsqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        var table = route.PrimaryStorage.Name.Identifier;
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table AND is_generated = 'NEVER'
                ORDER BY ordinal_position;
                """;
            metadata.Parameters.AddWithValue("table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        var identity = route.Envelope.Identity;
        var identityColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            route.Envelope.Id.Identifier,
            identity.LookupKey.Identifier,
            identity.ComparisonKey.Identifier
        };
        await using var seed = connection.CreateCommand();
        seed.CommandText = $"""
            WITH source AS (SELECT * FROM {Quote(table)} LIMIT 1)
            INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))})
            SELECT {string.Join(", ", columns.Select(column => identityColumns.Contains(column)
                ? $"s.{Quote(column)} || '-noise-' || n::text"
                : $"s.{Quote(column)}"))}
            FROM source s CROSS JOIN generate_series(1, 4096) AS n;
            """;
        await seed.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

public sealed class SqlServerDocumentIdentityAcceptanceTests(SqlServerPhysicalStorageContainer fixture)
    : RelationalDocumentIdentityAcceptanceConformance, IClassFixture<SqlServerPhysicalStorageContainer>
{
    protected override ProviderIdentity Provider => SqlServerGroundworkCapabilities.Provider;
    protected override IProviderPhysicalNameNormalizer PhysicalNames => SqlServerGroundworkCapabilities.PhysicalNames;
    protected override string DialectName => "sqlserver";
    protected override IPhysicalSchemaExecutor CreateSchemaExecutor() =>
        new SqlServerPhysicalSchemaExecutor(fixture.Container.GetConnectionString());

    protected override DocumentIdentityAcceptanceFixture CreateFixture(
        StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentIdentityAcceptanceSurface surface)
    {
        var documents = new SqlServerPhysicalDocumentStore(
            fixture.Container.GetConnectionString(),
            manifest,
            target.Routes,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        return new DocumentIdentityAcceptanceFixture(
            documents,
            SqlServerPhysicalQueryRuntime.Create(documents, manifest, route, target.Provider),
            route,
            () => ValueTask.CompletedTask,
            surface == DocumentIdentityAcceptanceSurface.Mutation
                ? SqlServerPhysicalMutationRuntime.Create(documents, manifest, route, target.Provider)
                : null,
            query => ExplainQueryAsync(documents, manifest, target, route, query),
            mutation => ExplainMutationAsync(documents, manifest, target, route, mutation));
    }

    internal override async Task<string> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route)
    {
        await SeedPlanNoiseAsync(route);
        await using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = $"UPDATE STATISTICS {Quote(route.PrimaryStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
        await using (var enable = connection.CreateCommand())
        {
            enable.CommandText = "SET STATISTICS XML ON;";
            await enable.ExecuteNonQueryAsync();
        }
        await using var native = connection.CreateCommand();
        native.CommandText = command.CommandText;
        foreach (var (name, value) in command.Parameters)
            native.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var plans = new List<string>();
        await using var reader = await native.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) ||
                        reader.GetFieldType(ordinal) == typeof(System.Data.SqlTypes.SqlXml))
                    {
                        plans.Add(reader.GetValue(ordinal).ToString() ?? string.Empty);
                    }
                }
            }
        } while (await reader.NextResultAsync());
        return Assert.Single(plans);
    }

    private async Task SeedPlanNoiseAsync(ExecutableStorageRoute route)
    {
        await SqlServerDocumentIdentityNoiseSeeder.SeedAsync(
            fixture.Container.GetConnectionString(),
            route.Envelope.Identity,
            route.PrimaryStorage.Name.Identifier);
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
