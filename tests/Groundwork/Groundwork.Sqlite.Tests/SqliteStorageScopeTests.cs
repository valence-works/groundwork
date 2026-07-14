using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Groundwork.TestInfrastructure;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.Sqlite;
using System.Diagnostics.Metrics;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteStorageScopeTests
{
    [Fact]
    public async Task SatisfiesSharedStorageScopeBlackBoxContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var manifest = ScopedManifest();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider);

        await StorageScopeDocumentStoreConformance.VerifyAsync(
            manifest,
            (targetManifest, access) => Task.FromResult<IDocumentStore>(
                new SqliteDocumentStore(connection, targetManifest, access)));
    }

    [Fact]
    public async Task ScopedIdentitySurvivesProviderRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-scope-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var manifest = ScopedManifest();
        try
        {
            var first = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
            await first.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "restart", "1", """{"key":"restart"}"""));

            var restarted = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
            var other = await SqliteDocumentStoreFactory.CreateAsync(
                connectionString,
                manifest,
                SqliteTestManifests.Provider,
                DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));

            Assert.NotNull(await restarted.LoadAsync("configurationDocument", "restart"));
            Assert.Null(await other.LoadAsync("configurationDocument", "restart"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EveryDocumentPathUsesTheBoundScopeInsteadOfPayloadData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var manifest = ScopedManifest();
        await new SqliteGroundworkMaterializer(connection).MaterializeAsync(manifest, SqliteTestManifests.Provider);
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "id" },
            await ReadKeyColumns(connection, "PRAGMA table_info(groundwork_documents);", nameOrdinal: 1, keyOrdinal: 5));
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "id_lookup_key" },
            await ReadKeyColumns(connection, "PRAGMA index_info(ux_groundwork_documents_identity_lookup);", nameOrdinal: 2, keyOrdinal: 0));
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "index_name", "index_value" },
            await ReadKeyColumns(connection, "PRAGMA index_info(ux_groundwork_document_indexes_unique);", nameOrdinal: 2, keyOrdinal: 0));
        var projectionTable = RelationalPhysicalizationNames.TableName("configurationDocument");
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "document_id" },
            await ReadKeyColumns(connection, $"PRAGMA table_info({projectionTable});", nameOrdinal: 1, keyOrdinal: 5));
        var optimizedUniqueIndex = await ReadOptimizedUniqueIndexName(connection, projectionTable);
        Assert.Equal(
            new[] { "storage_scope", "p_by_key_e69a184def06" },
            await ReadKeyColumns(connection, $"PRAGMA index_info({optimizedUniqueIndex});", nameOrdinal: 2, keyOrdinal: 0));
        var a = Store(connection, manifest, "tenant-a");
        var b = Store(connection, manifest, "TENANT-A");
        var unicode = Store(connection, manifest, "租户-Å");
        var all = new SqliteDocumentStore(
            connection,
            manifest,
            DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("scope conformance")));
        const string kind = "configurationDocument";
        const string id = "same-id";
        const string uniqueKey = "same-key";

        var savedA = await a.SaveAsync(new SaveDocumentRequest(kind, id, "1", $$"""{"tenantId":"tenant-b","key":"{{uniqueKey}}","category":"A"}"""));
        var savedB = await b.SaveAsync(new SaveDocumentRequest(kind, id, "1", $$"""{"tenantId":"tenant-a","key":"{{uniqueKey}}","category":"B"}"""));
        var savedUnicode = await unicode.SaveAsync(new SaveDocumentRequest(kind, id, "1", $$"""{"tenantId":"tenant-a","key":"{{uniqueKey}}","category":"Unicode"}"""));
        await a.SaveAsync(new SaveDocumentRequest(kind, "only-a", "1", """{"tenantId":"tenant-b","key":"only-a","category":"A"}"""));

        Assert.Equal("tenant-a", savedA.Document!.Scope!.Value);
        Assert.Equal("TENANT-A", savedB.Document!.Scope!.Value);
        Assert.Equal("租户-Å", savedUnicode.Document!.Scope!.Value);
        Assert.Null(await b.LoadAsync(kind, "only-a"));
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.SaveAsync(new SaveDocumentRequest(
            kind, "only-a", "1", """{"key":"stolen"}""", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.DeleteAsync(new DeleteDocumentRequest(kind, "only-a"))).Status);
        Assert.NotNull(await a.LoadAsync(kind, "only-a"));

        Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", uniqueKey)));
        Assert.Single(await b.QueryAsync(new DocumentStoreQuery(kind, "by-key", uniqueKey)));
        Assert.Equal(3, (await all.QueryAsync(new DocumentStoreQuery(kind, "by-key", uniqueKey))).Count);
        Assert.Equal(2, (await a.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(1, (await b.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(4, (await all.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.True(await a.AnyAsync(new PortableDocumentQuery(kind)));
        Assert.Equal("tenant-a", (await a.FirstOrDefaultAsync(new PortableDocumentQuery(kind)))!.Scope!.Value);

        var stale = await b.SaveAsync(new SaveDocumentRequest(kind, "only-a", "1", """{"key":"cross-scope-index"}""", ExpectedVersion: 1));
        Assert.Equal(DocumentStoreWriteStatus.NotFound, stale.Status);
        Assert.Empty(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", "cross-scope-index")));

        await using var unitOfWork = await a.BeginAsync(DocumentCommitScope.Of(kind));
        await unitOfWork.SaveAsync(new SaveDocumentRequest(kind, "rolled-back", "1", """{"key":"rollback"}"""));
        await unitOfWork.RollbackAsync();
        Assert.Null(await a.LoadAsync(kind, "rolled-back"));
    }

    [Fact]
    public async Task InvalidAndPrivilegedAccessEmitLowCardinalityEvidenceBeforeIo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var observer = new RecordingObserver();
        var measurements = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == "Groundwork.Documents.StorageScope")
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        var globalManifest = SqliteTestManifests.MetadataManifest();
        var wrong = new SqliteDocumentStore(
            connection,
            globalManifest,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")),
            observer);

        var exception = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            wrong.LoadAsync("configurationDocument", "secret"));

        Assert.Equal(StorageScopeRejectionReason.GlobalAccessRequired, exception.Rejection.Reason);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Single(observer.Rejections);

        var missingScope = new SqliteDocumentStore(
            connection,
            ScopedManifest(),
            DocumentStoreAccess.Global,
            observer);
        var scopedRequired = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            missingScope.LoadAsync("configurationDocument", "secret"));
        Assert.Equal(StorageScopeRejectionReason.ScopedAccessRequired, scopedRequired.Rejection.Reason);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        var crossScope = new SqliteDocumentStore(
            connection,
            ScopedManifest(),
            DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("repair")),
            observer);
        Assert.Single(observer.PrivilegedAcquisitions);
        Assert.Equal("repair", observer.PrivilegedAcquisitions[0].Reason);
        var ambiguous = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            crossScope.LoadAsync("configurationDocument", "secret"));
        Assert.Equal(StorageScopeRejectionReason.TargetScopeRequired, ambiguous.Rejection.Reason);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.DoesNotContain(observer.Rejections.Select(x => x.ToString()), value => value.Contains("tenant-a", StringComparison.Ordinal));
        Assert.Contains(measurements, measurement => measurement.Instrument == "groundwork.document_store.privileged_sessions");
        Assert.Contains(measurements, measurement => measurement.Instrument == "groundwork.document_store.scope_rejections");
        Assert.DoesNotContain(
            measurements.SelectMany(measurement => measurement.Tags),
            tag => string.Equals(tag.Value?.ToString(), "tenant-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnitOfWorkRejectsMixedGlobalAndScopedUnitsBeforeIo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var scoped = ScopedManifest();
        var first = scoped.StorageUnits.Single();
        var mixed = scoped with
        {
            StorageUnits =
            [
                first,
                first with
                {
                    Identity = new StorageUnitIdentity("globalDocument"),
                    Tenancy = TenancyPolicy.Global
                }
            ]
        };
        var store = Store(connection, mixed, "tenant-a");

        var exception = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            store.BeginAsync(DocumentCommitScope.Of("configurationDocument", "globalDocument")));

        Assert.Equal(StorageScopeRejectionReason.MixedUnitOfWorkPolicy, exception.Rejection.Reason);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static StorageManifest ScopedManifest()
    {
        var manifest = SqliteTestManifests.MetadataManifest();
        return manifest with
        {
            Identity = new StorageManifestIdentity("scoped.metadata"),
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped,
                    Physicalization = PhysicalizationPolicy.Optimized
                }
            ]
        };
    }

    private static SqliteDocumentStore Store(SqliteConnection connection, StorageManifest manifest, string scope) =>
        new(connection, manifest, DocumentStoreAccess.Scoped(new StorageScope(scope)));

    private static async Task<IReadOnlyList<string>> ReadKeyColumns(
        SqliteConnection connection,
        string sql,
        int nameOrdinal,
        int keyOrdinal)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var columns = new List<(long Order, string Name)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var order = reader.GetInt64(keyOrdinal);
            if (sql.StartsWith("PRAGMA table_info", StringComparison.Ordinal) && order == 0)
                continue;
            columns.Add((order, reader.GetString(nameOrdinal)));
        }
        return columns.OrderBy(x => x.Order).Select(x => x.Name).ToArray();
    }

    private static async Task<string> ReadOptimizedUniqueIndexName(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
              AND tbl_name = @table
              AND sql LIKE 'CREATE UNIQUE INDEX%'
            ORDER BY name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@table", table);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private sealed class RecordingObserver : IStorageScopeObserver
    {
        public List<PrivilegedStorageSessionAudit> PrivilegedAcquisitions { get; } = [];
        public List<StorageScopeAccessRejection> Rejections { get; } = [];

        public void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit) => PrivilegedAcquisitions.Add(audit);

        public void ScopeAccessRejected(StorageScopeAccessRejection rejection) => Rejections.Add(rejection);
    }
}
