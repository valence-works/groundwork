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
                mutation => ExplainMutationAsync(connection, documents, manifest, target, route, mutation));
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
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));
        var plan = string.Join(Environment.NewLine, details);
        var index = route.Indexes.Single(candidate => candidate.Identity.StartsWith(
            DocumentIdentityAcceptanceModel.ExactIndexIdentity,
            StringComparison.Ordinal));
        var lookup = route.Envelope.Identity.LookupKey.Identifier;
        var comparison = route.Envelope.Identity.ComparisonKey.Identifier;
        return new DocumentIdentityNativePlanEvidence(
            plan.Contains(index.Name.Identifier, StringComparison.Ordinal),
            plan.Contains("SCAN", StringComparison.OrdinalIgnoreCase),
            command.CommandText.Contains(lookup, StringComparison.Ordinal),
            command.CommandText.Contains(comparison, StringComparison.Ordinal),
            index.Columns.Any(column => column.Column.Identifier == lookup) &&
            index.Columns.Any(column => column.Column.Identifier == comparison),
            $"SQL:{Environment.NewLine}{command.CommandText}{Environment.NewLine}PLAN:{Environment.NewLine}{plan}");
    }
}
