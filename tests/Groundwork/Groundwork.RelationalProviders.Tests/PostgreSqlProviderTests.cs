using Groundwork.Documents.Store;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.Materialization;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class PostgreSqlProviderTests : RelationalProviderContractTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public void DocumentStoreConstructionRequiresFactoryAdmission() =>
        Assert.Empty(typeof(PostgreSqlDocumentStore).GetConstructors());

    [Fact]
    public async Task FactoryRejectsInvalidManifestBeforeCreatingAnyConnection()
    {
        var materializationConnections = 0;
        var operationConnections = 0;
        var manifest = RelationalTestManifests.MetadataManifest() with { StorageUnits = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlDocumentStoreFactory.CreateAsync(
                "Host=never-opened",
                manifest,
                RelationalTestManifests.PostgreSqlProvider,
                () =>
                {
                    materializationConnections++;
                    return new NpgsqlConnection();
                },
                () =>
                {
                    operationConnections++;
                    return new NpgsqlConnection();
                },
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Equal(0, materializationConnections);
        Assert.Equal(0, operationConnections);
    }

    [Fact]
    public async Task IndependentOperationsUseTheProviderPoolWithoutGlobalSerialization()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 2 };
        var blockerBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Pooling = false };
        var twoConnectionsOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = await PostgreSqlDocumentStoreFactory.CreateAsync(
            builder.ConnectionString,
            manifest,
            RelationalTestManifests.PostgreSqlProvider,
            () => new NpgsqlConnection(builder.ConnectionString),
            () =>
            {
                var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == System.Data.ConnectionState.Open && Interlocked.Increment(ref opened) == 2)
                        twoConnectionsOpened.TrySetResult();
                };
                return connection;
            },
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        var blocker = await RelationalSessionPoolPressure.BlockPostgreSqlDocumentsAsync(blockerBuilder.ConnectionString);

        await RelationalSessionPoolPressure.AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
            store,
            twoConnectionsOpened.Task,
            blocker);
    }

    protected override async Task<IRelationalProviderHarness> CreateHarnessAsync(Groundwork.Core.Manifests.StorageManifest? manifest = null)
    {
        var connectionString = container.GetConnectionString();
        var connection = new NpgsqlConnection(connectionString);
        manifest ??= RelationalTestManifests.MetadataManifest();
        var store = await PostgreSqlDocumentStoreFactory.CreateAsync(
            connectionString,
            manifest,
            RelationalTestManifests.PostgreSqlProvider,
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        return new PostgreSqlProviderHarness(connection, store, manifest, connectionString);
    }

    private sealed class PostgreSqlProviderHarness(
        NpgsqlConnection connection,
        IDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        string connectionString)
        : IRelationalProviderHarness
    {
        public string ProviderName => "postgresql";

        public IDocumentStore Store { get; } = store;

        public Task MaterializeAsync() =>
            new PostgreSqlGroundworkMaterializer(connection).MaterializeAsync(manifest, RelationalTestManifests.PostgreSqlProvider);

        public async Task<IDocumentStore> ApplyManifestAsync(Groundwork.Core.Manifests.StorageManifest targetManifest)
        {
            return await PostgreSqlDocumentStoreFactory.CreateAsync(
                connectionString,
                targetManifest,
                RelationalTestManifests.PostgreSqlProvider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        }

        public async Task<IDocumentStore> CreateStoreAsync(
            Groundwork.Core.Manifests.StorageManifest targetManifest,
            Groundwork.Documents.Scoping.DocumentStoreAccess access) =>
            await PostgreSqlDocumentStoreFactory.CreateAsync(
                connectionString,
                targetManifest,
                RelationalTestManifests.PostgreSqlProvider,
                access);

        public async Task<long> CountSchemaHistoryRowsAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM groundwork_schema_history
                WHERE manifest_id = @manifestId AND provider_name = @providerName;
                """;
            command.Parameters.AddWithValue("@manifestId", manifest.Identity.Value);
            command.Parameters.AddWithValue("@providerName", RelationalTestManifests.PostgreSqlProvider.Name);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<long> CountIdentitySchemaRowsAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM groundwork_document_identity_schema;";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task ReAdmitIdentitySchemaConcurrentlyAsync()
        {
            await EnsureOpenAsync();
            await using (var reset = connection.CreateCommand())
            {
                reset.CommandText = """
                    DELETE FROM groundwork_document_identity_schema;
                    DROP INDEX IF EXISTS ux_groundwork_documents_identity_lookup;
                    """;
                await reset.ExecuteNonQueryAsync();
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var concurrent = new NpgsqlConnection(connectionString);
                await new PostgreSqlGroundworkMaterializer(concurrent).MaterializeAsync(
                    manifest,
                    RelationalTestManifests.PostgreSqlProvider);
            }));
        }

        public async Task ReplaceIdentityLookupWithFilteredIndexAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ux_groundwork_documents_identity_lookup;
                CREATE UNIQUE INDEX ux_groundwork_documents_identity_lookup
                ON groundwork_documents(document_kind, storage_scope, id_lookup_key)
                WHERE id_lookup_key = 'excluded-from-runtime';
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task PrepareLegacyIdentityRowsAsync(IReadOnlyList<string> ids)
        {
            await EnsureOpenAsync();
            await using (var reset = connection.CreateCommand())
            {
                reset.CommandText = """
                    DROP INDEX IF EXISTS ux_groundwork_documents_identity_lookup;
                    DROP TABLE groundwork_document_identity_schema;
                    ALTER TABLE groundwork_documents
                        DROP COLUMN id_comparison_key,
                        DROP COLUMN id_lookup_key;
                    """;
                await reset.ExecuteNonQueryAsync();
            }

            foreach (var id in ids)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO groundwork_documents
                        (document_kind, storage_scope, id, schema_version, version, content_json, created_utc, updated_utc)
                    VALUES
                        ('configurationDocument', '__groundwork_global__', @id, '1', 1, '{"key":"legacy"}',
                         '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                    """;
                insert.Parameters.AddWithValue("@id", id);
                await insert.ExecuteNonQueryAsync();
            }
        }

        public async Task RemoveLegacyIdentityRowAsync(string id)
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM groundwork_documents WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> CountIdentityProjectionColumnsAsync(bool requireNotNull)
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'groundwork_documents'
                  AND column_name IN ('id_comparison_key', 'id_lookup_key')
                  AND (@requireNotNull = FALSE OR is_nullable = 'NO');
                """;
            command.Parameters.AddWithValue("@requireNotNull", requireNotNull);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task<bool> IdentitySchemaExistsAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('groundwork_document_identity_schema') IS NOT NULL;";
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async Task ForceLookupCollisionAsync(string retainedId, string requestedId)
        {
            await EnsureOpenAsync();
            string lookup;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT id_lookup_key FROM groundwork_documents WHERE id = @id;";
                read.Parameters.AddWithValue("id", requestedId);
                lookup = (string)(await read.ExecuteScalarAsync())!;
            }

            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DELETE FROM groundwork_documents WHERE id = @requestedId;
                UPDATE groundwork_documents SET id_lookup_key = @lookup WHERE id = @retainedId;
                """;
            corrupt.Parameters.AddWithValue("requestedId", requestedId);
            corrupt.Parameters.AddWithValue("retainedId", retainedId);
            corrupt.Parameters.AddWithValue("lookup", lookup);
            await corrupt.ExecuteNonQueryAsync();
        }

        public async Task<IReadOnlyList<string>> ReadDocumentPrimaryKeyColumnsAsync()
            => await ReadConstraintColumnsAsync("groundwork_documents", "PRIMARY KEY");

        public async Task<IReadOnlyList<string>> ReadIdentityLookupUniqueIndexColumnsAsync()
            => await ReadIndexColumnsAsync("groundwork_documents", "ux_groundwork_documents_identity_lookup");

        public async Task<IReadOnlyList<string>> ReadPortableUniqueIndexColumnsAsync()
            => await ReadIndexColumnsAsync("groundwork_document_indexes", "ux_groundwork_document_indexes_unique");

        public async Task<IReadOnlyList<string>> ReadOptimizedPrimaryKeyColumnsAsync()
            => await ReadConstraintColumnsAsync(
                Groundwork.Relational.Physicalization.RelationalPhysicalizationNames.TableName("configurationDocument"),
                "PRIMARY KEY");

        public async Task<IReadOnlyList<string>> ReadOptimizedUniqueIndexColumnsAsync()
        {
            var table = Groundwork.Relational.Physicalization.RelationalPhysicalizationNames.TableName("configurationDocument");
            await EnsureOpenAsync();
            await using var find = connection.CreateCommand();
            find.CommandText = """
                SELECT indexname
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = @table
                  AND indexdef LIKE 'CREATE UNIQUE INDEX%'
                  AND indexname NOT LIKE '%_pkey'
                ORDER BY indexname
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("@table", table);
            var name = (string?)await find.ExecuteScalarAsync();
            Assert.NotNull(name);
            return await ReadIndexColumnsAsync(table, name);
        }

        public async Task<string?> ReadStorageScopeCollationAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT collation_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'groundwork_documents'
                  AND column_name = 'storage_scope';
                """;
            return (string?)await command.ExecuteScalarAsync();
        }

        private async Task<IReadOnlyList<string>> ReadConstraintColumnsAsync(string table, string constraintType)
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_name = tc.constraint_name
                 AND kcu.table_schema = tc.table_schema
                WHERE tc.table_schema = 'public'
                  AND tc.table_name = @table
                  AND tc.constraint_type = @constraintType
                ORDER BY kcu.ordinal_position;
                """;
            command.Parameters.AddWithValue("@table", table);
            command.Parameters.AddWithValue("@constraintType", constraintType);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
            return columns;
        }

        private async Task<IReadOnlyList<string>> ReadIndexColumnsAsync(string table, string indexName)
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.attname
                FROM pg_class t
                JOIN pg_index i ON t.oid = i.indrelid
                JOIN pg_class ix ON ix.oid = i.indexrelid
                JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS key(attnum, ordinality) ON true
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = key.attnum
                WHERE t.relname = @table
                  AND ix.relname = @indexName
                ORDER BY key.ordinality;
                """;
            command.Parameters.AddWithValue("@table", table);
            command.Parameters.AddWithValue("@indexName", indexName);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
            return columns;
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();

        private async Task EnsureOpenAsync()
        {
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();
        }
    }
}
