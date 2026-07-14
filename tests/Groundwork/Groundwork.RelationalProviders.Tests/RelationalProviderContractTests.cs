using System.Text.Json;
using Groundwork.Core.Manifests;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Documents.Scoping;
using Groundwork.Core.Scoping;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class RelationalProviderContractTests
{
    [Fact]
    public async Task UnicodeIdentityConflictPreservesAuthoritativeOriginal()
    {
        await using var harness = await CreateHarnessAsync(RelationalTestManifests.UnicodeIdentityManifest());
        var store = harness.Store;

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "Straße-Σς",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));
        var conflict = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "straße-σΣ",
            "1.0.0",
            """{"key":"replacement","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("Straße-Σς", conflict.AuthoritativeId);
        Assert.Contains("alpha", (await store.LoadAsync("configurationDocument", "Straße-Σς"))!.ContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeIdentityLoadAndDeleteReturnAuthoritativeOriginal()
    {
        await using var harness = await CreateHarnessAsync(RelationalTestManifests.UnicodeIdentityManifest());
        var store = harness.Store;
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            "Straße-Σς",
            "1.0.0",
            """{"key":"alpha","category":"system"}"""));

        var loaded = await store.LoadAsync("configurationDocument", "straße-σΣ");
        var deleted = await store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            "STRAßE-Σσ",
            ExpectedVersion: 1));

        Assert.Equal("Straße-Σς", loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("Straße-Σς", deleted.AuthoritativeId);
        Assert.Null(await store.LoadAsync("configurationDocument", "Straße-Σς"));
    }

    [Fact]
    public async Task SatisfiesSharedStorageScopeBlackBoxContract()
    {
        await using var harness = await CreateHarnessAsync();
        var manifest = RelationalTestManifests.ScopedManifest();

        await StorageScopeDocumentStoreConformance.VerifyAsync(
            manifest,
            harness.CreateStoreAsync);
    }

    [Fact]
    public async Task StorageScopeIsAnInvariantAcrossEveryDocumentPath()
    {
        await using var harness = await CreateHarnessAsync();
        var manifest = RelationalTestManifests.ScopedManifest();
        var scopeA = DocumentStoreAccess.Scoped(new StorageScope("tenant-a"));
        var scopeB = DocumentStoreAccess.Scoped(new StorageScope("TENANT-A"));
        var unicodeScope = DocumentStoreAccess.Scoped(new StorageScope("租户-Å"));
        var privileged = DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("provider conformance"));
        var a = await harness.CreateStoreAsync(manifest, scopeA);
        var b = await harness.CreateStoreAsync(manifest, scopeB);
        var unicode = await harness.CreateStoreAsync(manifest, unicodeScope);
        var all = await harness.CreateStoreAsync(manifest, privileged);
        var sqlServer = harness.ProviderName == "sqlserver";
        Assert.Equal(
            sqlServer
                ? ["document_kind_key", "storage_scope_key", "id_key"]
                : ["document_kind", "storage_scope", "id_lookup_key"],
            await harness.ReadDocumentPrimaryKeyColumnsAsync());
        Assert.Equal(
            sqlServer
                ? ["document_kind_key", "storage_scope_key", "id_lookup_key"]
                : ["document_kind", "storage_scope", "id_lookup_key"],
            await harness.ReadIdentityLookupUniqueIndexColumnsAsync());
        Assert.Equal(
            sqlServer
                ? ["document_kind_key", "storage_scope_key", "index_name_key", "index_value_key"]
                : ["document_kind", "storage_scope", "index_name", "index_value"],
            await harness.ReadPortableUniqueIndexColumnsAsync());
        Assert.Equal(
            sqlServer
                ? ["document_kind_key", "storage_scope_key", "document_id_key"]
                : ["document_kind", "storage_scope", "document_id"],
            await harness.ReadOptimizedPrimaryKeyColumnsAsync());
        Assert.Equal(
            sqlServer
                ? ["storage_scope_key", "p_by_key_e69a184def06_key"]
                : ["storage_scope", "p_by_key_e69a184def06"],
            await harness.ReadOptimizedUniqueIndexColumnsAsync());
        if (harness.ProviderName == "sqlserver")
            Assert.Equal("Latin1_General_100_BIN2", await harness.ReadStorageScopeCollationAsync());
        const string kind = "configurationDocument";
        var sharedId = NewId();
        var onlyA = NewId();
        var key = NewValue("same-unique-key");

        var savedA = await a.SaveAsync(new SaveDocumentRequest(kind, sharedId, "1", $$"""{"key":"{{key}}","category":"scope","sort":"1"}"""));
        var savedB = await b.SaveAsync(new SaveDocumentRequest(kind, sharedId, "1", $$"""{"key":"{{key}}","category":"scope","sort":"1"}"""));
        var savedUnicode = await unicode.SaveAsync(new SaveDocumentRequest(kind, sharedId, "1", $$"""{"key":"{{key}}","category":"scope","sort":"1"}"""));
        await a.SaveAsync(new SaveDocumentRequest(kind, onlyA, "1", $$"""{"key":"{{NewValue("only-a")}}","category":"scope","sort":"2"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, savedA.Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, savedB.Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, savedUnicode.Status);
        Assert.Equal("tenant-a", savedA.Document!.Scope!.Value);
        Assert.Equal("TENANT-A", savedB.Document!.Scope!.Value);
        Assert.Equal("租户-Å", savedUnicode.Document!.Scope!.Value);
        Assert.Null(await b.LoadAsync(kind, onlyA));
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"key":"{{NewValue("wrong-update")}}"}""", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.DeleteAsync(new DeleteDocumentRequest(kind, onlyA))).Status);
        Assert.NotNull(await a.LoadAsync(kind, onlyA));

        Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
        Assert.Single(await b.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
        Assert.Equal(3, (await all.QueryAsync(new DocumentStoreQuery(kind, "by-key", key))).Count);
        Assert.Equal(2, (await a.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(1, (await b.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(4, (await all.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.True(await a.AnyAsync(new PortableDocumentQuery(kind)));
        Assert.Equal("tenant-a", (await a.FirstOrDefaultAsync(new PortableDocumentQuery(kind)))!.Scope!.Value);

        var duplicateInA = await a.SaveAsync(new SaveDocumentRequest(kind, NewId(), "1", $$"""{"key":"{{key}}"}"""));
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, duplicateInA.Status);

        await using var unitOfWork = await a.BeginAsync(DocumentCommitScope.Of(kind));
        var rolledBackId = NewId();
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
            kind, rolledBackId, "1", $$"""{"key":"{{NewValue("rollback")}}"}"""))).Status);
        await unitOfWork.RollbackAsync();
        Assert.Null(await a.LoadAsync(kind, rolledBackId));

        var restarted = await harness.CreateStoreAsync(manifest, scopeA);
        Assert.NotNull(await restarted.LoadAsync(kind, sharedId));
    }

    [Fact]
    public async Task MaterializeCreatesSchemaHistoryIdempotently()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.MaterializeAsync();
        await harness.MaterializeAsync();

        Assert.Equal(1, await harness.CountSchemaHistoryRowsAsync());
    }

    [Fact]
    public async Task MaterializationRejectsDocumentIdentityPolicyDrift()
    {
        await using var harness = await CreateHarnessAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ApplyManifestAsync(RelationalTestManifests.UnicodeIdentityManifest()));

        Assert.Contains("configurationDocument", exception.Message, StringComparison.Ordinal);
        Assert.Contains("identity policy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveLoadUpdateQueryAndDeleteMaintainIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var store = harness.Store;
        var id = NewId();
        var firstKey = NewValue("alpha");
        var secondKey = NewValue("beta");

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{firstKey}}","category":"system","value":1}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);

        var loaded = await store.LoadAsync("configurationDocument", id);
        Assert.NotNull(loaded);
        using var loadedContent = JsonDocument.Parse(loaded.ContentJson);
        Assert.Equal(firstKey, loadedContent.RootElement.GetProperty("key").GetString());

        var byKey = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", firstKey));
        Assert.Single(byKey);

        var updated = await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{secondKey}}","category":"application","value":2}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(2, updated.Document!.Version);

        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", firstKey)));
        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
        Assert.Contains(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", "application")), document => document.Id == id);

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 2));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Null(await store.LoadAsync("configurationDocument", id));
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
    }

    [Fact]
    public async Task UndeclaredIndexQueryFailsClearly()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();

        var exception = await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "missing-index", NewValue("alpha"))));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("missing-index", exception.IndexName);
    }

    [Fact]
    public async Task UniqueIndexesAreEnforcedByProvider()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var key = NewValue("unique");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}"""));

        var duplicate = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, duplicate.Status);
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotUpdateDocumentOrIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var firstKey = NewValue("alpha");
        var secondKey = NewValue("beta");
        var staleKey = NewValue("gamma");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{firstKey}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{secondKey}}","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{staleKey}}","category":"system"}""",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", staleKey)));
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotDeleteDocumentOrIndexes()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var key = NewValue("beta");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{NewValue("alpha")}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}""",
            ExpectedVersion: 1));

        var stale = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", id));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", key)));
    }

    [Fact]
    public async Task ExpectedVersionZeroCreatesWhenAbsentAndConflictsWhenPresent()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var createdKey = NewValue("created");
        var clobberKey = NewValue("clobber");

        // Create-only: expected version 0 against an absent document inserts version 1.
        var created = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{createdKey}}","category":"system"}""",
            ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, created.Status);
        Assert.Equal(1, created.Document!.Version);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", createdKey)));

        // Create-only against an existing document is refused and mutates neither document nor indexes.
        var refused = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{clobberKey}}","category":"system"}""",
            ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, refused.Status);
        var loaded = await harness.Store.LoadAsync("configurationDocument", id);
        Assert.Equal(1, loaded!.Version);
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", createdKey)));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", clobberKey)));
    }

    [Fact]
    public async Task PositiveExpectedVersionAgainstAbsentDocumentIsNotFoundAndWritesNothing()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var id = NewId();
        var key = NewValue("ghost");

        // A positive expected version can never match an absent document: NotFound, nothing persisted.
        var missing = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}""",
            ExpectedVersion: 3));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, missing.Status);
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", id));
        Assert.Empty(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", key)));

        // Delete semantics are unchanged: expected version 0 against an absent document stays NotFound.
        var deleteMissing = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.NotFound, deleteMissing.Status);
    }

    [Fact]
    public async Task FactoryStoreUnitOfWorkCommitsAndRollsBackAtomically()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var scope = DocumentCommitScope.Of("configurationDocument");
        var committed = NewId();
        var rolledBack = NewId();

        await using (var unitOfWork = await harness.Store.BeginAsync(scope))
        {
            await unitOfWork.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                committed,
                "1.0.0",
                $$"""{"key":"{{NewValue("commit")}}","category":"system"}"""));
            await unitOfWork.CommitAsync();
        }

        await using (var unitOfWork = await harness.Store.BeginAsync(scope))
        {
            await unitOfWork.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                rolledBack,
                "1.0.0",
                $$"""{"key":"{{NewValue("rollback")}}","category":"system"}"""));
            await unitOfWork.RollbackAsync();
        }

        Assert.Equal(TransactionBoundary.CrossUnitAtomic, harness.Store.TransactionBoundary);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", committed));
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", rolledBack));
    }

    protected abstract Task<IRelationalProviderHarness> CreateHarnessAsync(StorageManifest? manifest = null);

    [Fact]
    public async Task AddedIndexBackfillsPreexistingDocuments()
    {
        await using var harness = await CreateHarnessAsync();
        await AssertAddedIndexBackfillsAsync(harness, RelationalTestManifests.MetadataManifest());
    }

    [Fact]
    public async Task AddedIndexBackfillsPreexistingDocumentsAcrossManifestVersionBump()
    {
        await using var harness = await CreateHarnessAsync();
        var bumped = RelationalTestManifests.MetadataManifest() with { Version = new StorageManifestVersion("1.1.0") };
        await AssertAddedIndexBackfillsAsync(harness, bumped);
    }

    private static async Task AssertAddedIndexBackfillsAsync(IRelationalProviderHarness harness, StorageManifest withCategory)
    {
        // Phase 1: materialize a manifest whose unit has no "by-category" index and save documents against it.
        var withoutCategory = RelationalTestManifests.WithoutIndex("by-category") with { Version = withCategory.Version };
        var initialStore = await harness.ApplyManifestAsync(withoutCategory);

        var systemA = NewId();
        var systemB = NewId();
        var other = NewId();
        var tag = NewValue("cat");
        var otherTag = NewValue("cat");

        await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", systemA, "1.0.0", $$"""{"key":"{{NewValue("k")}}","category":"{{tag}}"}"""));
        await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", systemB, "1.0.0", $$"""{"key":"{{NewValue("k")}}","category":"{{tag}}"}"""));
        await initialStore.SaveAsync(new SaveDocumentRequest("configurationDocument", other, "1.0.0", $$"""{"key":"{{NewValue("k")}}","category":"{{otherTag}}"}"""));

        // Phase 2: add the "by-category" index to the unit that already holds documents; backfill must run.
        var store = await harness.ApplyManifestAsync(withCategory);

        var byTag = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", tag));
        Assert.Equal(new[] { systemA, systemB }.OrderBy(id => id), byTag.Select(document => document.Id).OrderBy(id => id));

        var byOtherTag = await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-category", otherTag));
        Assert.Equal(new[] { other }, byOtherTag.Select(document => document.Id));
    }

    [Fact]
    public async Task ClosedQueryHonoursClosedContractServerSide()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.MaterializeAsync();
        var store = harness.Store;
        var tag = NewValue("cq");

        var c1 = NewId();
        var c2 = NewId();
        var c3 = NewId();
        var c4 = NewId();

        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c1, "1.0.0", $$"""{"key":"Alpha-{{c1}}","category":"{{tag}}","sort":"001"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c2, "1.0.0", $$"""{"key":"beta-{{c2}}","category":"{{tag}}","sort":"002"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c3, "1.0.0", $$"""{"key":"alpha-{{c3}}","category":"{{tag}}","sort":"003"}"""));
        await store.SaveAsync(new SaveDocumentRequest("configurationDocument", c4, "1.0.0", $$"""{"key":"Gamma-{{c4}}","category":"{{tag}}","sort":"004"}"""));

        var tagClause = QueryClause.Of(QueryComparison.Equal("by-category", tag));

        // In: set membership over the category tag isolates this test's documents.
        var inResult = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [QueryClause.Of(QueryComparison.In("by-category", [tag]))]));
        Assert.Equal(4, inResult.TotalCount);

        // Empty In => no match.
        var emptyIn = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [QueryClause.Of(QueryComparison.In("by-category", Array.Empty<string>()))]));
        Assert.Equal(0, emptyIn.TotalCount);

        // Contains is case-insensitive: "ALPHA" matches "Alpha-..." and "alpha-...".
        var contains = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.Of(QueryComparison.Contains("by-key", "ALPHA"))]));
        Assert.Equal(new[] { c1, c3 }.OrderBy(x => x), contains.Documents.Select(d => d.Id).OrderBy(x => x));

        // OR within a clause, AND across clauses.
        var orResult = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [
                tagClause,
                QueryClause.AnyOf(
                    QueryComparison.Contains("by-key", "Gamma"),
                    QueryComparison.Contains("by-key", "beta"))
            ]));
        Assert.Equal(new[] { c2, c4 }.OrderBy(x => x), orResult.Documents.Select(d => d.Id).OrderBy(x => x));

        // Constant-false clause => empty.
        var none = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.MatchNone]));
        Assert.Equal(0, none.TotalCount);

        // Ordering + offset paging + total count.
        var page = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause],
            order: new QueryOrder("by-sort"),
            skip: 1,
            take: 2));
        Assert.Equal(new[] { c2, c3 }, page.Documents.Select(d => d.Id));
        Assert.Equal(4, page.TotalCount);

        var descending = await store.QueryAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause],
            order: new QueryOrder("by-sort", Descending: true)));
        Assert.Equal(new[] { c4, c3, c2, c1 }, descending.Documents.Select(d => d.Id));

        // FirstOrDefault honours ordering; Any reports existence.
        var first = await store.FirstOrDefaultAsync(new PortableDocumentQuery(
            "configurationDocument", [tagClause], order: new QueryOrder("by-sort")));
        Assert.Equal(c1, first!.Id);

        Assert.True(await store.AnyAsync(new PortableDocumentQuery("configurationDocument", [tagClause])));
        Assert.False(await store.AnyAsync(new PortableDocumentQuery(
            "configurationDocument",
            [tagClause, QueryClause.Of(QueryComparison.Equal("by-key", "no-such-key"))])));
    }

    private static string NewId() => $"doc-{Guid.NewGuid():N}";

    private static string NewValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

public interface IRelationalProviderHarness : IAsyncDisposable
{
    string ProviderName { get; }
    IDocumentStore Store { get; }
    Task MaterializeAsync();
    Task<IDocumentStore> ApplyManifestAsync(StorageManifest manifest);
    Task<IDocumentStore> CreateStoreAsync(StorageManifest manifest, DocumentStoreAccess access);
    Task<IReadOnlyList<string>> ReadDocumentPrimaryKeyColumnsAsync();
    Task<IReadOnlyList<string>> ReadIdentityLookupUniqueIndexColumnsAsync();
    Task<IReadOnlyList<string>> ReadPortableUniqueIndexColumnsAsync();
    Task<IReadOnlyList<string>> ReadOptimizedPrimaryKeyColumnsAsync();
    Task<IReadOnlyList<string>> ReadOptimizedUniqueIndexColumnsAsync();
    Task<string?> ReadStorageScopeCollationAsync();
    Task<long> CountSchemaHistoryRowsAsync();
}
