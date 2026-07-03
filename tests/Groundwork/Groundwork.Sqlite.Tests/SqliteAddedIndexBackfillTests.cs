using Groundwork.Core.Manifests;
using Groundwork.Documents.Store;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteAddedIndexBackfillTests
{
    [Fact]
    public async Task AddingPortableIndexBackfillsPreexistingDocuments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await SeedByKeyOnlyAsync(connection);

        // Add the "by-category" index to a unit that already holds documents (same manifest version).
        var withCategory = WithIndexes(SqliteTestManifests.MetadataManifest(), "by-key", "by-category");
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(withCategory, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(connection, withCategory);

        await AssertBackfilledAsync(store);
    }

    [Fact]
    public async Task AddingPortableIndexBackfillsPreexistingDocumentsAcrossManifestVersionBump()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await SeedByKeyOnlyAsync(connection);

        // Mirror the downstream Elsa probe: the index arrives together with a manifest version bump.
        var bumped = WithIndexes(SqliteTestManifests.MetadataManifest(), "by-key", "by-category") with
        {
            Version = new StorageManifestVersion("1.1.0")
        };
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(bumped, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(connection, bumped);

        await AssertBackfilledAsync(store);
    }

    [Fact]
    public async Task ReMaterializingAddedIndexRemainsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await SeedByKeyOnlyAsync(connection);

        var withCategory = WithIndexes(SqliteTestManifests.MetadataManifest(), "by-key", "by-category");
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(withCategory, SqliteTestManifests.Provider);
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(withCategory, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(connection, withCategory);

        await AssertBackfilledAsync(store);
        Assert.Equal(2, await CountIndexRowsAsync(connection, "by-category", "system"));
    }

    private static async Task SeedByKeyOnlyAsync(SqliteConnection connection)
    {
        var byKeyOnly = WithIndexes(SqliteTestManifests.MetadataManifest(), "by-key");
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(byKeyOnly, SqliteTestManifests.Provider);
        var store = new SqliteDocumentStore(connection, byKeyOnly);

        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "doc-1", "1.0.0", """{"key":"alpha","category":"system"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "doc-2", "1.0.0", """{"key":"beta","category":"system"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", "doc-3", "1.0.0", """{"key":"gamma","category":"other"}"""));

        // The new index does not exist yet, so no by-category rows are written at save time.
        Assert.Equal(0, await CountIndexRowsAsync(connection, "by-category", "system"));
    }

    private static async Task AssertBackfilledAsync(SqliteDocumentStore store)
    {
        var system = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "system"));
        Assert.Equal(new[] { "doc-1", "doc-2" }, system.Select(document => document.Id).OrderBy(id => id));

        var other = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "other"));
        Assert.Equal(new[] { "doc-3" }, other.Select(document => document.Id));

        // The pre-existing "by-key" index is unaffected by the additive change.
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", "alpha")));
    }

    private static async Task<long> CountIndexRowsAsync(SqliteConnection connection, string indexName, string indexValue)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM groundwork_document_indexes
            WHERE document_kind = $kind AND index_name = $index AND index_value = $value;
            """;
        command.Parameters.AddWithValue("$kind", "configurationDocument");
        command.Parameters.AddWithValue("$index", indexName);
        command.Parameters.AddWithValue("$value", indexValue);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static StorageManifest WithIndexes(StorageManifest manifest, params string[] indexNames)
    {
        var kept = indexNames.ToHashSet(StringComparer.Ordinal);
        var unit = manifest.StorageUnits.Single();
        return manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    Indexes = unit.Indexes.Where(index => kept.Contains(index.Identity)).ToList(),
                    Queries = unit.Queries.Where(query => kept.Contains(query.IndexIdentity)).ToList()
                }
            ]
        };
    }
}
