using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Xunit;

namespace Groundwork.TestInfrastructure;

/// <summary>The provider-independent black-box contract for storage-boundary scoping.</summary>
public static class StorageScopeDocumentStoreConformance
{
    public static async Task VerifyAsync(
        StorageManifest manifest,
        Func<StorageManifest, DocumentStoreAccess, Task<IDocumentStore>> createStore)
    {
        var aAccess = DocumentStoreAccess.Scoped(new StorageScope("tenant-a"));
        var bAccess = DocumentStoreAccess.Scoped(new StorageScope("TENANT-A"));
        var unicodeAccess = DocumentStoreAccess.Scoped(new StorageScope("租户-Å"));
        var privilegedAccess = DocumentStoreAccess.PrivilegedAcrossScopes(
            new PrivilegedStorageAccess("provider conformance"));
        var a = await createStore(manifest, aAccess);
        var b = await createStore(manifest, bAccess);
        var unicode = await createStore(manifest, unicodeAccess);
        var all = await createStore(manifest, privilegedAccess);
        var kind = manifest.StorageUnits.Single().Identity.Value;
        var suffix = Guid.NewGuid().ToString("N");
        var sharedId = $"same-id-{suffix}";
        var onlyA = $"only-a-{suffix}";
        var key = $"same-key-{suffix}";

        var savedA = await a.SaveAsync(new SaveDocumentRequest(
            kind, sharedId, "1", $$"""{"tenantId":"payload-b","key":"{{key}}","category":"scope","sort":"1"}"""));
        var savedB = await b.SaveAsync(new SaveDocumentRequest(
            kind, sharedId, "1", $$"""{"tenantId":"payload-a","key":"{{key}}","category":"scope","sort":"1"}"""));
        var savedUnicode = await unicode.SaveAsync(new SaveDocumentRequest(
            kind, sharedId, "1", $$"""{"tenantId":"payload-a","key":"{{key}}","category":"scope","sort":"1"}"""));
        await a.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"tenantId":"payload-b","key":"only-a-{{suffix}}","category":"scope","sort":"2"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, savedA.Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, savedB.Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, savedUnicode.Status);
        Assert.Equal("tenant-a", savedA.Document!.Scope!.Value);
        Assert.Equal("TENANT-A", savedB.Document!.Scope!.Value);
        Assert.Equal("租户-Å", savedUnicode.Document!.Scope!.Value);

        Assert.Null(await b.LoadAsync(kind, onlyA));
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"key":"wrong-update-{{suffix}}"}""", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.DeleteAsync(new DeleteDocumentRequest(kind, onlyA))).Status);
        Assert.NotNull(await a.LoadAsync(kind, onlyA));

        var updatedKey = $"only-a-updated-{suffix}";
        var staleKey = $"only-a-stale-{suffix}";
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await a.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"key":"{{updatedKey}}","category":"scope","sort":"2"}""", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await a.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"key":"{{staleKey}}","category":"scope","sort":"2"}""", ExpectedVersion: 1))).Status);
        Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", updatedKey)));
        Assert.Empty(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", staleKey)));

        Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
        Assert.Single(await b.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
        Assert.Single(await unicode.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
        Assert.Equal(3, (await all.QueryAsync(new DocumentStoreQuery(kind, "by-key", key))).Count);
        Assert.Equal(2, (await a.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(1, (await b.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(1, (await unicode.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.Equal(4, (await all.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
        Assert.True(await a.AnyAsync(new PortableDocumentQuery(kind)));
        Assert.Equal("tenant-a", (await a.FirstOrDefaultAsync(new PortableDocumentQuery(kind)))!.Scope!.Value);

        var exactId = $"exact-{suffix}";
        var spacedId = $"{exactId} ";
        var exactKey = $"exact-key-{suffix}";
        var spacedKey = $"{exactKey} ";
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await a.SaveAsync(new SaveDocumentRequest(
            kind, exactId, "1", $$"""{"key":"{{exactKey}}"}"""))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await a.SaveAsync(new SaveDocumentRequest(
            kind, spacedId, "1", $$"""{"key":"{{spacedKey}}"}"""))).Status);
        Assert.Equal(exactId, Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", exactKey))).Id);
        Assert.Equal(spacedId, Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", spacedKey))).Id);
        Assert.Equal(exactId, (await a.LoadAsync(kind, exactId))!.Id);
        Assert.Equal(spacedId, (await a.LoadAsync(kind, spacedId))!.Id);

        var duplicateInA = await a.SaveAsync(new SaveDocumentRequest(
            kind, $"duplicate-{suffix}", "1", $$"""{"key":"{{key}}"}"""));
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, duplicateInA.Status);

        var stale = await b.SaveAsync(new SaveDocumentRequest(
            kind, onlyA, "1", $$"""{"key":"cross-scope-index-{{suffix}}"}""", ExpectedVersion: 1));
        Assert.Equal(DocumentStoreWriteStatus.NotFound, stale.Status);
        Assert.Empty(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", $"cross-scope-index-{suffix}")));

        var rolledBackId = $"rolled-back-{suffix}";
        await using (var unitOfWork = await a.BeginAsync(DocumentCommitScope.Of(kind)))
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
                kind, rolledBackId, "1", $$"""{"key":"rollback-{{suffix}}"}"""))).Status);
            await unitOfWork.RollbackAsync();
        }
        Assert.Null(await a.LoadAsync(kind, rolledBackId));

        var scopeValidatedId = $"scope-validated-{suffix}";
        await using (var unitOfWork = await a.BeginAsync(DocumentCommitScope.Of(kind)))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => unitOfWork.SaveAsync(new SaveDocumentRequest(
                "undeclared-kind", $"outside-save-{suffix}", "1", "{}")));
            await Assert.ThrowsAsync<ArgumentException>(() => unitOfWork.DeleteAsync(new DeleteDocumentRequest(
                "undeclared-kind", $"outside-delete-{suffix}")));
            await Assert.ThrowsAsync<ArgumentException>(() => unitOfWork.LoadAsync(
                "undeclared-kind", $"outside-load-{suffix}"));

            Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
                kind, scopeValidatedId, "1", $$"""{"key":"scope-validated-{{suffix}}"}"""))).Status);
            await unitOfWork.CommitAsync();
        }
        Assert.NotNull(await a.LoadAsync(kind, scopeValidatedId));

        var poisonedId = $"poisoned-{suffix}";
        await using (var unitOfWork = await a.BeginAsync(DocumentCommitScope.Of(kind)))
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
                kind, poisonedId, "1", $$"""{"key":"poisoned-{{suffix}}"}"""))).Status);
            Assert.Equal(DocumentStoreWriteStatus.NotFound, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
                kind, $"missing-{suffix}", "1", "{}", ExpectedVersion: 1))).Status);

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.RollbackAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.LoadAsync(kind, sharedId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.DeleteAsync(new DeleteDocumentRequest(
                kind, sharedId)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.SaveAsync(new SaveDocumentRequest(
                kind, $"after-poison-{suffix}", "1", "{}")));
        }
        Assert.Null(await a.LoadAsync(kind, poisonedId));

        var restarted = await createStore(manifest, aAccess);
        Assert.NotNull(await restarted.LoadAsync(kind, sharedId));

        var maxScopeValue = new string('x', StorageScope.MaxValueLength);
        var maxScope = await createStore(
            manifest,
            DocumentStoreAccess.Scoped(new StorageScope(maxScopeValue)));
        var maxScopeId = $"max-scope-{suffix}";
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await maxScope.SaveAsync(new SaveDocumentRequest(
            kind, maxScopeId, "1", $$"""{"key":"max-scope-{{suffix}}"}"""))).Status);
        Assert.Equal(maxScopeValue, (await maxScope.LoadAsync(kind, maxScopeId))!.Scope!.Value);

        var scopedUnit = manifest.StorageUnits.Single();
        var globalKind = $"global-{suffix}";
        var mixedManifest = manifest with
        {
            StorageUnits =
            [
                scopedUnit,
                scopedUnit with
                {
                    Identity = new StorageUnitIdentity(globalKind),
                    Tenancy = TenancyPolicy.Global
                }
            ]
        };
        var mixed = await createStore(mixedManifest, aAccess);
        var mixedException = await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            mixed.BeginAsync(DocumentCommitScope.Of(kind, globalKind)));
        Assert.Equal(StorageScopeRejectionReason.MixedUnitOfWorkPolicy, mixedException.Rejection.Reason);
    }
}
