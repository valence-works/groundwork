using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteRelationalPhysicalStorageConformanceTests : RelationalPhysicalStorageConformance
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Caller_cancellation_after_non_success_cannot_prevent_rollback(
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var model = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
        using var cancellation = new CancellationTokenSource();
        var store = new RelationalPhysicalDocumentStore(
            connection,
            model.Manifest,
            model.Target.Routes,
            new SqlitePhysicalDocumentDialect(),
            DocumentStoreAccess.Global,
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            });
        await using var transaction = await store.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "staged-before-cancellation",
            "1",
            "{\"category\":\"tools\",\"priority\":1}",
            ExpectedVersion: 0))).Status);

        var nonSuccess = await transaction.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "missing",
            "1",
            "{\"category\":\"tools\",\"priority\":1}",
            ExpectedVersion: 1), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, nonSuccess.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        Assert.Null(await store.LoadAsync("configurationDocument", "staged-before-cancellation"));
    }

    protected override async Task<RelationalPhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = dedicatedWithoutLinked ? CreateDedicatedWithoutLinked() : SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            var route = model.Target.Routes.Single();
            var store = new SqlitePhysicalDocumentStore(
                connection,
                model.Manifest,
                model.Target.Routes,
                DocumentStoreAccess.Global);
            var queries = dedicatedWithoutLinked
                ? null
                : SqlitePhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider);
            return new RelationalPhysicalStorageFixture(
                store,
                queries,
                route,
                dedicatedWithoutLinked ? () => Task.FromResult(string.Empty) : () => ExplainCategoryLookupAsync(connection, route),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<RelationalScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var model = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true, scoped: true);
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlitePhysicalSchemaExecutor(connection));
            return new RelationalScopedPhysicalStorageFixture(
                access => new SqlitePhysicalDocumentStore(connection, model.Manifest, model.Target.Routes, access),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<RelationalPhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var initial = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: false);
            var additive = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
            var executor = new SqlitePhysicalSchemaExecutor(connection);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
            var initialDocuments = new SqlitePhysicalDocumentStore(
                connection,
                initial.Manifest,
                initial.Target.Routes,
                DocumentStoreAccess.Global);
            return new RelationalPhysicalStorageEvolutionFixture(
                initialDocuments,
                async () =>
                {
                    await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
                    var route = additive.Target.Routes.Single();
                    var store = new SqlitePhysicalDocumentStore(
                        connection,
                        additive.Manifest,
                        additive.Target.Routes,
                        DocumentStoreAccess.Global);
                    return new RelationalPhysicalStorageFixture(
                        store,
                        SqlitePhysicalQueryRuntime.Create(store, additive.Manifest, route, additive.Target.Provider),
                        route,
                        () => ExplainCategoryLookupAsync(connection, route),
                        () => ValueTask.CompletedTask);
                },
                async () => (await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor)).Outcome,
                async cancellationToken =>
                    await executor.AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
                connection.DisposeAsync);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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

    private static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateDedicatedWithoutLinked()
    {
        var template = SqliteTestManifests.MetadataManifest();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.DedicatedDocumentTable("configuration_documents")))
                }
            ]
        };
        var resolution = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }
}
