using Groundwork.Core.Manifests;
using Groundwork.Core.Indexing;
using Groundwork.Core.Physicalization;
using Groundwork.Documents.Store;
using Groundwork.Relational.Physicalization;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteOptimizedPhysicalizationTests
{
    [Fact]
    public async Task OptimizedUnitsCreateMaintainAndQueryProjectionTables()
    {
        await using var harness = await SqliteOptimizedHarness.Create();
        var projectionTable = RelationalPhysicalizationNames.TableName(harness.Unit);

        Assert.True(await harness.TableExistsAsync(projectionTable));

        var saved = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(("alpha", "system", 1L), await harness.LoadProjectionAsync("doc-1"));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));

        var updated = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"application"}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(("beta", "application", 2L), await harness.LoadProjectionAsync("doc-1"));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "beta")));
    }

    [Fact]
    public async Task StaleWritesDoNotUpdateOptimizedProjectionRows()
    {
        await using var harness = await SqliteOptimizedHarness.Create();
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var stale = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"beta","category":"application"}""",
            ExpectedVersion: 2));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(("alpha", "system", 1L), await harness.LoadProjectionAsync("doc-1"));
    }

    [Fact]
    public async Task AdditivePhysicalizedIndexChangesAddColumnsAndBackfillExistingDocuments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var firstManifest = WithPhysicalizedIndexes(SqliteTestManifests.MetadataManifest(), "by-key");
        var firstUnit = firstManifest.StorageUnits.Single();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(firstManifest, SqliteTestManifests.Provider);
        var firstStore = new SqliteDocumentStore(connection, firstManifest);

        await firstStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "doc-1",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var secondManifest = WithPhysicalizedIndexes(SqliteTestManifests.MetadataManifest(), "by-key", "by-category");
        var secondUnit = secondManifest.StorageUnits.Single();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(secondManifest, SqliteTestManifests.Provider);
        var secondStore = new SqliteDocumentStore(connection, secondManifest);
        var categoryField = PhysicalizationProjection.EligibleFields(secondUnit).Single(field => field.Name == "by-category");
        var categoryColumn = RelationalPhysicalizationNames.ColumnName(categoryField);

        Assert.True(await ColumnExistsAsync(connection, RelationalPhysicalizationNames.TableName(secondUnit), categoryColumn));
        Assert.Equal("system", await LoadProjectionValueAsync(connection, secondUnit, categoryColumn, "doc-1"));
        Assert.Single(await secondStore.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "system")));
    }

    private sealed class SqliteOptimizedHarness : IAsyncDisposable
    {
        private SqliteOptimizedHarness(SqliteConnection connection, SqliteDocumentStore store, StorageUnit unit)
        {
            Connection = connection;
            Store = store;
            Unit = unit;
        }

        private SqliteConnection Connection { get; }
        public SqliteDocumentStore Store { get; }
        public StorageUnit Unit { get; }

        public static async Task<SqliteOptimizedHarness> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            var manifest = SqliteTestManifests.MetadataManifest();
            var unit = manifest.StorageUnits.Single() with { Physicalization = PhysicalizationPolicy.Optimized };
            manifest = manifest with { StorageUnits = [unit] };
            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider);
            return new SqliteOptimizedHarness(connection, new SqliteDocumentStore(connection, manifest), unit);
        }

        public async Task<bool> TableExistsAsync(string tableName)
        {
            await using var command = Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
            command.Parameters.AddWithValue("$table", tableName);
            return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
        }

        public async Task<(string Key, string Category, long Version)> LoadProjectionAsync(string documentId)
        {
            var fields = PhysicalizationProjection.EligibleFields(Unit).ToDictionary(field => field.Name, StringComparer.Ordinal);
            var keyColumn = RelationalPhysicalizationNames.ColumnName(fields["by-key"]);
            var categoryColumn = RelationalPhysicalizationNames.ColumnName(fields["by-category"]);
            await using var command = Connection.CreateCommand();
            command.CommandText = $"""
                SELECT {keyColumn}, {categoryColumn}, document_version
                FROM {RelationalPhysicalizationNames.TableName(Unit)}
                WHERE document_kind = $kind AND document_id = $id;
                """;
            command.Parameters.AddWithValue("$kind", Unit.Identity.Value);
            command.Parameters.AddWithValue("$id", documentId);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private static StorageManifest WithPhysicalizedIndexes(StorageManifest manifest, params string[] indexNames)
    {
        var physicalized = indexNames.ToHashSet(StringComparer.Ordinal);
        var unit = manifest.StorageUnits.Single();
        return manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    Indexes = unit.Indexes
                        .Select(index => index with
                        {
                            Physicalization = physicalized.Contains(index.Identity)
                                ? IndexPhysicalizationPolicy.Optimized
                                : IndexPhysicalizationPolicy.Portable
                        })
                        .ToList()
                }
            ]
        };
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == columnName)
                return true;
        }

        return false;
    }

    private static async Task<string> LoadProjectionValueAsync(
        SqliteConnection connection,
        StorageUnit unit,
        string columnName,
        string documentId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {columnName}
            FROM {RelationalPhysicalizationNames.TableName(unit)}
            WHERE document_kind = $kind AND document_id = $id;
            """;
        command.Parameters.AddWithValue("$kind", unit.Identity.Value);
        command.Parameters.AddWithValue("$id", documentId);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }
}
