using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Proves the precompiled plan set (groundwork#122) compiles bounded-query plans once at admission and
/// binds a per-session store cheaply, instead of recompiling the admitted catalog on every session open.
/// </summary>
public sealed class SqlitePhysicalQueryPlanSetTests
{
    private static readonly DocumentQuery ListByCategory = new(
        "configurationDocument",
        "list-by-category",
        [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
        [new DocumentQueryOrder("category")],
        skip: 0,
        take: 10);

    [Fact]
    public async Task Plan_set_compiles_once_and_every_bind_reuses_the_single_compilation()
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

        var compileCount = 0;
        var planSet = RelationalPhysicalQueryPlanSet.Compile(
            manifest,
            route,
            SqlitePhysicalQueryRuntime.Capabilities(target.Provider),
            explain: null,
            compile: (r, s, c) =>
            {
                Interlocked.Increment(ref compileCount);
                return PhysicalQueryPlanCompiler.Compile(r, s, c);
            });

        // Admission compiled exactly once; opening 25 sessions must not recompile.
        Assert.Equal(1, compileCount);
        for (var session = 0; session < 25; session++)
        {
            var bound = planSet.Bind(writer);
            var page = await bound.QueryAsync(ListByCategory);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal("a", Assert.Single(page.Documents).Id);
        }

        Assert.Equal(1, compileCount);
    }

    [Fact]
    public async Task Concurrent_binds_share_one_compilation()
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

        var compileCount = 0;
        var planSet = RelationalPhysicalQueryPlanSet.Compile(
            manifest,
            route,
            SqlitePhysicalQueryRuntime.Capabilities(target.Provider),
            explain: null,
            compile: (r, s, c) =>
            {
                Interlocked.Increment(ref compileCount);
                return PhysicalQueryPlanCompiler.Compile(r, s, c);
            });

        var ready = new ManualResetEventSlim(false);
        var binds = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            ready.Wait();
            return planSet.Bind(writer);
        })).ToArray();
        ready.Set();
        var boundStores = await Task.WhenAll(binds);

        Assert.Equal(1, compileCount);
        Assert.All(boundStores, Assert.NotNull);
        // A store bound by a racing thread still executes the certified plan correctly.
        var page = await boundStores[0].QueryAsync(ListByCategory);
        Assert.Equal("a", Assert.Single(page.Documents).Id);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Bound_store_matches_the_legacy_create_path(PhysicalStorageForm form)
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

        var legacy = SqlitePhysicalQueryRuntime.Create(writer, manifest, route, target.Provider);
        var bound = SqlitePhysicalQueryRuntime.CompilePlanSet(manifest, route, target.Provider).Bind(writer);

        foreach (var store in new[] { legacy, bound })
        {
            var page = await store.QueryAsync(ListByCategory);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal(["a", "b"], page.Documents.Select(document => document.Id));
            Assert.Equal(2, await store.CountAsync(ListByCategory.Select(BoundedQueryResultOperation.Count)));
            Assert.True(await store.AnyAsync(ListByCategory.Select(BoundedQueryResultOperation.Any)));
            Assert.Equal("a", (await store.FirstOrDefaultAsync(ListByCategory.Select(BoundedQueryResultOperation.First)))!.Id);
        }
    }

    private static SaveDocumentRequest Save(string id, string category) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":1}}", 0);
}
