using Groundwork.Core.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Proves the write-path SQL memoization (groundwork#123): the deterministic save/update/delete/lock-load
/// statements for a route are rendered exactly once per (dialect discriminator, route) and reused, distinct
/// route shapes get distinct entries, and concurrent resolution is safe.
/// </summary>
public sealed class RelationalPhysicalMutationSqlCacheTests
{
    private static ExecutableStorageRoute Route(
        PhysicalStorageForm form = PhysicalStorageForm.SharedDocuments,
        bool includePriority = true) =>
        SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority).Target.Routes.Single();

    [Fact]
    public void Same_route_and_dialect_render_the_sql_once()
    {
        var cache = new RelationalPhysicalMutationSqlCache();
        var dialect = new SqlitePhysicalDocumentDialect();
        var route = Route();

        var first = cache.GetOrCompile(dialect, route);
        var second = cache.GetOrCompile(dialect, route);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Distinct_dialect_instances_of_the_same_provider_share_one_entry()
    {
        var cache = new RelationalPhysicalMutationSqlCache();
        var route = Route();

        var first = cache.GetOrCompile(new SqlitePhysicalDocumentDialect(), route);
        var second = cache.GetOrCompile(new SqlitePhysicalDocumentDialect(), route);

        // Keyed by the dialect's discriminator, not by instance identity.
        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Distinct_route_shapes_get_distinct_entries()
    {
        var cache = new RelationalPhysicalMutationSqlCache();
        var dialect = new SqlitePhysicalDocumentDialect();

        // SharedDocuments keeps projections in a linked index table; the priority column changes the
        // linked insert, not the shared-envelope primary insert.
        var sharedWithProjection = cache.GetOrCompile(dialect, Route(includePriority: true));
        var sharedWithoutProjection = cache.GetOrCompile(dialect, Route(includePriority: false));
        // A dedicated entity table inlines projections, so it renders a different primary insert target.
        var entityTable = cache.GetOrCompile(
            dialect,
            Route(PhysicalStorageForm.PhysicalEntityTable, includePriority: true));

        Assert.NotSame(sharedWithProjection, sharedWithoutProjection);
        Assert.NotSame(sharedWithProjection, entityTable);
        Assert.NotEqual(sharedWithProjection.LinkedInsert, sharedWithoutProjection.LinkedInsert);
        Assert.NotEqual(sharedWithProjection.InsertPrimary, entityTable.InsertPrimary);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public async Task Concurrent_resolution_is_safe_and_shares_one_instance()
    {
        var cache = new RelationalPhysicalMutationSqlCache();
        var dialect = new SqlitePhysicalDocumentDialect();
        var route = Route();

        using var ready = new ManualResetEventSlim(false);
        var resolves = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            ready.Wait();
            return cache.GetOrCompile(dialect, route);
        })).ToArray();
        ready.Set();
        var resolved = await Task.WhenAll(resolves);

        var expected = resolved[0];
        Assert.All(resolved, entry => Assert.Same(expected, entry));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Rendered_statements_cover_every_write_shape()
    {
        var sql = RelationalPhysicalMutationSql.Compile(new SqlitePhysicalDocumentDialect(), Route());

        Assert.StartsWith("INSERT INTO ", sql.InsertPrimary);
        Assert.StartsWith("UPDATE ", sql.UpdatePrimaryWithoutExpectedVersion);
        Assert.StartsWith("DELETE FROM ", sql.DeletePrimaryWithoutExpectedVersion);

        // Version and non-version variants differ only by the optimistic-concurrency clause.
        Assert.NotEqual(sql.UpdatePrimaryWithoutExpectedVersion, sql.UpdatePrimaryWithExpectedVersion);
        Assert.NotEqual(sql.DeletePrimaryWithoutExpectedVersion, sql.DeletePrimaryWithExpectedVersion);
        Assert.Contains("expectedVersion", sql.UpdatePrimaryWithExpectedVersion);
        Assert.DoesNotContain("expectedVersion", sql.UpdatePrimaryWithoutExpectedVersion);

        // Read and locking loads differ.
        Assert.NotEqual(sql.LoadForRead, sql.LoadForWrite);
        Assert.Equal(sql.UpdatePrimaryWithExpectedVersion, sql.UpdatePrimary(includeExpectedVersion: true));
        Assert.Equal(sql.DeletePrimaryWithoutExpectedVersion, sql.DeletePrimary(includeExpectedVersion: false));
        Assert.Equal(sql.LoadForWrite, sql.Load(lockForWrite: true));
    }

    [Fact]
    public void Shared_documents_render_linked_index_statements()
    {
        var sql = RelationalPhysicalMutationSql.Compile(
            new SqlitePhysicalDocumentDialect(),
            Route(PhysicalStorageForm.SharedDocuments, includePriority: true));

        Assert.NotNull(sql.LinkedInsert);
        Assert.NotNull(sql.LinkedDelete);
        Assert.NotNull(sql.LinkedEvidenceSelect);
        Assert.StartsWith("INSERT INTO ", sql.LinkedInsert);
    }

    [Fact]
    public void Entity_tables_inline_projections_and_render_no_linked_statements()
    {
        var sql = RelationalPhysicalMutationSql.Compile(
            new SqlitePhysicalDocumentDialect(),
            Route(PhysicalStorageForm.PhysicalEntityTable, includePriority: true));

        Assert.Null(sql.LinkedInsert);
        Assert.Null(sql.LinkedDelete);
        Assert.Null(sql.LinkedEvidenceSelect);
        Assert.Contains("priority", sql.InsertPrimary);
    }
}
