using System.Text.Json;
using System.Xml.Linq;
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

internal sealed record RelationalIdentityNativeExplain(
    string Plan,
    IReadOnlyList<DocumentIdentityAccessPath> AccessPaths,
    IReadOnlyList<DocumentIdentityMaterializedIndex> MaterializedIndexes);

internal static class RelationalIdentityPlanInspector
{
    public static IReadOnlyList<DocumentIdentityAccessPath> PostgreSql(string explain)
    {
        using var document = JsonDocument.Parse(explain);
        var plan = document.RootElement[0].GetProperty("Plan");
        var paths = new List<DocumentIdentityAccessPath>();
        VisitPostgreSql(plan, paths);
        return paths;
    }

    public static IReadOnlyList<DocumentIdentityAccessPath> SqlServer(string explain)
    {
        var document = XDocument.Parse(explain);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Select(element =>
            {
                var operation = element.Attribute("PhysicalOp")?.Value;
                var isFullScan = operation is "Table Scan" or "Index Scan" or "Clustered Index Scan";
                var isIndexAccess = operation is "Index Seek" or "Clustered Index Seek";
                if (!isFullScan && !isIndexAccess)
                    return null;
                var index = element.Descendants()
                    .FirstOrDefault(candidate => candidate.Name.LocalName == "Object")?
                    .Attribute("Index")?.Value;
                return new DocumentIdentityAccessPath(
                    isIndexAccess || operation is "Index Scan" or "Clustered Index Scan"
                        ? UnquoteSqlServerIdentifier(index)
                        : null,
                    isFullScan);
            })
            .Where(path => path is not null)
            .Cast<DocumentIdentityAccessPath>()
            .ToArray();
    }

    private static void VisitPostgreSql(
        JsonElement plan,
        ICollection<DocumentIdentityAccessPath> paths)
    {
        var nodeType = plan.GetProperty("Node Type").GetString();
        if (nodeType == "Seq Scan")
        {
            paths.Add(new DocumentIdentityAccessPath(null, true));
        }
        else if (nodeType is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan")
        {
            paths.Add(new DocumentIdentityAccessPath(
                plan.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
                false));
        }
        if (!plan.TryGetProperty("Plans", out var children))
            return;
        foreach (var child in children.EnumerateArray())
            VisitPostgreSql(child, paths);
    }

    private static string? UnquoteSqlServerIdentifier(string? identifier)
    {
        if (identifier is null || identifier.Length < 2 || identifier[0] != '[' || identifier[^1] != ']')
            return identifier;
        return identifier[1..^1].Replace("]]", "]", StringComparison.Ordinal);
    }
}

public sealed class RelationalDocumentIdentityAcceptanceEvidenceTests
{
    [Fact]
    public void Same_name_index_with_wrong_catalog_shape_does_not_cover_the_selector()
    {
        var evidence = DocumentIdentityNativePlanEvidence.Create(
            ExpectedIndex(),
            [new DocumentIdentityAccessPath("ix_identity", false)],
            [new DocumentIdentityMaterializedIndex(
                "ix_identity",
                ["storage_scope", "id_comparison_key", "id_lookup_key"],
                IsValid: true,
                IsReady: true,
                IsUnfiltered: true)],
            ["id_lookup_key", "id_comparison_key"],
            "same name, wrong ordered key shape");

        Assert.True(evidence.UsesExpectedIndex);
        Assert.False(evidence.IndexCoversSelectorFields);
    }

    [Fact]
    public void Same_name_index_with_an_extra_leading_key_does_not_cover_the_selector()
    {
        var evidence = DocumentIdentityNativePlanEvidence.Create(
            ExpectedIndex(),
            [new DocumentIdentityAccessPath("ix_identity", false)],
            [new DocumentIdentityMaterializedIndex(
                "ix_identity",
                ["decoy", "storage_scope", "id_lookup_key", "id_comparison_key"])],
            ["id_lookup_key", "id_comparison_key"],
            "same name, extra leading key");

        Assert.True(evidence.UsesExpectedIndex);
        Assert.False(evidence.IndexCoversSelectorFields);
    }

    [Fact]
    public void Projected_selector_fields_do_not_count_as_predicate_bound_fields()
    {
        var command = new RelationalPhysicalQueryCommand(
            "SELECT id_lookup_key, id_comparison_key FROM documents WHERE status = @status",
            [("status", "pending")],
            ["status"]);
        var expected = ExpectedIndex();
        var evidence = DocumentIdentityNativePlanEvidence.Create(
            expected,
            [new DocumentIdentityAccessPath("ix_identity", false)],
            [new DocumentIdentityMaterializedIndex(
                "ix_identity",
                ["storage_scope", "id_lookup_key", "id_comparison_key"])],
            expected.SelectorFieldsBoundBy(command.PredicateFieldIdentifiers),
            command.CommandText);

        Assert.False(evidence.SelectorUsesLookupKey);
        Assert.False(evidence.SelectorUsesComparisonKey);
    }

    [Theory]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, true, true, true)]
    public void Unusable_catalog_index_does_not_cover_the_selector(
        bool isValid,
        bool isReady,
        bool isUnfiltered,
        bool isEnabled,
        bool isHypothetical)
    {
        var evidence = DocumentIdentityNativePlanEvidence.Create(
            ExpectedIndex(),
            [new DocumentIdentityAccessPath("ix_identity", false)],
            [new DocumentIdentityMaterializedIndex(
                "ix_identity",
                ["storage_scope", "id_lookup_key", "id_comparison_key"],
                isValid,
                isReady,
                isUnfiltered,
                isEnabled,
                isHypothetical)],
            ["id_lookup_key", "id_comparison_key"],
            "unusable catalog index");

        Assert.False(evidence.IndexCoversSelectorFields);
    }

    private static DocumentIdentityExpectedIndex ExpectedIndex() => new(
        "ix_identity",
        ["id_lookup_key", "id_comparison_key"],
        ["storage_scope", "id_lookup_key", "id_comparison_key"]);

    [Fact]
    public void Postgre_sql_plan_text_cannot_impersonate_a_winning_index_access_path()
    {
        const string explain =
            "[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"documents\",\"Filter\":\"ix_identity = id_lookup_key\"}}]";

        var paths = RelationalIdentityPlanInspector.PostgreSql(explain);

        Assert.Single(paths);
        Assert.True(paths[0].IsFullScan);
        Assert.Null(paths[0].IndexName);
    }

    [Fact]
    public void Sql_server_statement_text_cannot_impersonate_a_winning_index_access_path()
    {
        const string explain = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT ix_identity">
                <QueryPlan><RelOp PhysicalOp="Table Scan"><TableScan><Object Table="[documents]" /></TableScan></RelOp></QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        var paths = RelationalIdentityPlanInspector.SqlServer(explain);

        Assert.Single(paths);
        Assert.True(paths[0].IsFullScan);
        Assert.Null(paths[0].IndexName);
    }
}

public abstract class RelationalDocumentIdentityAcceptanceConformance : DocumentIdentityAcceptanceConformance
{
    protected abstract ProviderIdentity Provider { get; }
    protected abstract IProviderPhysicalNameNormalizer PhysicalNames { get; }
    protected abstract string DialectName { get; }
    protected abstract IPhysicalSchemaExecutor CreateSchemaExecutor();
    internal abstract Task<RelationalIdentityNativeExplain> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route,
        DocumentIdentityExpectedIndex expectedIndex);
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
        var expectedIndex = DocumentIdentityAcceptanceModel.ExactIndex(route);
        var native = await ExplainNativeAsync(command, route, expectedIndex);
        var selectorFields = expectedIndex.SelectorFieldsBoundBy(command.PredicateFieldIdentifiers);
        return DocumentIdentityNativePlanEvidence.Create(
            expectedIndex,
            native.AccessPaths,
            native.MaterializedIndexes,
            selectorFields,
            $"SQL:{Environment.NewLine}{command.CommandText}{Environment.NewLine}PLAN:{Environment.NewLine}{native.Plan}");
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

    protected override async Task PrepareMutationNativeEvidenceAsync(DocumentIdentityAcceptanceFixture identityFixture)
    {
        await SeedPlanNoiseAsync(identityFixture.Route);
        await using var connection = new NpgsqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        await using var analyze = connection.CreateCommand();
        analyze.CommandText = $"ANALYZE {Quote(identityFixture.Route.PrimaryStorage.Name.Identifier)};";
        await analyze.ExecuteNonQueryAsync();
    }

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
            mutation => ExplainMutationAsync(documents, manifest, target, route, mutation),
            observer => RelationalPhysicalMutationRuntime.CreateWithSelectionObserver(
                new RelationalPhysicalMutationRuntimeContext(
                    documents,
                    manifest,
                    route,
                    target.Provider,
                    PostgreSqlGroundworkCapabilities.Provider.Name,
                    "postgresql"),
                (identity, command) =>
                {
                    observer(identity, command.CommandText);
                    return ValueTask.CompletedTask;
                }));
    }

    internal override async Task<RelationalIdentityNativeExplain> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route,
        DocumentIdentityExpectedIndex expectedIndex)
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
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));
        }
        var plan = string.Join(Environment.NewLine, lines);
        var materializedIndexes = await ReadIndexAsync(connection, route, expectedIndex.Name);
        return new RelationalIdentityNativeExplain(
            plan,
            RelationalIdentityPlanInspector.PostgreSql(plan),
            materializedIndexes);
    }

    private static async Task<IReadOnlyList<DocumentIdentityMaterializedIndex>> ReadIndexAsync(
        NpgsqlConnection connection,
        ExecutableStorageRoute route,
        string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.indisvalid,
                   i.indisready,
                   i.indpred IS NULL AS is_unfiltered,
                   keys.ordinality,
                   attributes.attname
            FROM pg_catalog.pg_index AS i
            INNER JOIN pg_catalog.pg_class AS indexes ON indexes.oid = i.indexrelid
            INNER JOIN pg_catalog.pg_class AS tables ON tables.oid = i.indrelid
            INNER JOIN pg_catalog.pg_namespace AS schemas ON schemas.oid = tables.relnamespace
            INNER JOIN LATERAL unnest(i.indkey::smallint[]) WITH ORDINALITY AS keys(attnum, ordinality)
                ON keys.ordinality <= i.indnkeyatts
            LEFT JOIN pg_catalog.pg_attribute AS attributes
                ON attributes.attrelid = tables.oid AND attributes.attnum = keys.attnum
            WHERE schemas.nspname = current_schema()
              AND tables.relname = @table
              AND indexes.relname = @index
            ORDER BY keys.ordinality;
            """;
        command.Parameters.AddWithValue("table", route.PrimaryStorage.Name.Identifier);
        command.Parameters.AddWithValue("index", indexName);
        var fields = new List<string>();
        bool? isValid = null;
        bool? isReady = null;
        bool? isUnfiltered = null;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            isValid ??= reader.GetBoolean(0);
            isReady ??= reader.GetBoolean(1);
            isUnfiltered ??= reader.GetBoolean(2);
            fields.Add(reader.IsDBNull(4) ? "<expression>" : reader.GetString(4));
        }
        return isValid is null
            ? []
            :
            [
                new DocumentIdentityMaterializedIndex(
                    indexName,
                    fields,
                    isValid.Value,
                    isReady!.Value,
                    isUnfiltered!.Value)
            ];
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

    protected override async Task PrepareMutationNativeEvidenceAsync(DocumentIdentityAcceptanceFixture identityFixture)
    {
        await SeedPlanNoiseAsync(identityFixture.Route);
        await using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        await using var statistics = connection.CreateCommand();
        statistics.CommandText = $"UPDATE STATISTICS {Quote(identityFixture.Route.PrimaryStorage.Name.Identifier)};";
        await statistics.ExecuteNonQueryAsync();
    }

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
            mutation => ExplainMutationAsync(documents, manifest, target, route, mutation),
            observer => RelationalPhysicalMutationRuntime.CreateWithSelectionObserver(
                new RelationalPhysicalMutationRuntimeContext(
                    documents,
                    manifest,
                    route,
                    target.Provider,
                    SqlServerGroundworkCapabilities.Provider.Name,
                    "sqlserver"),
                (identity, command) =>
                {
                    observer(identity, command.CommandText);
                    return ValueTask.CompletedTask;
                }));
    }

    internal override async Task<RelationalIdentityNativeExplain> ExplainNativeAsync(
        RelationalPhysicalQueryCommand command,
        ExecutableStorageRoute route,
        DocumentIdentityExpectedIndex expectedIndex)
    {
        await SeedPlanNoiseAsync(route);
        await using var connection = new SqlConnection(fixture.Container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = $"UPDATE STATISTICS {Quote(route.PrimaryStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
        var materializedIndexes = await ReadIndexAsync(connection, route, expectedIndex.Name);
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
        var plan = Assert.Single(plans);
        return new RelationalIdentityNativeExplain(
            plan,
            RelationalIdentityPlanInspector.SqlServer(plan),
            materializedIndexes);
    }

    private static async Task<IReadOnlyList<DocumentIdentityMaterializedIndex>> ReadIndexAsync(
        SqlConnection connection,
        ExecutableStorageRoute route,
        string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexes.is_disabled,
                   indexes.has_filter,
                   indexes.is_hypothetical,
                   index_columns.key_ordinal,
                   columns.name
            FROM sys.indexes AS indexes
            INNER JOIN sys.index_columns AS index_columns
                ON index_columns.object_id = indexes.object_id
               AND index_columns.index_id = indexes.index_id
               AND index_columns.key_ordinal > 0
            INNER JOIN sys.columns AS columns
                ON columns.object_id = index_columns.object_id
               AND columns.column_id = index_columns.column_id
            WHERE indexes.object_id = OBJECT_ID(@table)
              AND indexes.name = @index
            ORDER BY index_columns.key_ordinal;
            """;
        command.Parameters.AddWithValue("@table", route.PrimaryStorage.Name.Identifier);
        command.Parameters.AddWithValue("@index", indexName);
        var fields = new List<string>();
        bool? isDisabled = null;
        bool? hasFilter = null;
        bool? isHypothetical = null;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            isDisabled ??= reader.GetBoolean(0);
            hasFilter ??= reader.GetBoolean(1);
            isHypothetical ??= reader.GetBoolean(2);
            fields.Add(reader.GetString(4));
        }
        return isDisabled is null
            ? []
            :
            [
                new DocumentIdentityMaterializedIndex(
                    indexName,
                    fields,
                    IsUnfiltered: !hasFilter!.Value,
                    IsEnabled: !isDisabled.Value,
                    IsHypothetical: isHypothetical!.Value)
            ];
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
