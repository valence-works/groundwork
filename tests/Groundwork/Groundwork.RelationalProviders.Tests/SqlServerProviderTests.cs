using System.Data.Common;
using Groundwork.Documents.Store;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.Materialization;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerProviderTests : RelationalProviderContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU18-ubuntu-22.04").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public void DocumentStoreConstructionRequiresFactoryAdmission() =>
        Assert.Empty(typeof(SqlServerDocumentStore).GetConstructors());

    [Fact]
    public async Task FactoryRejectsInvalidManifestBeforeCreatingAnyConnection()
    {
        var materializationConnections = 0;
        var operationConnections = 0;
        var manifest = RelationalTestManifests.MetadataManifest() with { StorageUnits = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerDocumentStoreFactory.CreateAsync(
                "Server=never-opened",
                manifest,
                RelationalTestManifests.SqlServerProvider,
                () =>
                {
                    materializationConnections++;
                    return new SqlConnection();
                },
                () =>
                {
                    operationConnections++;
                    return new SqlConnection();
                },
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global));

        Assert.Equal(0, materializationConnections);
        Assert.Equal(0, operationConnections);
    }

    [Fact]
    public async Task MaximumLegalOpaqueValuesFitNativeHashedKeys()
    {
        var template = RelationalTestManifests.MetadataManifest();
        var kind = new string('k', 450);
        var indexName = new string('n', 450);
        var unit = template.StorageUnits.Single();
        var index = unit.Indexes.Single(x => x.Identity == "by-key") with { Identity = indexName };
        var manifest = template with
        {
            Identity = new Groundwork.Core.Manifests.StorageManifestIdentity("max-key-values"),
            StorageUnits =
            [
                unit with
                {
                    Identity = new Groundwork.Core.Manifests.StorageUnitIdentity(kind),
                    Tenancy = Groundwork.Core.Manifests.TenancyPolicy.Scoped,
                    Indexes = [index],
                    Queries = [],
                    Physicalization = Groundwork.Core.Manifests.PhysicalizationPolicy.Optimized
                }
            ]
        };
        var scope = new string('s', Groundwork.Core.Scoping.StorageScope.MaxValueLength);
        var store = await SqlServerDocumentStoreFactory.CreateAsync(
            container.GetConnectionString(),
            manifest,
            RelationalTestManifests.SqlServerProvider,
            Groundwork.Documents.Scoping.DocumentStoreAccess.Scoped(new Groundwork.Core.Scoping.StorageScope(scope)));
        var id = new string('d', 450);
        var firstValue = new string('v', 450);
        var secondValue = new string('w', 450);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            kind, id, "1", $$"""{"key":"{{firstValue}}"}"""))).Status);
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery(kind, indexName, firstValue)));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            kind, id, "1", $$"""{"key":"{{secondValue}}"}""", ExpectedVersion: 1))).Status);
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery(kind, indexName, firstValue)));
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery(kind, indexName, secondValue)));

        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        Assert.True(await ReadLargestDeclaredIndexKeyBytesAsync(connection) <= 900);
    }

    [Fact]
    public async Task MaterializationRejectsDisabledIdentityLookupIndex()
    {
        var manifest = RelationalTestManifests.MetadataManifest();
        var connectionString = container.GetConnectionString();
        await SqlServerDocumentStoreFactory.CreateAsync(
            connectionString,
            manifest,
            RelationalTestManifests.SqlServerProvider,
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "ALTER INDEX ux_groundwork_documents_identity_lookup ON groundwork_documents DISABLE;";
            await disable.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlServerGroundworkMaterializer(connection).MaterializeAsync(
                manifest,
                RelationalTestManifests.SqlServerProvider));

        Assert.Contains("identity lookup index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> ReadLargestDeclaredIndexKeyBytesAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(key_bytes)
            FROM (
                SELECT SUM(c.max_length) AS key_bytes
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE OBJECT_NAME(i.object_id) LIKE 'groundwork%'
                  AND ic.key_ordinal > 0
                GROUP BY i.object_id, i.index_id
            ) keys;
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task IndependentOperationsUseTheProviderPoolWithoutGlobalSerialization()
    {
        var builder = new SqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 2 };
        var blockerBuilder = new SqlConnectionStringBuilder(builder.ConnectionString) { Pooling = false };
        var twoConnectionsOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var manifest = RelationalTestManifests.MetadataManifest();
        var store = await SqlServerDocumentStoreFactory.CreateAsync(
            builder.ConnectionString,
            manifest,
            RelationalTestManifests.SqlServerProvider,
            () => new SqlConnection(builder.ConnectionString),
            () =>
            {
                var connection = new SqlConnection(builder.ConnectionString);
                connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == System.Data.ConnectionState.Open && Interlocked.Increment(ref opened) == 2)
                        twoConnectionsOpened.TrySetResult();
                };
                return connection;
            },
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        var blocker = await RelationalSessionPoolPressure.BlockSqlServerDocumentsAsync(blockerBuilder.ConnectionString);

        await RelationalSessionPoolPressure.AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
            store,
            twoConnectionsOpened.Task,
            blocker);
    }

    protected override async Task<IRelationalProviderHarness> CreateHarnessAsync(Groundwork.Core.Manifests.StorageManifest? manifest = null)
    {
        var connectionString = container.GetConnectionString();
        var connection = new SqlConnection(connectionString);
        manifest ??= RelationalTestManifests.MetadataManifest();
        var store = await SqlServerDocumentStoreFactory.CreateAsync(
            connectionString,
            manifest,
            RelationalTestManifests.SqlServerProvider,
            Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        return new SqlServerProviderHarness(connection, store, manifest, connectionString);
    }

    private sealed class SqlServerProviderHarness(
        SqlConnection connection,
        IDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        string connectionString)
        : IRelationalProviderHarness
    {
        public string ProviderName => "sqlserver";

        public IDocumentStore Store { get; } = store;

        public Task MaterializeAsync() =>
            new SqlServerGroundworkMaterializer(connection).MaterializeAsync(manifest, RelationalTestManifests.SqlServerProvider);

        public async Task<IDocumentStore> ApplyManifestAsync(Groundwork.Core.Manifests.StorageManifest targetManifest)
        {
            return await SqlServerDocumentStoreFactory.CreateAsync(
                connectionString,
                targetManifest,
                RelationalTestManifests.SqlServerProvider,
                Groundwork.Documents.Scoping.DocumentStoreAccess.Global);
        }

        public async Task<IDocumentStore> CreateStoreAsync(
            Groundwork.Core.Manifests.StorageManifest targetManifest,
            Groundwork.Documents.Scoping.DocumentStoreAccess access) =>
            await SqlServerDocumentStoreFactory.CreateAsync(
                connectionString,
                targetManifest,
                RelationalTestManifests.SqlServerProvider,
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
            command.Parameters.AddWithValue("@providerName", RelationalTestManifests.SqlServerProvider.Name);
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
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_groundwork_documents_identity_lookup' AND object_id = OBJECT_ID(N'groundwork_documents'))
                        DROP INDEX ux_groundwork_documents_identity_lookup ON groundwork_documents;
                    """;
                await reset.ExecuteNonQueryAsync();
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var concurrent = new SqlConnection(connectionString);
                await new SqlServerGroundworkMaterializer(concurrent).MaterializeAsync(
                    manifest,
                    RelationalTestManifests.SqlServerProvider);
            }));
        }

        public async Task ReplaceIdentityLookupWithFilteredIndexAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ux_groundwork_documents_identity_lookup ON groundwork_documents;
                CREATE UNIQUE INDEX ux_groundwork_documents_identity_lookup
                ON groundwork_documents(document_kind_key, storage_scope_key, id_lookup_key)
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
                    DROP INDEX ux_groundwork_documents_identity_lookup ON groundwork_documents;
                    DROP TABLE groundwork_document_identity_schema;
                    ALTER TABLE groundwork_documents DROP COLUMN id_comparison_key, id_lookup_key;
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
                        (N'configurationDocument', N'__groundwork_global__', @id, N'1', 1, N'{"key":"legacy"}',
                         N'2026-01-01T00:00:00Z', N'2026-01-01T00:00:00Z');
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
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'groundwork_documents')
                  AND name IN (N'id_comparison_key', N'id_lookup_key')
                  AND (@requireNotNull = 0 OR is_nullable = 0);
                """;
            command.Parameters.AddWithValue("@requireNotNull", requireNotNull);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task<bool> IdentitySchemaExistsAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'groundwork_document_identity_schema', N'U') IS NULL THEN 0 ELSE 1 END;";
            return Convert.ToBoolean(await command.ExecuteScalarAsync());
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
            => await ReadIndexColumnsAsync("groundwork_documents", primaryKey: true);

        public async Task<IReadOnlyList<string>> ReadIdentityLookupUniqueIndexColumnsAsync()
            => await ReadIndexColumnsAsync("groundwork_documents", indexName: "ux_groundwork_documents_identity_lookup");

        public async Task<IReadOnlyList<string>> ReadPortableUniqueIndexColumnsAsync()
            => await ReadIndexColumnsAsync("groundwork_document_indexes", indexName: "ux_groundwork_document_indexes_unique");

        public async Task<IReadOnlyList<string>> ReadOptimizedPrimaryKeyColumnsAsync()
            => await ReadIndexColumnsAsync(
                Groundwork.Relational.Physicalization.RelationalPhysicalizationNames.TableName("configurationDocument"),
                primaryKey: true);

        public async Task<IReadOnlyList<string>> ReadOptimizedUniqueIndexColumnsAsync()
            => await ReadIndexColumnsAsync(
                Groundwork.Relational.Physicalization.RelationalPhysicalizationNames.TableName("configurationDocument"),
                uniqueNonPrimary: true);

        public async Task<string?> ReadStorageScopeCollationAsync()
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.collation_name
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID(N'groundwork_documents')
                  AND c.name = N'storage_scope';
                """;
            return (string?)await command.ExecuteScalarAsync();
        }

        private async Task<IReadOnlyList<string>> ReadIndexColumnsAsync(
            string table,
            string? indexName = null,
            bool primaryKey = false,
            bool uniqueNonPrimary = false)
        {
            await EnsureOpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'{table}')
                  AND {(indexName is not null ? $"i.name = N'{indexName}'" : primaryKey ? "i.is_primary_key = 1" : uniqueNonPrimary ? "i.is_unique = 1 AND i.is_primary_key = 0" : "1 = 0")}
                ORDER BY ic.key_ordinal;
                """;
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
