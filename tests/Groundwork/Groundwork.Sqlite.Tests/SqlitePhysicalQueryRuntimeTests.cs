using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqlitePhysicalQueryRuntimeTests
{
    [Theory]
    [InlineData(BoundedQueryResultOperation.Documents, 1, "linked-identity-collision-check", "count", "page")]
    [InlineData(BoundedQueryResultOperation.Documents, 0, "linked-identity-collision-check", "count", null)]
    [InlineData(BoundedQueryResultOperation.Count, 1, "linked-identity-collision-check", "count", null)]
    [InlineData(BoundedQueryResultOperation.First, 1, "linked-identity-collision-check", "first", null)]
    [InlineData(BoundedQueryResultOperation.Any, 1, "linked-identity-collision-check", "any", null)]
    public async Task Public_explain_returns_every_exact_production_command_in_terminal_operation_order(
        BoundedQueryResultOperation operation,
        int take,
        string firstCommand,
        string secondCommand,
        string? thirdCommand)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("a", "tools"));
        await writer.SaveAsync(Save("b", "other"));
        var runtime = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var explainer = Assert.IsAssignableFrom<IPhysicalDocumentQueryExplainer>(runtime);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            take: take,
            resultOperation: operation);

        var explanation = await explainer.ExplainAsync(query);

        Assert.Equal(
            new[] { firstCommand, secondCommand, thirdCommand }.Where(identity => identity is not null),
            explanation.Commands.Select(command => command.Identity));
        Assert.Equal(
            PhysicalDocumentQueryInvocationFingerprint.Compute(query, explanation.Plan, DocumentScopeSelection.Global),
            explanation.RuntimeInvocationFingerprint);
        Assert.Equal(route.Indexes.Single(index => index.Identity == "by-category").Name, explanation.Plan.IndexName);
        Assert.All(explanation.Commands, command =>
        {
            Assert.Equal("sqlite-query-plan", command.NativePlanFormat);
            Assert.Contains("SEARCH", command.NativePlan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SCAN", command.NativePlan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(explanation.Plan.Discriminator.Identifier, command.PredicateFieldIdentifiers);
            Assert.Contains(Assert.Single(explanation.Plan.Predicates).Field.Identifier, command.PredicateFieldIdentifiers);
        });
        Assert.Contains(explanation.Commands, command =>
            command.NativePlan.Contains(explanation.Plan.IndexName!.Identifier, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exact_identity_query_binds_equivalent_unicode_spelling_to_the_same_plan_evidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateIdentityModel();
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        var lower = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store,
            manifest,
            route,
            target.Provider,
            "sqlite",
            IdentityQuery("metric-\U00010428-\u00e9"));
        var upper = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store,
            manifest,
            route,
            target.Provider,
            "sqlite",
            IdentityQuery("METRIC-\U00010400-\u00c9"));

        var lowerEvidence = lower.Parameters.Where(parameter => parameter.Name.StartsWith('q')).ToArray();
        var upperEvidence = upper.Parameters.Where(parameter => parameter.Name.StartsWith('q')).ToArray();
        Assert.Equal(lowerEvidence, upperEvidence);
        Assert.Equal(
            [
                "61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181",
                "00004D00004500005400005200004900004300002D01040000002D0000C9"
            ],
            lowerEvidence.Select(parameter => parameter.Value));
    }

    [Theory]
    [InlineData(PortableQueryOperation.GreaterThan)]
    [InlineData(PortableQueryOperation.StartsWith)]
    public async Task Ordered_identity_query_binds_equivalent_unicode_spelling_to_the_same_comparison_key(
        PortableQueryOperation operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateIdentityModel(operation);
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var lower = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store, manifest, route, target.Provider, "sqlite",
            IdentityQuery("metric-\U00010428-\u00e9", operation));
        var upper = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store, manifest, route, target.Provider, "sqlite",
            IdentityQuery("METRIC-\U00010400-\u00c9", operation));

        var lowerEvidence = lower.Parameters.Where(parameter => parameter.Name.StartsWith('q')).ToArray();
        var upperEvidence = upper.Parameters.Where(parameter => parameter.Name.StartsWith('q')).ToArray();
        Assert.Equal(lowerEvidence, upperEvidence);
        const string comparisonKey = "00004D00004500005400005200004900004300002D01040000002D0000C9";
        Assert.Equal(comparisonKey, Assert.IsType<string>(lowerEvidence[0].Value));
        if (operation == PortableQueryOperation.StartsWith)
        {
            Assert.Equal(2, lowerEvidence.Length);
            Assert.Equal(
                comparisonKey[..^1] + (char)(comparisonKey[^1] + 1),
                Assert.IsType<string>(lowerEvidence[1].Value));
        }
        else
            Assert.Single(lowerEvidence);
    }

    [Fact]
    public async Task Linked_index_count_command_does_not_join_the_primary_envelope_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            resultOperation: BoundedQueryResultOperation.Count);

        var rendered = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store,
            manifest,
            route,
            target.Provider,
            "sqlite",
            query);

        Assert.Contains(route.LinkedIndexStorage!.Name.Identifier, rendered.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(route.PrimaryStorage.Name.Identifier, rendered.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task CertifiedPlansExecuteFilterCountOrderAndPageOnTheSelectedRoute(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("b", "tools"));
        await writer.SaveAsync(Save("a", "tools"));
        await writer.SaveAsync(Save("c", "other"));
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var predicate = DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"));
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [predicate],
            [new DocumentQueryOrder("category")],
            skip: 0,
            take: 1);

        var page = await queries.QueryAsync(query);
        var count = await queries.CountAsync(query.Select(BoundedQueryResultOperation.Count));
        var any = await queries.AnyAsync(query.Select(BoundedQueryResultOperation.Any));
        var first = await queries.FirstOrDefaultAsync(query.Select(BoundedQueryResultOperation.First));
        var compoundCount = await queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", "1"))
            ],
            resultOperation: BoundedQueryResultOperation.Count));

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Documents);
        Assert.Equal("a", page.Documents[0].Id);
        Assert.Equal(2, count);
        Assert.True(any);
        Assert.Equal("a", first!.Id);
        Assert.Equal(2, compoundCount);
        Assert.Contains(route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier,
            await ExplainCategoryLookupAsync(connection, route));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Latest_per_key_filters_before_grouping_and_pages_deterministic_representatives(
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            includeLatestPerCategory: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("alpha-low", "alpha", 1, true));
        await writer.SaveAsync(Save("alpha-high", "alpha", 3, true));
        await writer.SaveAsync(Save("beta-tie-b", "beta", 2, true));
        await writer.SaveAsync(Save("beta-tie-a", "beta", 2, true));
        await writer.SaveAsync(Save("gamma-low", "gamma", 1, false));
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var query = new DocumentQuery(
            "configurationDocument",
            "latest-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("visible", "true"))],
            [new DocumentQueryOrder("category"), new DocumentQueryOrder("priority")],
            skip: 1,
            take: 1,
            latestPerKeyPath: "category");

        var page = await queries.QueryAsync(query);
        var count = await queries.CountAsync(query.Select(BoundedQueryResultOperation.Count));
        var first = await queries.FirstOrDefaultAsync(query.Page(0, 1).Select(BoundedQueryResultOperation.First));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("beta-tie-a", Assert.Single(page.Documents).Id);
        Assert.Equal(2, count);
        Assert.Equal("alpha-low", first!.Id);
    }

    [Fact]
    public async Task Cursor_pages_use_the_compiled_identity_tie_break_and_bind_the_token_to_the_query()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: false,
            categoryPaging: QueryPagingSupport.Cursor);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        var empty = await SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider).QueryAsync(
            new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "missing"))],
                [new DocumentQueryOrder("category")],
                take: 1));
        Assert.Empty(empty.Documents);
        Assert.Equal(0, empty.TotalCount);
        Assert.Null(empty.NextContinuation);

        await writer.SaveAsync(Save("c", "tools"));
        await writer.SaveAsync(Save("a", "tools"));
        await writer.SaveAsync(Save("b", "tools"));
        await writer.SaveAsync(Save("d", "tools"));
        await writer.SaveAsync(Save("e", "tools"));
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var predicate = DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"));
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [predicate],
            [new DocumentQueryOrder("category")],
            take: 1);

        var first = await queries.QueryAsync(query);
        var middle = await queries.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 2,
            continuation: first.NextContinuation));
        var final = await queries.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 10,
            continuation: middle.NextContinuation));
        var expected = new[] { "a", "b", "c", "d", "e" }
            .OrderBy(id => route.Envelope.Identity.Project(id).LookupKey, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(5, first.TotalCount);
        Assert.Equal(expected[0], Assert.Single(first.Documents).Id);
        Assert.NotNull(first.NextContinuation);
        Assert.Equal(expected[1..3], middle.Documents.Select(document => document.Id));
        Assert.NotNull(middle.NextContinuation);
        Assert.Equal(expected[3..], final.Documents.Select(document => document.Id));
        Assert.Null(final.NextContinuation);

        var otherQuery = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "other"))],
            [new DocumentQueryOrder("category")],
            take: 1,
            continuation: first.NextContinuation);
        await Assert.ThrowsAsync<InvalidDocumentQueryContinuationException>(() => queries.QueryAsync(otherQuery));
    }

    [Fact]
    public async Task ResidualPredicatesExecuteBeforeCountLimitAndCursorContinuation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateResidualHistoryModel();
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        await writer.SaveAsync(HistorySave("newest-blocked", "blocked", "2026-07-17T12:00:00Z"));
        await writer.SaveAsync(HistorySave("middle-ready", "ready", "2026-07-17T11:00:00Z"));
        await writer.SaveAsync(HistorySave("oldest-ready", "ready", "2026-07-17T10:00:00Z"));
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var ready = DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "ready"));
        var query = new DocumentQuery(
            "configurationDocument",
            "history-by-created-at",
            [ready],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 1);

        var first = await queries.QueryAsync(query);
        var second = await queries.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 1,
            continuation: first.NextContinuation));
        var count = await queries.CountAsync(query.Select(BoundedQueryResultOperation.Count));

        Assert.Equal(2, first.TotalCount);
        Assert.Equal("middle-ready", Assert.Single(first.Documents).Id);
        Assert.NotNull(first.NextContinuation);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal("oldest-ready", Assert.Single(second.Documents).Id);
        Assert.Null(second.NextContinuation);
        Assert.Equal(2, count);
        var plan = Assert.IsAssignableFrom<PhysicalQueryDocumentStore>(queries).ResolvePlan(query);
        Assert.True(plan.Predicates.Single(predicate => predicate.Path == "status").IsResidual);
        Assert.NotNull(plan.IndexName);
    }

    [Fact]
    public async Task Cursor_token_resumes_after_the_issuing_process_has_exited()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"groundwork-document-cursor-{Guid.NewGuid():N}.db");
        try
        {
            using var seed = JsonDocument.Parse(await RunCursorProbeAsync("seed", databasePath));
            var token = seed.RootElement.GetProperty("Continuation").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));

            using var resumed = JsonDocument.Parse(
                await RunCursorProbeAsync("resume", databasePath, token!));
            var allIds = seed.RootElement.GetProperty("Ids").EnumerateArray()
                .Concat(resumed.RootElement.GetProperty("Ids").EnumerateArray())
                .Select(element => element.GetString())
                .ToArray();

            Assert.Equal(3, resumed.RootElement.GetProperty("TotalCount").GetInt64());
            Assert.Equal(["a", "b", "c"], allIds.Order(StringComparer.Ordinal));
            Assert.Equal(3, allIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(JsonValueKind.Null, resumed.RootElement.GetProperty("Continuation").ValueKind);
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Invalid_cursor_token_is_rejected_before_the_provider_connection_opens()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: false,
            categoryPaging: QueryPagingSupport.Cursor);
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, route, target.Provider);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("category")],
            take: 1,
            continuation: "invalid");

        await Assert.ThrowsAsync<InvalidDocumentQueryContinuationException>(() => queries.QueryAsync(query));

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Cursor_pages_preserve_mixed_direction_compound_order_across_ties()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            compoundPaging: QueryPagingSupport.Cursor);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Global);
        foreach (var (id, priority) in new[] { ("a", 2), ("b", 3), ("c", 2), ("d", 1) })
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await store.SaveAsync(new SaveDocumentRequest(
                    "configurationDocument",
                    id,
                    "1",
                    $"{{\"category\":\"tools\",\"priority\":{priority}}}"))).Status);
        }
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, route, target.Provider);
        var query = new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            [new DocumentQueryOrder("priority", PhysicalSortDirection.Descending)],
            take: 2);

        var first = await queries.QueryAsync(query);
        var second = await queries.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 2,
            continuation: first.NextContinuation));
        var tied = new[] { "a", "c" }
            .OrderBy(id => route.Envelope.Identity.Project(id).LookupKey, StringComparer.Ordinal);

        Assert.Equal(new[] { "b" }.Concat(tied).Append("d"),
            first.Documents.Concat(second.Documents).Select(document => document.Id));
        Assert.NotNull(first.NextContinuation);
        Assert.Null(second.NextContinuation);
    }

    [Fact]
    public async Task LinkedQueryHydratesPrimaryThroughUnicodeEquivalentIdentityEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true,
            stringCasePolicy: Groundwork.Core.Manifests.StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("Configuration-One", "tools"));
        await using (var changeOriginalSpelling = connection.CreateCommand())
        {
            changeOriginalSpelling.CommandText =
                $"UPDATE \"{route.LinkedIndexStorage!.Name.Identifier}\" " +
                $"SET \"{route.LinkedRelationship!.DocumentId.Identifier}\" = 'configuration-one';";
            await changeOriginalSpelling.ExecuteNonQueryAsync();
        }
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);

        var result = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))]));

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Configuration-One", Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task LinkedQueryRejectsLookupCollisionEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("Primary-Id", "tools"));
        await using (var corruptEvidence = connection.CreateCommand())
        {
            corruptEvidence.CommandText =
                $"UPDATE \"{route.LinkedIndexStorage!.Name.Identifier}\" SET " +
                $"\"{route.LinkedRelationship!.DocumentId.Identifier}\" = 'Collision-Id', " +
                $"\"{route.LinkedRelationship.Identity.ComparisonKey.Identifier}\" = 'different-comparison';";
            await corruptEvidence.ExecuteNonQueryAsync();
        }
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() => queries.QueryAsync(
            new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))])));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("Collision-Id", exception.RequestedId);
        Assert.Equal("Primary-Id", exception.RetainedId);
        Assert.Equal(route.Envelope.Identity.Project("Primary-Id").LookupKey, exception.LookupKey);
    }

    [Fact]
    public async Task SubstringOperationsTreatLikeWildcardsAsLiteralInput()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var operations = new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.Contains,
            PortableQueryOperation.StartsWith
        };
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            categoryOperations: operations);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await writer.SaveAsync(Save("literal", "100%_ready"));
        await writer.SaveAsync(Save("wildcard-match", "100xxready"));
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, target.Routes.Single(), target.Provider);

        var result = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Contains("category", "%_"))]));

        Assert.Equal("literal", Assert.Single(result.Documents).Id);
        var prefix = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.StartsWith("category", "100%_"))]));
        Assert.Equal("literal", Assert.Single(prefix.Documents).Id);
    }

    [Fact]
    public async Task OversizedMembershipFailsBeforeDispatchInsteadOfExceedingProviderParameters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var operations = new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In
        };
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            categoryOperations: operations);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var writer = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        var queries = SqlitePhysicalQueryRuntime.Create(writer, manifest, target.Routes.Single(), target.Provider);
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("category", Enumerable.Range(0, 996).Select(index => index.ToString())))]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => queries.QueryAsync(query));
        Assert.Contains("provider limit", exception.Message);
    }

    [Fact]
    public async Task CanonicalJsonNumberAndDateTimePlansFailBeforeTrafficWhenExactSemanticsCannotBeCertified()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateCanonicalValueModel();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider));

        Assert.Contains("no executable server-side source", exception.Message);
    }

    [Theory]
    [InlineData(PortablePhysicalType.String, IndexValueKind.String, "\"alpha\"", "alpha")]
    [InlineData(PortablePhysicalType.Int32, IndexValueKind.Number, "42", "42")]
    [InlineData(PortablePhysicalType.Int64, IndexValueKind.Number, "42000000000", "42000000000")]
    [InlineData(PortablePhysicalType.Decimal, IndexValueKind.Number, "42.5", "42.5")]
    [InlineData(PortablePhysicalType.Boolean, IndexValueKind.Boolean, "true", "true")]
    [InlineData(PortablePhysicalType.DateTime, IndexValueKind.DateTime, "\"2026-01-01T01:00:00+01:00\"", "2026-01-01T00:00:00Z")]
    [InlineData(PortablePhysicalType.Guid, IndexValueKind.Keyword, "\"20b7b527-8799-45b2-8f43-aa742308da8c\"", "20b7b527-8799-45b2-8f43-aa742308da8c")]
    [InlineData(PortablePhysicalType.Json, IndexValueKind.Keyword, "{\"nested\":[1,2]}", "{\"nested\":[1,2]}")]
    [InlineData(PortablePhysicalType.Binary, IndexValueKind.Keyword, "\"AQID\"", "AQID")]
    public async Task Projected_queries_preserve_semantics_while_binding_the_native_physical_representation(
        PortablePhysicalType physicalType,
        IndexValueKind logicalKind,
        string jsonValue,
        string queryValue)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(physicalType, logicalKind);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "match",
            "1",
            $"{{\"value\":{jsonValue}}}",
            0));
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);

        var result = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", queryValue))]));

        Assert.Equal("match", Assert.Single(result.Documents).Id);
    }

    [Theory]
    [InlineData(PortablePhysicalType.Decimal, IndexValueKind.Number, "2", "10", "9")]
    [InlineData(
        PortablePhysicalType.DateTime,
        IndexValueKind.DateTime,
        "\"2026-01-01T00:00:00Z\"",
        "\"2026-02-01T01:00:00+01:00\"",
        "2026-01-15T00:00:00Z")]
    public async Task Projected_range_queries_use_the_declared_semantics_and_native_representation(
        PortablePhysicalType physicalType,
        IndexValueKind logicalKind,
        string lowJson,
        string highJson,
        string boundary)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(
            physicalType,
            logicalKind,
            PortableQueryOperation.GreaterThan);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "low", "1", $"{{\"value\":{lowJson}}}", 0));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "high", "1", $"{{\"value\":{highJson}}}", 0));
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);

        var result = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("value", boundary))]));

        Assert.Equal("high", Assert.Single(result.Documents).Id);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Decimal_equality_range_uniqueness_and_exponent_binding_are_exact(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(
            PortablePhysicalType.Decimal,
            IndexValueKind.Number,
            PortableQueryOperation.GreaterThan,
            form,
            isUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Value("low", "99999999999999.9998"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Value("high", "99999999999999.9999"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(Value("duplicate", "99999999999999.9999"))).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Value("rounded-collision", "99999999999999.99990000000000001")));
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);

        var greater = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("value", "99999999999999.9998"))]));
        var equal = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "99999999999999.9999"))]));

        Assert.Equal("high", Assert.Single(greater.Documents).Id);
        Assert.Equal("high", Assert.Single(equal.Documents).Id);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Value("exponent", "100"))).Status);
        var exponent = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("value", "1e1"))]));
        Assert.Contains(exponent.Documents, document => document.Id == "exponent");
        await Assert.ThrowsAsync<InvalidDataException>(() => queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "1e-29"))])));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Value("negative-exponent", "-1.25e2"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(
            Value("negative-max-exponent", "-9.99999999999999999e13"))).Status);
        var negativeExponent = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "-125"))]));
        Assert.Equal("negative-exponent", Assert.Single(negativeExponent.Documents).Id);
        var negativeMaxExponent = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "-99999999999999.9999"))]));
        Assert.Equal("negative-max-exponent", Assert.Single(negativeMaxExponent.Documents).Id);
    }

    private static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateIdentityModel(
        PortableQueryOperation operation = PortableQueryOperation.Equal)
    {
        var template = SqliteTestManifests.MetadataManifest();
        var logicalIndex = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-id",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "identity_entities",
            [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        ..(operation == PortableQueryOperation.Equal
                            ? new[]
                            {
                                new PhysicalIndexColumnDefinition("id_lookup_key", 1),
                                new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                            }
                            : [new PhysicalIndexColumnDefinition("id_comparison_key", 1)])
                    ])
            ]);
        var unit = template.StorageUnits.Single() with
        {
            IdentityPolicy = IdentityPolicy.StringId(
                stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query])
        };
        var manifest = template with { StorageUnits = [unit] };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (
            manifest,
            new PhysicalSchemaTarget(
                manifest.Identity,
                manifest.Version,
                SqliteTestManifests.Provider,
                compilation.Routes));
    }

    private static DocumentQuery IdentityQuery(
        string id,
        PortableQueryOperation operation = PortableQueryOperation.Equal) => new(
        "configurationDocument",
        "find-by-id",
        [DocumentQueryClause.Of(new DocumentQueryComparison(
            PhysicalDocumentFieldPaths.Id,
            operation switch
            {
                PortableQueryOperation.Equal => QueryComparisonOperator.Equal,
                PortableQueryOperation.GreaterThan => QueryComparisonOperator.GreaterThan,
                PortableQueryOperation.StartsWith => QueryComparisonOperator.StartsWith,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            },
            [id]))],
        resultOperation: BoundedQueryResultOperation.Count);

    [Theory]
    [InlineData("100000000000000.0000", "precision")]
    [InlineData("1.00001", "scale")]
    [InlineData("1e-29", "scale")]
    [InlineData("99999999999999.99990000000000001", "scale")]
    public async Task Decimal_values_outside_the_declared_shape_fail_before_mutation(string value, string expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(
            PortablePhysicalType.Decimal,
            IndexValueKind.Number);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Value("outside", value)));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.LoadAsync("configurationDocument", "outside"));
    }

    [Theory]
    [InlineData(PortablePhysicalType.Int32, PhysicalStorageForm.SharedDocuments)]
    [InlineData(PortablePhysicalType.Int32, PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PortablePhysicalType.Int32, PhysicalStorageForm.PhysicalEntityTable)]
    [InlineData(PortablePhysicalType.Int64, PhysicalStorageForm.SharedDocuments)]
    [InlineData(PortablePhysicalType.Int64, PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PortablePhysicalType.Int64, PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Integer_projections_reject_nonzero_values_that_decimal_would_round_to_zero(
        PortablePhysicalType physicalType,
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(physicalType, IndexValueKind.Number, form: form);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Value("underflow", "1e-29")));

        Assert.Null(await store.LoadAsync("configurationDocument", "underflow"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DateTime_equality_and_ranges_preserve_ticks_and_offset_equivalent_instants(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(
            PortablePhysicalType.DateTime,
            IndexValueKind.DateTime,
            PortableQueryOperation.GreaterThan,
            form,
            isUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(Value("tick-zero", "\"2026-01-01T00:00:00.0000000Z\""));
        await store.SaveAsync(Value("tick-one", "\"2026-01-01T00:00:00.0000001Z\""));
        await store.SaveAsync(Value("hundred-microseconds", "\"2026-01-01T00:00:00.0001000Z\""));
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(
            Value("same-instant", "\"2025-12-31T19:00:00.0000000-05:00\""))).Status);
        foreach (var subTick in new[] { ".00000001", ".00000014", ".00000015" })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveAsync(Value($"sub-tick-{subTick}", $"\"2026-01-01T00:00:00{subTick}Z\"")));
        }
        var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);

        var greater = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("value", "2025-12-31T19:00:00.0000000-05:00"))]));
        var equivalent = await queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "2025-12-31T19:00:00.0000000-05:00"))]));

        Assert.Equal(["hundred-microseconds", "tick-one"], greater.Documents.Select(document => document.Id).Order());
        Assert.Equal("tick-zero", Assert.Single(equivalent.Documents).Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => queries.QueryAsync(new DocumentQuery(
            "configurationDocument",
            "by-value",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("value", "2026-01-01T00:00:00.00000015Z"))])));
    }

    [Fact]
    public async Task DateTime_without_an_explicit_offset_fails_before_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateProjectedValueModel(
            PortablePhysicalType.DateTime,
            IndexValueKind.DateTime);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Value("offsetless", "\"2026-01-01T00:00:00.0000000\"")));

        Assert.Contains("explicit UTC designator or numeric offset", exception.InnerException?.Message);
        Assert.Null(await store.LoadAsync("configurationDocument", "offsetless"));
    }

    private static SaveDocumentRequest Save(string id, string category) =>
        Save(id, category, 1);

    private static SaveDocumentRequest Save(string id, string category, int priority) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":{priority}}}", 0);

    private static SaveDocumentRequest Save(string id, string category, int priority, bool visible) =>
        new(
            "configurationDocument",
            id,
            "1",
            $"{{\"category\":\"{category}\",\"priority\":{priority},\"visible\":{visible.ToString().ToLowerInvariant()}}}",
            0);

    private static SaveDocumentRequest HistorySave(string id, string status, string createdAt) =>
        new(
            "configurationDocument",
            id,
            "1",
            $"{{\"status\":\"{status}\",\"createdAt\":\"{createdAt}\"}}",
            0);

    private static SaveDocumentRequest Value(string id, string jsonValue) =>
        new("configurationDocument", id, "1", $"{{\"value\":{jsonValue}}}", 0);

    private static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateResidualHistoryModel()
    {
        var template = SqliteTestManifests.MetadataManifest();
        var logicalIndex = new LogicalIndexDeclaration(
            "history-order",
            [new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "history-by-created-at",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count
            },
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal,
                        PortableQueryOperation.In
                    })
            ]);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_execution_history",
            [
                new ProjectedColumnDefinition(
                    "created_at",
                    "createdAt",
                    PortablePhysicalType.DateTime),
                new ProjectedColumnDefinition(
                    "status",
                    "status",
                    PortablePhysicalType.String)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition(
                            "created_at",
                            0,
                            PhysicalSortDirection.Descending),
                        new PhysicalIndexColumnDefinition("id_lookup_key", 1)
                    ])
            ]);
        var unit = template.StorageUnits.Single() with
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query])
        };
        var manifest = template with { StorageUnits = [unit] };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (
            manifest,
            new PhysicalSchemaTarget(
                manifest.Identity,
                manifest.Version,
                SqliteTestManifests.Provider,
                compilation.Routes));
    }

    private static (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) CreateCanonicalValueModel()
    {
        var template = SqliteTestManifests.MetadataManifest();
        var logicalIndexes = new[]
        {
            new LogicalIndexDeclaration("by-score", [new IndexField("score")], IndexValueKind.Number, false, MissingValueBehavior.Excluded),
            new LogicalIndexDeclaration("by-enabled", [new IndexField("enabled")], IndexValueKind.Boolean, false, MissingValueBehavior.Excluded),
            new LogicalIndexDeclaration("by-occurred-at", [new IndexField("occurredAt")], IndexValueKind.DateTime, false, MissingValueBehavior.Excluded)
        };
        var comparableOperations = new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.GreaterThan,
            PortableQueryOperation.GreaterThanOrEqual,
            PortableQueryOperation.LessThan,
            PortableQueryOperation.LessThanOrEqual
        };
        var equalityOperations = new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.NotEqual,
            PortableQueryOperation.In
        };
        var queries = logicalIndexes.Select(index => new BoundedQueryDeclaration(
            index.Identity,
            index.Identity,
            index.ValueKind == IndexValueKind.Boolean ? equalityOperations : comparableOperations,
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary)).ToArray();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.DedicatedDocumentTable("canonical_documents")),
                        logicalIndexes,
                        queries)
                }
            ]
        };
        var resolution = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }

    private static (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) CreateProjectedValueModel(
        PortablePhysicalType physicalType,
        IndexValueKind logicalKind,
        PortableQueryOperation operation = PortableQueryOperation.Equal,
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        bool isUnique = false)
    {
        var template = SqliteTestManifests.MetadataManifest();
        var logical = new LogicalIndexDeclaration(
            "by-value",
            [new IndexField("value")],
            logicalKind,
            isUnique,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "by-value",
            logical.Identity,
            operation == PortableQueryOperation.Equal
                ? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }
                : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var projection = new ProjectedColumnDefinition(
            "value",
            "value",
            physicalType,
            Precision: physicalType == PortablePhysicalType.Decimal ? 18 : null,
            Scale: physicalType == PortablePhysicalType.Decimal ? 4 : null,
            IsNullable: false);
        var index = new PhysicalIndexDefinition(
            logical.Identity,
            [new PhysicalIndexColumnDefinition("value", 0)],
            isUnique);
        var binding = new SharedStorageBinding("runtime-documents");
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding, [projection], [index], linkedProjectionLogicalName: "typed_projection"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "typed_documents", indexes: [index], linkedProjectedColumns: [projection],
                linkedProjectionLogicalName: "typed_projection"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "typed_entities", [projection], indexes: [index]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition),
                        [logical],
                        [query])
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }

    private static async Task<string> ExplainCategoryLookupAsync(SqliteConnection connection, ExecutableStorageRoute route)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var scope = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.ScopeKey.Column.Identifier
            : route.LinkedRelationship!.StorageScope.Identifier;
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"EXPLAIN QUERY PLAN SELECT * FROM \"{table}\" WHERE \"{scope}\" = @scope AND \"{category.Column.Identifier}\" = @category;";
        command.Parameters.AddWithValue("@scope", "__groundwork_global__");
        command.Parameters.AddWithValue("@category", "tools");
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private static async Task<string> RunCursorProbeAsync(
        string operation,
        string databasePath,
        string? continuation = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Groundwork.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the Groundwork repository root.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(
            root.FullName,
            "tests",
            "Groundwork",
            "Groundwork.DocumentCursor.ProcessProbe",
            "bin",
            configuration,
            "net10.0",
            "Groundwork.DocumentCursor.ProcessProbe.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(databasePath);
        if (continuation is not null)
            start.ArgumentList.Add(continuation);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start cursor process probe.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
}
