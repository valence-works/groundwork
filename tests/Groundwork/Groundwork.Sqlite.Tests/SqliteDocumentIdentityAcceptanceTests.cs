using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

internal static class SqliteIdentityPlanInspector
{
    public static IReadOnlyList<DocumentIdentityAccessPath> Parse(IReadOnlyList<string> details) => details
        .Select(detail =>
        {
            var indexName = IndexName(detail, "USING COVERING INDEX ") ??
                IndexName(detail, "USING INDEX ");
            var fullScan = detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase);
            return indexName is null && !fullScan
                ? null
                : new DocumentIdentityAccessPath(indexName, fullScan);
        })
        .Where(path => path is not null)
        .Cast<DocumentIdentityAccessPath>()
        .ToArray();

    private static string? IndexName(string detail, string marker)
    {
        var start = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = detail.IndexOfAny([' ', '('], start);
        return end < 0 ? detail[start..] : detail[start..end];
    }
}

internal static class SqliteIdentityIndexCatalog
{
    public static async Task<IReadOnlyList<DocumentIdentityMaterializedIndex>> ReadAsync(
        SqliteConnection connection,
        string tableName,
        string indexName)
    {
        bool? isPartial = null;
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list({Quote(tableName)});";
            await using var reader = await list.ExecuteReaderAsync();
            var nameOrdinal = reader.GetOrdinal("name");
            var partialOrdinal = reader.GetOrdinal("partial");
            while (await reader.ReadAsync())
            {
                if (!reader.GetString(nameOrdinal).Equals(indexName, StringComparison.Ordinal))
                    continue;
                isPartial = reader.GetInt64(partialOrdinal) != 0;
                break;
            }
        }
        if (isPartial is null)
            return [];

        await using var details = connection.CreateCommand();
        details.CommandText = $"PRAGMA index_xinfo({Quote(indexName)});";
        var fields = new List<(long Ordinal, string Name)>();
        await using (var reader = await details.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (reader.GetInt64(5) != 1 || reader.IsDBNull(2))
                    continue;
                fields.Add((reader.GetInt64(0), reader.GetString(2)));
            }
        }
        return
        [
            new DocumentIdentityMaterializedIndex(
                indexName,
                fields.OrderBy(field => field.Ordinal).Select(field => field.Name).ToArray(),
                IsPartial: isPartial.Value)
        ];
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

public sealed class SqliteDocumentIdentityAcceptanceEvidenceTests
{
    [Fact]
    public void Plan_detail_text_cannot_impersonate_a_structured_index_access_path()
    {
        var paths = SqliteIdentityPlanInspector.Parse(["SCAN documents /* ix_identity */"]);

        Assert.Single(paths);
        Assert.True(paths[0].IsFullScan);
        Assert.Null(paths[0].IndexName);
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

    [Fact]
    public async Task Partial_sqlite_index_does_not_cover_the_selector()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE documents (
                    storage_scope TEXT NOT NULL,
                    id_lookup_key TEXT NOT NULL,
                    id_comparison_key TEXT NOT NULL,
                    status TEXT NOT NULL);
                CREATE INDEX ix_identity
                    ON documents (storage_scope, id_lookup_key, id_comparison_key)
                    WHERE status = 'pending';
                """;
            await create.ExecuteNonQueryAsync();
        }

        var indexes = await SqliteIdentityIndexCatalog.ReadAsync(connection, "documents", "ix_identity");
        var evidence = DocumentIdentityNativePlanEvidence.Create(
            ExpectedIndex(),
            [new DocumentIdentityAccessPath("ix_identity", false)],
            indexes,
            ["id_lookup_key", "id_comparison_key"],
            "partial SQLite catalog index");

        Assert.True(Assert.Single(indexes).IsPartial);
        Assert.False(evidence.IndexCoversSelectorFields);
    }

    private static DocumentIdentityExpectedIndex ExpectedIndex() => new(
        "ix_identity",
        ["id_lookup_key", "id_comparison_key"],
        ["storage_scope", "id_lookup_key", "id_comparison_key"]);
}

public sealed class SqliteDocumentIdentityAcceptanceTests : DocumentIdentityAcceptanceConformance
{
    protected override async Task<DocumentIdentityAcceptanceFixture> CreateIdentityFixtureAsync(
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        Groundwork.Core.Manifests.StringIdentityCasePolicy stringCasePolicy =
            Groundwork.Core.Manifests.StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        DocumentIdentityAcceptanceSurface surface = DocumentIdentityAcceptanceSurface.Exact)
    {
        var manifest = DocumentIdentityAcceptanceModel.Manifest(
            form,
            stringCasePolicy,
            surface,
            Guid.NewGuid().ToString("N")[..8]);
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        var target = new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            SqliteGroundworkCapabilities.Provider,
            compilation.Routes);
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
            var route = Assert.Single(target.Routes);
            var documents = new SqlitePhysicalDocumentStore(
                connection,
                manifest,
                target.Routes,
                DocumentStoreAccess.Scoped(new("tenant-a")));
            var queries = SqlitePhysicalQueryRuntime.Create(
                documents,
                manifest,
                route,
                target.Provider);
            var mutations = surface == DocumentIdentityAcceptanceSurface.Mutation
                ? SqlitePhysicalMutationRuntime.Create(documents, manifest, route, target.Provider)
                : null;
            return new DocumentIdentityAcceptanceFixture(
                documents,
                queries,
                route,
                async () => await connection.DisposeAsync(),
                mutations,
                query => ExplainQueryAsync(connection, documents, manifest, target, route, query),
                mutation => ExplainMutationAsync(connection, documents, manifest, target, route, mutation),
                observer => RelationalPhysicalMutationRuntime.CreateWithSelectionObserver(
                    new RelationalPhysicalMutationRuntimeContext(
                        documents,
                        manifest,
                        route,
                        target.Provider,
                        SqliteGroundworkCapabilities.Provider.Name,
                        "sqlite"),
                    (identity, command) =>
                    {
                        observer(identity, command.CommandText);
                        return ValueTask.CompletedTask;
                    }));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static Task<DocumentIdentityNativePlanEvidence> ExplainQueryAsync(
        SqliteConnection connection,
        SqlitePhysicalDocumentStore documents,
        Groundwork.Core.Manifests.StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentQuery query)
    {
        var command = RelationalPhysicalQueryRuntime.BuildCountCommand(
            documents,
            manifest,
            route,
            target.Provider,
            "sqlite",
            query.Select(BoundedQueryResultOperation.Count));
        return ExplainAsync(connection, route, command);
    }

    private static Task<DocumentIdentityNativePlanEvidence> ExplainMutationAsync(
        SqliteConnection connection,
        SqlitePhysicalDocumentStore documents,
        Groundwork.Core.Manifests.StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        DocumentMutation mutation)
    {
        var command = RelationalPhysicalMutationRuntime.BuildSelectionCommand(
            new RelationalPhysicalMutationRuntimeContext(
                documents,
                manifest,
                route,
                target.Provider,
                SqliteGroundworkCapabilities.Provider.Name,
                "sqlite"),
            mutation);
        return ExplainAsync(connection, route, command);
    }

    private static async Task<DocumentIdentityNativePlanEvidence> ExplainAsync(
        SqliteConnection connection,
        ExecutableStorageRoute route,
        RelationalPhysicalQueryCommand command)
    {
        await using var explain = connection.CreateCommand();
        explain.CommandText = $"EXPLAIN QUERY PLAN {command.CommandText};";
        foreach (var (name, value) in command.Parameters)
            explain.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var details = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                details.Add(reader.GetString(3));
        }
        var plan = string.Join(Environment.NewLine, details);
        var expectedIndex = DocumentIdentityAcceptanceModel.ExactIndex(route);
        var materializedIndexes = await SqliteIdentityIndexCatalog.ReadAsync(
            connection,
            route.PrimaryStorage.Name.Identifier,
            expectedIndex.Name);
        var selectorFields = expectedIndex.SelectorFieldsBoundBy(command.PredicateFieldIdentifiers);
        return DocumentIdentityNativePlanEvidence.Create(
            expectedIndex,
            SqliteIdentityPlanInspector.Parse(details),
            materializedIndexes,
            selectorFields,
            $"SQL:{Environment.NewLine}{command.CommandText}{Environment.NewLine}PLAN:{Environment.NewLine}{plan}");
    }

}
