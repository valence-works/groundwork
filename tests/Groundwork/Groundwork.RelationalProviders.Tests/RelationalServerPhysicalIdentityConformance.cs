using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Documents;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public abstract class RelationalServerPhysicalIdentityConformance : RelationalPhysicalStorageConformance
{
    protected abstract Task<RelationalServerIdentityFixture> CreateIdentityAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal);

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseUsesRetainedOriginalForEquivalentSpelling(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(Save("Configuration-One", "tools", 0))).Status);
        var loaded = await fixture.Documents.LoadAsync("configurationDocument", "configuration-one");
        var conflict = await fixture.Documents.SaveAsync(Save("configuration-one", "gadgets", 1));

        Assert.Equal("Configuration-One", loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("Configuration-One", conflict.AuthoritativeId);
        Assert.Contains("\"category\":\"tools\"", loaded.ContentJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task UnicodeIgnoreCaseDeleteUsesEquivalentSpellingWithoutBypassingOcc(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await fixture.Documents.SaveAsync(Save("Configuration-One", "tools", 0));

        var stale = await fixture.Documents.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "configuration-one", ExpectedVersion: 2));
        var deleted = await fixture.Documents.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "configuration-one", ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("Configuration-One", deleted.AuthoritativeId);
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "Configuration-One"));
    }

    [Fact]
    public async Task UnicodeIgnoreCaseSupportsSupplementaryPlaneIdentitySpelling()
    {
        await using var fixture = await CreateIdentityAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var retained = $"document-{char.ConvertFromUtf32(0x10428)}";
        var equivalent = $"document-{char.ConvertFromUtf32(0x10400)}";

        await fixture.Documents.SaveAsync(Save(retained, "tools", 0));
        var loaded = await fixture.Documents.LoadAsync("configurationDocument", equivalent);
        var conflict = await fixture.Documents.SaveAsync(Save(equivalent, "gadgets", 1));

        Assert.Equal(retained, loaded!.Id);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal(retained, conflict.AuthoritativeId);
    }

    [Fact]
    public async Task SchemaRetainsOriginalComparisonAndLookupWhileKeyingOnLookup()
    {
        await using var fixture = await CreateIdentityAsync(PhysicalStorageForm.SharedDocuments);

        AssertIdentitySchema(
            await fixture.ReadIdentitySchemaAsync(false),
            fixture.Route.Envelope.Identity,
            fixture.PhysicalKeyColumn);
        AssertIdentitySchema(
            await fixture.ReadIdentitySchemaAsync(true),
            fixture.Route.LinkedRelationship!.Identity,
            fixture.PhysicalKeyColumn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRejectsMissingComparisonEvidence(bool linked)
    {
        await using var fixture = await CreateIdentityAsync(
            linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable);
        await fixture.DropComparisonEvidenceAsync(linked);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(fixture.RestartAsync);

        Assert.Contains(
            linked
                ? fixture.Route.LinkedRelationship!.Identity.ComparisonKey.Identifier
                : fixture.Route.Envelope.Identity.ComparisonKey.Identifier,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task ConcurrentEquivalentSpellingCreatesExactlyOneAuthoritativeDocument(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form, StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        fixture.Store.WriteInterceptor = async (point, operation, _, _, cancellationToken) =>
        {
            if (operation != RelationalPhysicalWriteOperation.Save ||
                point != fixture.RaceSynchronizationPoint)
                return;
            if (Interlocked.Increment(ref arrivals) == 2)
                release.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var first = fixture.Documents.SaveAsync(Save("Configuration-Race", "tools", 0));
        var second = fixture.Documents.SaveAsync(Save("configuration-race", "tools", 0));
        var results = await Task.WhenAll(first, second);
        fixture.Store.WriteInterceptor = null;

        var saved = Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.Saved);
        var conflict = Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.IdentityConflict);
        Assert.Equal(saved.Document!.Id, conflict.AuthoritativeId);
        Assert.Equal(saved.Document.Id, (await fixture.Documents.LoadAsync(
            "configurationDocument", "CONFIGURATION-RACE"))!.Id);
        Assert.Equal(1, await fixture.Queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            resultOperation: BoundedQueryResultOperation.Count)));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task LookupCollisionFailsLoadSaveAndDeleteClosed(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form);
        await fixture.Documents.SaveAsync(Save("Retained-Id", "tools", 0));
        const string requestedId = "Requested-Id";
        var lookupKey = fixture.Route.Envelope.Identity.Project(requestedId).LookupKey;
        await fixture.CorruptPrimaryLookupAsync(lookupKey);

        var load = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.LoadAsync("configurationDocument", requestedId));
        var save = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.SaveAsync(Save(requestedId, "gadgets", 0)));
        var delete = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.DeleteAsync(new DeleteDocumentRequest("configurationDocument", requestedId)));

        AssertCollision(load, requestedId, lookupKey);
        AssertCollision(save, requestedId, lookupKey);
        AssertCollision(delete, requestedId, lookupKey);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task LinkedLookupCollisionRollsBackPrimaryUpdate(PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityAsync(form);
        const string requestedId = "Requested-Id";
        await fixture.Documents.SaveAsync(Save(requestedId, "tools", 0));
        await fixture.CorruptLinkedIdentityAsync("Collision-Retained", "different-comparison");

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => fixture.Documents.SaveAsync(Save(requestedId, "gadgets", 1)));

        AssertCollision(
            exception,
            requestedId,
            fixture.Route.LinkedRelationship!.Identity.Project(requestedId).LookupKey,
            "Collision-Retained");
        Assert.Contains(
            "\"category\":\"tools\"",
            (await fixture.Documents.LoadAsync("configurationDocument", requestedId))!.ContentJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupCollisionTerminatesUnitOfWorkAndRollsBackPriorWrite()
    {
        await using var fixture = await CreateIdentityAsync(PhysicalStorageForm.SharedDocuments);
        await fixture.Documents.SaveAsync(Save("Retained-Id", "tools", 0));
        const string requestedId = "Requested-Id";
        await fixture.CorruptPrimaryLookupAsync(
            fixture.Route.Envelope.Identity.Project(requestedId).LookupKey);
        await using var transaction = await fixture.Documents.BeginAsync(
            DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(
            Save("staged-before-collision", "tools", 0))).Status);

        await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(
            () => transaction.SaveAsync(Save(requestedId, "gadgets", 0)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Null(await fixture.Documents.LoadAsync("configurationDocument", "staged-before-collision"));
    }

    private static SaveDocumentRequest Save(string id, string category, long expectedVersion) =>
        new("configurationDocument", id, "1", $"{{\"category\":\"{category}\",\"priority\":1}}", expectedVersion);

    private static void AssertIdentitySchema(
        RelationalIdentitySchemaEvidence evidence,
        ExecutableDocumentIdentityRoute identity,
        Func<string, string> physicalKeyColumn)
    {
        Assert.Contains(identity.OriginalId.Identifier, evidence.Columns);
        Assert.Contains(identity.ComparisonKey.Identifier, evidence.Columns);
        Assert.Contains(identity.LookupKey.Identifier, evidence.Columns);
        Assert.Contains(physicalKeyColumn(identity.LookupKey.Identifier), evidence.PrimaryKeyColumns);
        Assert.DoesNotContain(physicalKeyColumn(identity.OriginalId.Identifier), evidence.PrimaryKeyColumns);
        Assert.DoesNotContain(physicalKeyColumn(identity.ComparisonKey.Identifier), evidence.PrimaryKeyColumns);
    }

    private static void AssertCollision(
        DocumentIdentityLookupCollisionException exception,
        string requestedId,
        string lookupKey,
        string retainedId = "Retained-Id")
    {
        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal(requestedId, exception.RequestedId);
        Assert.Equal(retainedId, exception.RetainedId);
        Assert.Equal(lookupKey, exception.LookupKey);
    }
}

public sealed class RelationalServerIdentityFixture(
    RelationalPhysicalDocumentStore store,
    IBoundedDocumentStore queries,
    ExecutableStorageRoute route,
    bool synchronizeAfterPrimaryLock,
    Func<string, Task> corruptPrimaryLookupAsync,
    Func<string, string, Task> corruptLinkedIdentityAsync,
    Func<bool, Task<RelationalIdentitySchemaEvidence>> readIdentitySchemaAsync,
    Func<bool, Task> dropComparisonEvidenceAsync,
    Func<Task> restartAsync,
    Func<string, string> physicalKeyColumn,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public RelationalPhysicalDocumentStore Store { get; } = store;
    public IDocumentStore Documents => Store;
    public IBoundedDocumentStore Queries { get; } = queries;
    public ExecutableStorageRoute Route { get; } = route;
    internal RelationalPhysicalWriteExecutionPoint RaceSynchronizationPoint { get; } = synchronizeAfterPrimaryLock
        ? RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock
        : RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock;
    public Func<string, Task> CorruptPrimaryLookupAsync { get; } = corruptPrimaryLookupAsync;
    public Func<string, string, Task> CorruptLinkedIdentityAsync { get; } = corruptLinkedIdentityAsync;
    public Func<bool, Task<RelationalIdentitySchemaEvidence>> ReadIdentitySchemaAsync { get; } = readIdentitySchemaAsync;
    public Func<bool, Task> DropComparisonEvidenceAsync { get; } = dropComparisonEvidenceAsync;
    public Func<Task> RestartAsync { get; } = restartAsync;
    public Func<string, string> PhysicalKeyColumn { get; } = physicalKeyColumn;
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed record RelationalIdentitySchemaEvidence(
    IReadOnlySet<string> Columns,
    IReadOnlyList<string> PrimaryKeyColumns);
