using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteDocumentStoreTests
{
    [Fact]
    public void Document_store_construction_requires_factory_admission()
    {
        Assert.Empty(typeof(SqliteDocumentStore).GetConstructors());
        Assert.Empty(typeof(RelationalDocumentStore).GetConstructors());
    }

    [Fact]
    public async Task Unicode_identity_spelling_conflict_preserves_the_authoritative_document()
    {
        var manifest = WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await using var harness = await SqliteDocumentStoreHarness.Create(manifest);

        var saved = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "Straße-Σς",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));
        var conflict = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "straße-σΣ",
            "1.0.0",
            """{"key":"replacement","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("Straße-Σς", conflict.AuthoritativeId);
        var authoritative = await harness.Store.LoadAsync("configurationDocument", "Straße-Σς");
        Assert.Equal(1, authoritative!.Version);
        Assert.Contains("alpha", authoritative.ContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unicode_identity_load_and_delete_return_the_authoritative_original()
    {
        var manifest = WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await using var harness = await SqliteDocumentStoreHarness.Create(manifest);
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "Straße-Σς",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var loaded = await harness.Store.LoadAsync("configurationDocument", "straße-σΣ");
        var deleted = await harness.Store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            "STRAßE-Σσ",
            ExpectedVersion: 1));

        Assert.Equal("Straße-Σς", loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("Straße-Σς", deleted.AuthoritativeId);
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "Straße-Σς"));
    }

    [Fact]
    public async Task Lookup_hash_collision_throws_the_dedicated_integrity_exception()
    {
        var manifest = WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await using var harness = await SqliteDocumentStoreHarness.Create(manifest);
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "retained",
            "1.0.0",
            """{"key":"retained","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "requested",
            "1.0.0",
            """{"key":"requested","category":"system"}"""));

        var lookupCommand = harness.Connection.CreateCommand();
        lookupCommand.CommandText = "SELECT id_lookup_key FROM groundwork_documents WHERE id = 'requested';";
        var requestedLookup = Assert.IsType<string>(await lookupCommand.ExecuteScalarAsync());
        await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "requested"));
        var corruptCommand = harness.Connection.CreateCommand();
        corruptCommand.CommandText = "UPDATE groundwork_documents SET id_lookup_key = @lookup WHERE id = 'retained';";
        corruptCommand.Parameters.AddWithValue("lookup", requestedLookup);
        await corruptCommand.ExecuteNonQueryAsync();

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            harness.Store.LoadAsync("configurationDocument", "requested"));

        Assert.Equal("requested", exception.RequestedId);
        Assert.Equal("retained", exception.RetainedId);
    }

    [Fact]
    public async Task Materialization_rejects_document_identity_policy_drift()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var materializer = new SqliteGroundworkMaterializer(connection);
        await materializer.MaterializeAsync(
            SqliteTestManifests.MetadataManifest(),
            SqliteTestManifests.Provider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync(
                WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
                SqliteTestManifests.Provider));

        Assert.Contains("configurationDocument", exception.Message, StringComparison.Ordinal);
        Assert.Contains("identity schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restart_validation_does_not_repair_rows_behind_recorded_identity_schema_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var manifest = WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var materializer = new SqliteGroundworkMaterializer(connection);
        await materializer.MaterializeAsync(manifest, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(connection, manifest, Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "retained", "1", """{"key":"retained"}"""));
        await using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE groundwork_documents SET id_comparison_key = 'tampered' WHERE id = 'retained';";
            await corrupt.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync(manifest, SqliteTestManifests.Provider));

        Assert.Contains("recorded original identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT id_comparison_key FROM groundwork_documents WHERE id = 'retained';";
        Assert.Equal("tampered", (string)(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Materialization_evolves_pre_policy_rows_before_recording_identity_schema_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await CreateLegacyDocumentTableAsync(connection);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO groundwork_documents
                    (document_kind, storage_scope, id, schema_version, version, content_json, created_utc, updated_utc)
                VALUES
                    ('configurationDocument', '__groundwork_global__', '𐐀', '1', 1, '{"key":"legacy"}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var manifest = WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(
            connection,
            manifest,
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

        var loaded = await store.LoadAsync("configurationDocument", "𐐨");

        Assert.Equal("𐐀", loaded!.Id);
    }

    [Fact]
    public async Task Materialization_rolls_back_pre_policy_identity_collisions_without_recording_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await CreateLegacyDocumentTableAsync(connection);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO groundwork_documents
                    (document_kind, storage_scope, id, schema_version, version, content_json, created_utc, updated_utc)
                VALUES
                    ('configurationDocument', '__groundwork_global__', 'A', '1', 1, '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
                    ('configurationDocument', '__groundwork_global__', 'a', '1', 1, '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteGroundworkMaterializer(connection).MaterializeAsync(
                WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
                SqliteTestManifests.Provider));

        Assert.Contains("collide", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var shape = connection.CreateCommand();
        shape.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('groundwork_documents')
            WHERE name IN ('id_comparison_key', 'id_lookup_key');
            """;
        Assert.Equal(0L, (long)(await shape.ExecuteScalarAsync())!);
        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'groundwork_document_identity_schema';";
        Assert.Equal(0L, (long)(await state.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Materialization_fails_closed_when_pre_policy_storage_has_no_original_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE groundwork_documents (
                    document_kind TEXT NOT NULL,
                    storage_scope TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    content_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteGroundworkMaterializer(connection).MaterializeAsync(
                WithIdentityCasePolicy(StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
                SqliteTestManifests.Provider));

        Assert.Contains("original identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FactoryRejectsInvalidManifestBeforeCreatingAnyConnection()
    {
        var materializationConnections = 0;
        var operationConnections = 0;
        var manifest = SqliteTestManifests.MetadataManifest() with { StorageUnits = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                "Data Source=never-opened.db",
                manifest,
                SqliteTestManifests.Provider,
                () =>
                {
                    materializationConnections++;
                    return new SqliteConnection("Data Source=never-opened.db");
                },
                () =>
                {
                    operationConnections++;
                    return new SqliteConnection("Data Source=never-opened.db");
                },
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Equal(0, materializationConnections);
        Assert.Equal(0, operationConnections);
    }

    [Fact]
    public async Task SaveLoadUpdateQueryAndDeleteMaintainIndexes()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        var store = harness.Store;

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system","value":1}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);

        var loaded = await store.LoadAsync("configurationDocument", "doc-1");
        Assert.NotNull(loaded);
        using var loadedContent = JsonDocument.Parse(loaded.ContentJson);
        Assert.Equal("alpha", loadedContent.RootElement.GetProperty("key").GetString());

        var byKey = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha"));
        Assert.Single(byKey);

        var updated = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"application","value":2}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(2, updated.Document!.Version);

        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "application")));

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "doc-1", ExpectedVersion: 2));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("doc-1", deleted.AuthoritativeId);
        Assert.Null(await store.LoadAsync("configurationDocument", "doc-1"));
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
    }

    [Fact]
    public async Task JsonHelpersSaveLoadAndQueryTypedDocuments()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var document = new JsonConfigurationDocument("alpha", "system", 1);

        var saved = await harness.Store.SaveJsonAsync(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            document,
            options);

        var loaded = await harness.Store.LoadJsonAsync<JsonConfigurationDocument>(
            "configurationDocument",
            "doc-1",
            options);
        var byKey = await harness.Store.QueryJsonAsync<JsonConfigurationDocument>(
            new DocumentStoreQuery("configurationDocument", "by-key", "alpha"),
            options);

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(document, loaded);
        Assert.Single(byKey);
        Assert.Equal(document, byKey[0]);
    }

    [Fact]
    public async Task FactoryMaterializesAndReturnsUsableStore()
    {
        var manifest = SqliteTestManifests.MetadataManifest();
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-factory-{Guid.NewGuid():N}.db");
        try
        {
            var store = await SqliteDocumentStoreFactory.CreateAsync(
                $"Data Source={databasePath};Pooling=False",
                manifest,
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            var saved = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                "doc-1",
                "1.0.0",
                """{"key":"alpha","category":"system"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
            Assert.NotNull(await store.LoadAsync("configurationDocument", "doc-1"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(StorageIdentityKind.Guid)]
    [InlineData(StorageIdentityKind.Composite)]
    public async Task FactoryAndStorePreserveOrdinalProjectionForAdmittedNonStringIdentityKinds(
        StorageIdentityKind identityKind)
    {
        var manifest = WithIdentityKind(identityKind);
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-{identityKind}-{Guid.NewGuid():N}.db");
        try
        {
            var store = await SqliteDocumentStoreFactory.CreateAsync(
                $"Data Source={databasePath};Pooling=False",
                manifest,
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            var upper = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "A-B", "1.0.0", """{"key":"upper"}"""));
            var lower = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "a-b", "1.0.0", """{"key":"lower"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, upper.Status);
            Assert.Equal(DocumentStoreWriteStatus.Saved, lower.Status);
            var updated = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "A-B", "1.0.0", """{"key":"updated"}""", ExpectedVersion: 1));
            var deleted = await store.DeleteAsync(new DeleteDocumentRequest(
                "configurationDocument", "a-b", ExpectedVersion: 1));
            Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
            Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
            Assert.Contains("updated", (await store.LoadAsync("configurationDocument", "A-B"))!.ContentJson);
            Assert.Null(await store.LoadAsync("configurationDocument", "a-b"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=groundwork;Mode=Memory")]
    public async Task FactoryRejectsPrivateInMemoryDatabase(string connectionString)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Contains("direct-connection constructor", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Source=file::memory:")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    public async Task FactoryRejectsSqliteMemoryUri(string connectionString)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Contains("direct-connection constructor", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mode=ReadWriteCreate")]
    [InlineData("Data Source=   ")]
    public async Task FactoryRejectsEmptyOrWhitespaceDataSource(string connectionString)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Equal("connectionString", exception.ParamName);
    }

    [Fact]
    public async Task FactoryAcceptsFilePathContainingMemoryModeText()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-mode=memory-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        try
        {
            var store = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            var saved = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                "doc-1",
                "1.0.0",
                """{"key":"alpha","category":"system"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task FactoryRejectsSqliteUriMemoryModeCaseInsensitively()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                "Data Source=file:groundwork?cache=shared&MoDe=MeMoRy",
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Contains("direct-connection constructor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryDisposesMaterializationConnectionBeforeReturningStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-factory-lifetime-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        SqliteConnection? materializationConnection = null;
        try
        {
            var store = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                () => materializationConnection = new SqliteConnection(connectionString),
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            Assert.NotNull(materializationConnection);
            Assert.Equal(ConnectionState.Closed, materializationConnection.State);

            var saved = await store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                "doc-1",
                "1.0.0",
                """{"key":"alpha","category":"system"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
            Assert.Equal(ConnectionState.Closed, materializationConnection.State);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task FactoryCancellationDoesNotCreateMaterializationConnection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-factory-cancel-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        SqliteConnection? materializationConnection = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SqliteDocumentStoreFactory.CreateAsync(
                    connectionString,
                    SqliteTestManifests.MetadataManifest(),
                    SqliteTestManifests.Provider,
                    () => materializationConnection = new SqliteConnection(connectionString),
                    Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                    cancellationToken: cancellation.Token));

            Assert.Null(materializationConnection);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task FactoryFailureDisposesMaterializationConnectionAndReturnsNoStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-factory-failure-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var inaccessibleConnectionString = $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db")};Pooling=False";
        SqliteConnection? materializationConnection = null;
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                SqliteDocumentStoreFactory.CreateAsync(
                    connectionString,
                    SqliteTestManifests.MetadataManifest(),
                    SqliteTestManifests.Provider,
                    () => materializationConnection = new SqliteConnection(inaccessibleConnectionString),
                    Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

            Assert.NotNull(materializationConnection);
            Assert.Equal(ConnectionState.Closed, materializationConnection.State);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task FactorySerializesFileBackedStoreOperations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-factory-serialized-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var operationConnections = new ConcurrentBag<SqliteConnection>();
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstWrite = new ManualResetEventSlim();
        try
        {
            var store = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                SqliteTestManifests.MetadataManifest(),
                SqliteTestManifests.Provider,
                () => new SqliteConnection(connectionString),
                () =>
                {
                    var connection = new SqliteConnection(connectionString);
                    connection.CreateFunction("groundwork_test_wait", () =>
                    {
                        firstWriteEntered.TrySetResult();
                        releaseFirstWrite.Wait(TimeSpan.FromSeconds(10));
                        return 0;
                    });
                    operationConnections.Add(connection);
                    return connection;
                },
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER groundwork_test_wait_before_insert
                    BEFORE INSERT ON groundwork_documents
                    BEGIN
                        SELECT groundwork_test_wait();
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var first = Task.Run(() => store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "doc-1", "1.0.0", """{"key":"alpha","category":"system"}""")));
            await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var second = Task.Run(() => store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "doc-2", "1.0.0", """{"key":"beta","category":"system"}""")));
            await Task.Delay(250);

            Assert.Single(operationConnections);
            releaseFirstWrite.Set();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, result => Assert.Equal(DocumentStoreWriteStatus.Saved, result.Status));
            Assert.Equal(2, operationConnections.Count);
        }
        finally
        {
            releaseFirstWrite.Set();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void DeserializeJsonFailsClearlyForNullDocumentContent()
    {
        var envelope = new DocumentEnvelope(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            1,
            "null",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            envelope.DeserializeJson<JsonConfigurationDocument>());

        Assert.Contains("doc-1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("configurationDocument", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeclaredIndexQueryFailsClearly()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();

        var exception = await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "missing-index", "alpha")));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("missing-index", exception.IndexName);
    }

    [Fact]
    public async Task UniqueIndexesAreEnforcedBySQLite()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var duplicate = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-2",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, duplicate.Status);
    }

    [Fact]
    public async Task ConcurrentQueriesCanShareStoreConnection()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var queries = Enumerable.Range(0, 50)
            .Select(_ => harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));

        var results = await Task.WhenAll(queries);

        Assert.All(results, result => Assert.Single(result));
    }

    [Fact]
    public async Task CompoundIndexesAreNotQueryableUntilPortableSupportExists()
    {
        var manifest = WithCompoundIndex(SqliteTestManifests.MetadataManifest());
        await using var connection = new SqliteConnection("Data Source=:memory:");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider));
    }

    [Fact]
    public async Task MissingRowDuringUnguardedUpdateReturnsNotFound()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        var store = new RelationalDocumentStore(harness.Connection, SqliteTestManifests.MetadataManifest(), new MissingUpdateDialect(), Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var result = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, result.Status);
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
    }

    [Fact]
    public async Task MissingRowDuringUnguardedDeleteReturnsNotFound()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        var store = new RelationalDocumentStore(harness.Connection, SqliteTestManifests.MetadataManifest(), new MissingDeleteDialect(), Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var result = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "doc-1"));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, result.Status);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", "doc-1"));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
    }

    [Fact]
    public async Task DependencyFailureDuringIndexRefreshReturnsNotFoundAndRollsBack()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        var store = new RelationalDocumentStore(harness.Connection, SqliteTestManifests.MetadataManifest(), new DependencyFailureDialect(), Groundwork.Documents.Scoping.DocumentStoreAccess.Global);

        var result = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, result.Status);
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "doc-1"));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotUpdateDocumentOrIndexes()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"gamma","category":"system"}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "gamma")));
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotDeleteDocumentOrIndexes()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "doc-1", ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", "doc-1"));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
    }

    [Fact]
    public async Task ExpectedVersionZeroCreatesWhenAbsentAndConflictsWhenPresent()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();

        // Create-only: expected version 0 against an absent document inserts version 1.
        var created = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}""",
            ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, created.Status);
        Assert.Equal(1, created.Document!.Version);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));

        // Create-only against an existing document is refused and mutates neither document nor indexes.
        var refused = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"clobber","category":"system"}""",
            ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, refused.Status);
        var loaded = await harness.Store.LoadAsync("configurationDocument", "doc-1");
        Assert.Equal(1, loaded!.Version);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "clobber")));
    }

    [Fact]
    public async Task PositiveExpectedVersionAgainstAbsentDocumentIsNotFoundAndWritesNothing()
    {
        await using var harness = await SqliteDocumentStoreHarness.Create();

        // A positive expected version can never match an absent document: NotFound, nothing persisted.
        var missing = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"ghost","category":"system"}""",
            ExpectedVersion: 3));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, missing.Status);
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "doc-1"));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "ghost")));

        // Delete semantics are unchanged: expected version 0 against an absent document stays NotFound.
        var deleteMissing = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "doc-1", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, deleteMissing.Status);
    }

    private sealed class SqliteDocumentStoreHarness : IAsyncDisposable
    {
        private SqliteDocumentStoreHarness(SqliteConnection connection, SqliteDocumentStore store)
        {
            Connection = connection;
            Store = store;
        }

        public SqliteConnection Connection { get; }
        public SqliteDocumentStore Store { get; }

        public static async Task<SqliteDocumentStoreHarness> Create(StorageManifest? manifest = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            manifest ??= SqliteTestManifests.MetadataManifest();
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider);
            return new SqliteDocumentStoreHarness(connection, new SqliteDocumentStore(connection, manifest, Groundwork.Documents.Scoping.DocumentStoreAccess.Global));
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private static StorageManifest WithIdentityCasePolicy(StringIdentityCasePolicy policy)
    {
        var manifest = SqliteTestManifests.MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: policy)
                }
            ]
        };
    }

    private static StorageManifest WithIdentityKind(StorageIdentityKind kind)
    {
        var manifest = SqliteTestManifests.MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    IdentityPolicy = new IdentityPolicy(kind, "id")
                }
            ]
        };
    }

    private static async Task CreateLegacyDocumentTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE groundwork_documents (
                document_kind TEXT NOT NULL,
                storage_scope TEXT NOT NULL,
                id TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                version INTEGER NOT NULL,
                content_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (document_kind, storage_scope, id)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static StorageManifest WithCompoundIndex(StorageManifest manifest)
    {
        var unit = manifest.StorageUnits.Single();
        var compoundIndex = new IndexDeclaration(
            "by-key-and-category",
            [new IndexField("key"), new IndexField("category")],
            IndexValueKind.Keyword,
            true,
            true,
            MissingValueBehavior.Excluded,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });

        return manifest with { StorageUnits = [unit with { Indexes = [compoundIndex] }] };
    }

    private sealed class MissingUpdateDialect : RelationalDocumentStoreDialect
    {
        public override string UpdateDocumentSql => $$"""
            UPDATE groundwork_documents
            SET schema_version = {{Parameter("schemaVersion")}},
                version = {{Parameter("version")}},
                content_json = {{Parameter("content")}},
                updated_utc = {{Parameter("updatedUtc")}}
            WHERE document_kind = {{Parameter("kind")}} AND id = '__missing__';
            """;
    }

    private sealed class MissingDeleteDialect : RelationalDocumentStoreDialect
    {
        public override string DeleteDocumentSql => $$"""
            DELETE FROM groundwork_documents
            WHERE document_kind = {{Parameter("kind")}} AND id = '__missing__';
            """;
    }

    private sealed class DependencyFailureDialect : RelationalDocumentStoreDialect
    {
        public override string InsertIndexSql => "SELECT missing_write_dependency;";

        public override bool IsWriteDependencyException(System.Data.Common.DbException exception) => exception is SqliteException;
    }

    private sealed record JsonConfigurationDocument(string Key, string Category, int Value);
}
