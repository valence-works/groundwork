using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqlitePhysicalDocumentStoreTests
{
    [Fact]
    public async Task OnlyPrimaryKeyAndUniqueExtendedConstraintCodesAreClassifiedAsConcurrency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE constraint_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL UNIQUE);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (1, 'one');";
            await seed.ExecuteNonQueryAsync();
        }
        var dialect = new SqlitePhysicalDocumentDialect();

        var unique = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (2, 'one');";
            await command.ExecuteNonQueryAsync();
        });
        var notNull = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO constraint_probe (id, value) VALUES (3, NULL);";
            await command.ExecuteNonQueryAsync();
        });

        Assert.Equal(2067, unique.SqliteExtendedErrorCode);
        Assert.True(dialect.IsUniqueConstraintException(unique));
        Assert.Equal(1299, notNull.SqliteExtendedErrorCode);
        Assert.False(dialect.IsUniqueConstraintException(notNull));
    }

    [Fact]
    public async Task RequiredProjectionIsValidatedBeforeSqlDispatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "missing-category", "1", """{"priority":1}""", 0)));

        Assert.Contains("category", exception.Message);
        Assert.Null(await store.LoadAsync("configurationDocument", "missing-category"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task CrudOccAndProjectionsFollowTheCompiledRouteAtomically(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        var created = await store.SaveAsync(Save("one", "tools", 7, 0));
        var conflict = await store.SaveAsync(Save("one", "wrong", 9, 0));
        var updated = await store.SaveAsync(Save("one", "gadgets", 8, 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, created.Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        Assert.Equal(2, updated.Document!.Version);
        var loaded = await store.LoadAsync("configurationDocument", "one");
        Assert.Equal(updated.Document.ContentJson, loaded!.ContentJson);

        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var projectionTable = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal("gadgets", await ScalarAsync(connection,
            $"SELECT \"{category.Column.Identifier}\" FROM \"{projectionTable}\";"));

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "one", 2));
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Null(await store.LoadAsync("configurationDocument", "one"));
        if (route.LinkedIndexStorage is not null)
            Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{route.LinkedIndexStorage.Name.Identifier}\";")));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnitOfWorkCommitsOrRollsBackEnvelopeAndProjectionTogether(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(form, includePriority: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        await using (var rollback = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            await rollback.SaveAsync(Save("rolled-back", "tools", 1));
            await rollback.RollbackAsync();
        }
        await using (var commit = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument")))
        {
            await commit.SaveAsync(Save("committed", "tools", 1));
            await commit.CommitAsync();
        }

        Assert.Null(await store.LoadAsync("configurationDocument", "rolled-back"));
        Assert.NotNull(await store.LoadAsync("configurationDocument", "committed"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task FailedLinkedMutationAbortsTheUnitOfWorkBeforePartialStateCanBeCommitted(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            categoryUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);
        await store.SaveAsync(Save("owner", "tools", 1, 0));
        await store.SaveAsync(Save("candidate", "gadgets", 2, 0));

        await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(Save("earlier", "other", 3, 0))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.ConcurrencyConflict,
            (await unitOfWork.SaveAsync(Save("candidate", "tools", 2, 1))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

        Assert.Null(await store.LoadAsync("configurationDocument", "earlier"));
        Assert.Contains("\"category\":\"gadgets\"", (await store.LoadAsync("configurationDocument", "candidate"))!.ContentJson);
        var route = target.Routes.Single();
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        Assert.Equal("gadgets", await ScalarAsync(
            connection,
            $"SELECT \"{category.Column.Identifier}\" FROM \"{route.LinkedIndexStorage!.Name.Identifier}\" " +
            $"WHERE \"{route.LinkedRelationship!.DocumentId.Identifier}\" = 'candidate';"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task ScopeAndUniqueIndexesIsolateTheSameIdentityAndValue(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            scoped: true,
            categoryUnique: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var tenantA = new SqlitePhysicalDocumentStore(
            connection, manifest, target.Routes, DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = new SqlitePhysicalDocumentStore(
            connection, manifest, target.Routes, DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantA.SaveAsync(Save("same", "tools", 1, 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await tenantB.SaveAsync(Save("same", "tools", 2, 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await tenantA.SaveAsync(Save("other", "tools", 3, 0))).Status);

        Assert.Contains("\"priority\":1", (await tenantA.LoadAsync("configurationDocument", "same"))!.ContentJson);
        Assert.Contains("\"priority\":2", (await tenantB.LoadAsync("configurationDocument", "same"))!.ContentJson);
    }

    [Fact]
    public async Task DedicatedDocumentStorageDoesNotRequireALinkedTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
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
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        var target = new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        Assert.Null(route.LinkedIndexStorage);
        var store = new SqlitePhysicalDocumentStore(connection, manifest, target.Routes, DocumentStoreAccess.Global);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(Save("one", "tools", 1, 0))).Status);
        Assert.NotNull(await store.LoadAsync("configurationDocument", "one"));
    }

    [Fact]
    public async Task StatelessFacadeOwnsOneSerializedConnectionPerOperationAndOnePerUnitOfWork()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-sessions-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        var overlappingSessionObserved = false;
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
            }
            var sessions = RelationalSessionFactory.Serialized(() =>
            {
                lock (connections)
                {
                    overlappingSessionObserved |= connections.Any(connection => connection.State == System.Data.ConnectionState.Open);
                    var connection = new SqliteConnection($"Data Source={database}");
                    connections.Add(connection);
                    return connection;
                }
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);

            await store.SaveAsync(Save("one", "tools", 1, 0));
            await store.LoadAsync("configurationDocument", "one");
            var queries = SqlitePhysicalQueryRuntime.Create(store, manifest, target.Routes.Single(), target.Provider);
            Assert.Equal(1, (await queries.QueryAsync(new DocumentQuery(
                "configurationDocument",
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))]))).TotalCount);
            Assert.Equal(3, connections.Count);
            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));

            var beforeUnitOfWork = connections.Count;
            await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
            await unitOfWork.SaveAsync(Save("two", "tools", 2, 0));
            await unitOfWork.SaveAsync(Save("three", "tools", 3, 0));
            Assert.Equal(beforeUnitOfWork + 1, connections.Count);
            Assert.Equal(System.Data.ConnectionState.Open, connections[^1].State);
            await unitOfWork.CommitAsync();
            Assert.Equal(System.Data.ConnectionState.Closed, connections[^1].State);
            Assert.False(overlappingSessionObserved);
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Fact]
    public void StatelessSqliteFacadeRejectsPrivateInMemoryStorage()
    {
        var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true);

        var exception = Assert.Throws<ArgumentException>(() => new SqlitePhysicalDocumentStore(
            "Data Source=:memory:",
            manifest,
            target.Routes,
            DocumentStoreAccess.Global));

        Assert.Contains("direct-connection constructor", exception.Message);
    }

    [Fact]
    public async Task ReusableKernelSupportsConcurrentPerOperationSessionsWithoutRetainingConnections()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-pool-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.PhysicalEntityTable,
                includePriority: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
                var seed = new SqlitePhysicalDocumentStore(
                    materializationConnection, manifest, target.Routes, DocumentStoreAccess.Global);
                await seed.SaveAsync(Save("one", "tools", 1, 0));
            }
            var sessions = RelationalSessionFactory.Concurrent(() =>
            {
                var connection = new SqliteConnection($"Data Source={database}");
                lock (connections)
                    connections.Add(connection);
                return connection;
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions, manifest, target.Routes, DocumentStoreAccess.Global);

            var loaded = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => store.LoadAsync("configurationDocument", "one")));

            Assert.All(loaded, Assert.NotNull);
            Assert.Equal(20, connections.Count);
            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Fact]
    public async Task FailedStatelessUnitOfWorkRollsBackAndReleasesItsOwnedSession()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-physical-uow-{Guid.NewGuid():N}.db");
        var connections = new List<SqliteConnection>();
        try
        {
            var (manifest, target) = SqlitePhysicalSchemaExecutorTests.CreateModel(
                PhysicalStorageForm.DedicatedDocumentTable,
                includePriority: true,
                categoryUnique: true);
            await using (var materializationConnection = new SqliteConnection($"Data Source={database}"))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(materializationConnection));
            }
            var sessions = RelationalSessionFactory.Serialized(() =>
            {
                var connection = new SqliteConnection($"Data Source={database}");
                connections.Add(connection);
                return connection;
            });
            var store = new SqlitePhysicalDocumentStore(
                sessions, manifest, target.Routes, DocumentStoreAccess.Global);
            await store.SaveAsync(Save("owner", "tools", 1, 0));
            await store.SaveAsync(Save("candidate", "gadgets", 2, 0));

            await using var unitOfWork = await store.BeginAsync(DocumentCommitScope.Of("configurationDocument"));
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(Save("earlier", "other", 3, 0))).Status);
            Assert.Equal(
                DocumentStoreWriteStatus.ConcurrencyConflict,
                (await unitOfWork.SaveAsync(Save("candidate", "tools", 2, 1))).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

            Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
            Assert.Null(await store.LoadAsync("configurationDocument", "earlier"));
            Assert.Contains("\"category\":\"gadgets\"", (await store.LoadAsync("configurationDocument", "candidate"))!.ContentJson);
        }
        finally
        {
            File.Delete(database);
        }
    }

    private static SaveDocumentRequest Save(string id, string category, int priority, long? expectedVersion = null) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":{priority}}}", expectedVersion);

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
