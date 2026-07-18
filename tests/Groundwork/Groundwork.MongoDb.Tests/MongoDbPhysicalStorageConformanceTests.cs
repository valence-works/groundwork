using System.Collections.Concurrent;
using System.Diagnostics;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbPhysicalStorageConformanceTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-rs")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Forms_route_crud_occ_query_count_page_order_and_canonical_json(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        await materializer.MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        var first = await store.SaveAsync(new SaveDocumentRequest("workItem", "1", "1", """{"status":"open","rank":2}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "2", "1", """{"status":"open","rank":1}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "3", "1", """{"status":"closed","rank":3}"""));
        var conflict = await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "1", "1", """{"status":"closed","rank":2}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, first.Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        Assert.Equal("""{"status":"open","rank":2}""", (await store.LoadAsync("workItem", "1"))!.ContentJson);

        var query = new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            order: [new DocumentQueryOrder("status")],
            skip: 1,
            take: 1);
        var page = await store.QueryAsync(query);
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Documents);
        Assert.Equal(2, await store.CountAsync(query.Select(BoundedQueryResultOperation.Count)));
        Assert.True(await store.AnyAsync(query.Select(BoundedQueryResultOperation.Any)));
        Assert.NotNull(await store.FirstOrDefaultAsync(query.Select(BoundedQueryResultOperation.First)));

        var route = Assert.Single(model.Routes);
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, "1"))
            .SingleAsync();
        Assert.Equal(
            """{"status":"open","rank":2}""",
            MongoDbCanonicalJson.Serialize(primary[route.Envelope.CanonicalJson.Identifier]));
        if (form == PhysicalStorageForm.PhysicalEntityTable)
            Assert.Equal("open", primary[route.ProjectedColumns.Single().Column.Identifier].AsString);
        else
            Assert.Equal("open", (await database.GetCollection<BsonDocument>(route.LinkedIndexStorage!.Name.Identifier)
                .Find(Builders<BsonDocument>.Filter.Empty).FirstAsync())[route.ProjectedColumns.Single().Column.Identifier].AsString);
    }

    [Fact]
    public async Task Unicode_equivalent_identity_loads_the_retained_original_spelling()
    {
        var (_, _, store) = await CreateIdentityStoreAsync(PhysicalStorageForm.PhysicalEntityTable);
        const string retainedId = "metric-\U00010428-\u00e9";

        var saved = await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            retainedId,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));
        var loaded = await store.LoadAsync("workItem", "METRIC-\U00010400-\u00c9");

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(retainedId, loaded!.Id);
    }

    [Fact]
    public async Task Equivalent_identity_save_returns_the_authoritative_original_without_overwriting()
    {
        var (_, _, store) = await CreateIdentityStoreAsync(PhysicalStorageForm.PhysicalEntityTable);
        const string retainedId = "metric-\U00010428-\u00e9";
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            retainedId,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));

        var conflict = await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            "METRIC-\U00010400-\u00c9",
            "1",
            """{"status":"closed"}"""));

        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal(retainedId, conflict.AuthoritativeId);
        var retained = await store.LoadAsync("workItem", retainedId);
        Assert.Equal(retainedId, retained!.Id);
        Assert.Equal("""{"status":"open"}""", retained.ContentJson);
        Assert.Equal(1, retained.Version);
    }

    [Fact]
    public async Task Equivalent_identity_delete_removes_primary_and_linked_records()
    {
        var (_, _, store) = await CreateIdentityStoreAsync(PhysicalStorageForm.SharedDocuments);
        const string retainedId = "metric-\U00010428-\u00e9";
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            retainedId,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));

        var deleted = await store.DeleteAsync(new DeleteDocumentRequest(
            "workItem",
            "METRIC-\U00010400-\u00c9",
            ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal(retainedId, deleted.AuthoritativeId);
        Assert.Null(await store.LoadAsync("workItem", retainedId));
        Assert.Equal(0, await store.CountAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            resultOperation: BoundedQueryResultOperation.Count)));
    }

    [Fact]
    public async Task Distinct_comparison_keys_with_one_lookup_key_raise_an_identity_collision()
    {
        var (database, model, store) = await CreateIdentityStoreAsync(
            PhysicalStorageForm.PhysicalEntityTable,
            StringIdentityCasePolicy.Ordinal);
        const string retainedId = "retained-id";
        const string requestedId = "requested-id";
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            retainedId,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));
        var route = Assert.Single(model.Routes);
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var retained = await collection.Find(Builders<BsonDocument>.Filter.Empty).SingleAsync();
        await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Id,
            retained[MongoDbPhysicalStorageFields.Id]));
        var requestedProjection = route.Envelope.Identity.Project(requestedId);
        retained[route.Envelope.Identity.LookupKey.Identifier] = requestedProjection.LookupKey;
        retained[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(
            route.PrimaryKey,
            retained);
        await collection.InsertOneAsync(retained);

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            store.SaveAsync(new SaveDocumentRequest(
                "workItem",
                requestedId,
                "1",
                """{"status":"closed"}""",
                ExpectedVersion: 0)));

        Assert.Equal("workItem", exception.DocumentKind);
        Assert.Equal(requestedId, exception.RequestedId);
        Assert.Equal(retainedId, exception.RetainedId);
        Assert.Equal(requestedProjection.LookupKey, exception.LookupKey);
        await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            store.LoadAsync("workItem", requestedId));
        await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            store.DeleteAsync(new DeleteDocumentRequest("workItem", requestedId)));
    }

    [Fact]
    public async Task Concurrent_equivalent_identity_creates_report_the_retained_original()
    {
        var (_, _, store) = await CreateIdentityStoreAsync(PhysicalStorageForm.PhysicalEntityTable);
        const string firstSpelling = "metric-\U00010428-\u00e9";
        const string secondSpelling = "METRIC-\U00010400-\u00c9";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = Enumerable.Range(0, 16).Select(async index =>
        {
            await start.Task;
            return await store.SaveAsync(new SaveDocumentRequest(
                "workItem",
                index % 2 == 0 ? firstSpelling : secondSpelling,
                "1",
                """{"status":"open"}""",
                ExpectedVersion: 0));
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(saves);

        var saved = Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.Saved);
        var authoritativeId = saved.Document!.Id;
        Assert.Equal(8, results.Count(result => result.Status == DocumentStoreWriteStatus.IdentityConflict));
        Assert.All(
            results.Where(result => result.Status == DocumentStoreWriteStatus.IdentityConflict),
            result => Assert.Equal(authoritativeId, result.AuthoritativeId));
        Assert.Equal(7, results.Count(result => result.Status == DocumentStoreWriteStatus.ConcurrencyConflict));
    }

    [Fact]
    public async Task Load_accepts_same_comparison_evidence_when_insert_commits_between_identity_reads()
    {
        var databaseName = $"groundwork_{Guid.NewGuid():N}";
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializationDatabase = new MongoClient(container.GetConnectionString()).GetDatabase(databaseName);
        await new MongoDbGroundworkMaterializer(materializationDatabase).MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        using var allowLookupFallback = new ManualResetEventSlim();
        var exactMissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        var observedExactMiss = 0;
        var loaderSettings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        loaderSettings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(@event =>
        {
            if (Volatile.Read(ref armed) == 0 ||
                !string.Equals(@event.CommandName, "find", StringComparison.Ordinal) ||
                !IsEmptyFirstBatch(@event.Reply) ||
                Interlocked.Exchange(ref observedExactMiss, 1) != 0)
            {
                return;
            }

            exactMissed.TrySetResult();
            allowLookupFallback.Wait(TimeSpan.FromSeconds(10));
        });
        var access = DocumentStoreAccess.Scoped(new("tenant-a"));
        var loader = new MongoDbPhysicalDocumentStore(
            new MongoClient(loaderSettings).GetDatabase(databaseName),
            model,
            access);
        var writer = new MongoDbPhysicalDocumentStore(
            new MongoClient(container.GetConnectionString()).GetDatabase(databaseName),
            model,
            access);

        Volatile.Write(ref armed, 1);
        var load = loader.LoadAsync("workItem", "contended");
        try
        {
            await exactMissed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await writer.SaveAsync(new SaveDocumentRequest(
                "workItem",
                "contended",
                "1",
                """{"status":"open"}""",
                ExpectedVersion: 0))).Status);
        }
        finally
        {
            allowLookupFallback.Set();
        }

        var loaded = await load;
        Assert.NotNull(loaded);
        Assert.Equal("contended", loaded.Id);
        Assert.Equal(route.StorageUnit.Value, loaded.DocumentKind);
    }

    [Fact]
    public async Task Unit_of_work_identity_conflict_is_terminal_and_rolls_back_prior_writes()
    {
        var (_, _, store) = await CreateIdentityStoreAsync(PhysicalStorageForm.PhysicalEntityTable);
        const string retainedId = "metric-\U00010428-\u00e9";
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            retainedId,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));
        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem",
            "staged",
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0))).Status);

        var conflict = await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem",
            "METRIC-\U00010400-\u00c9",
            "1",
            """{"status":"closed"}"""));

        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal(retainedId, conflict.AuthoritativeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Null(await store.LoadAsync("workItem", "staged"));
        Assert.Equal("""{"status":"open"}""", (await store.LoadAsync("workItem", retainedId))!.ContentJson);
    }

    [Fact]
    public async Task Public_factory_rejects_identity_policy_drift_in_applied_database_state()
    {
        var databaseName = $"groundwork_{Guid.NewGuid():N}";
        var database = new MongoClient(container.GetConnectionString()).GetDatabase(databaseName);
        var ordinal = Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(ordinal);
        var unicode = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            identityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.CreatePhysicalAsync(
                container.GetConnectionString(),
                databaseName,
                unicode.Manifest,
                unicode.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("Rejected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GW-SCHEMA-006", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_factory_accepts_an_existing_database_through_the_admitted_seam()
    {
        var database = Database();
        var model = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            identityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        await using var handle = await MongoDbDocumentStoreFactory.CreatePhysicalAsync(
            database,
            model.Manifest,
            model.Provider,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var saved = await handle.Store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            "metric-\u00e9",
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal("metric-\u00e9", (await handle.Store.LoadAsync("workItem", "METRIC-\u00c9"))!.Id);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Physical_forms_persist_original_comparison_and_lookup_identity_evidence(
        PhysicalStorageForm form)
    {
        var (database, model, store) = await CreateIdentityStoreAsync(form);
        const string id = "metric-\U00010428-\u00e9";
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem",
            id,
            "1",
            """{"status":"open"}""",
            ExpectedVersion: 0));

        var route = Assert.Single(model.Routes);
        var projection = route.Envelope.Identity.Project(id);
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        AssertIdentity(primary, route.Envelope.Identity, projection);
        Assert.Equal(
            projection.LookupKey,
            primary[MongoDbPhysicalStorageFields.Id]
                .AsBsonDocument[route.Envelope.Identity.LookupKey.Identifier]
                .AsString);

        if (route.LinkedIndexStorage is null)
            return;
        var linked = await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        AssertIdentity(linked, route.LinkedRelationship!.Identity, projection);
        Assert.Equal(
            projection.LookupKey,
            linked[MongoDbPhysicalStorageFields.Id]
                .AsBsonDocument[route.LinkedRelationship.Identity.LookupKey.Identifier]
                .AsString);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task Linked_hydration_uses_structural_scope_and_id_keys(PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var first = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("a")));
        var second = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("a\u001fb")));
        var privileged = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("structural hydration conformance")));
        await first.SaveAsync(new SaveDocumentRequest(
            "workItem", "b\u001fc", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));
        await second.SaveAsync(new SaveDocumentRequest(
            "workItem", "c", "1", """{"status":"open","rank":2}""", ExpectedVersion: 0));

        var result = await privileged.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]));

        Assert.Equal(["b\u001fc", "c"], result.Documents.Select(document => document.Id).Order());
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Privileged_paging_uses_scope_then_id_as_a_total_order(PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var tenantA = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        var tenantB = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-b")));
        var privileged = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.PrivilegedAcrossScopes(new PrivilegedStorageAccess("paging conformance")));
        await tenantA.SaveAsync(new SaveDocumentRequest(
            "workItem", "same", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));
        await tenantB.SaveAsync(new SaveDocumentRequest(
            "workItem", "same", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));

        async Task<DocumentEnvelope> PageAsync(int skip) => Assert.Single((await privileged.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            order: [new DocumentQueryOrder("status")],
            skip: skip,
            take: 1))).Documents);

        Assert.Equal("tenant-a", (await PageAsync(0)).Scope!.Value);
        Assert.Equal("tenant-b", (await PageAsync(1)).Scope!.Value);
    }

    [Fact]
    public async Task Linked_query_count_page_and_primary_hydration_share_one_snapshot_during_update()
    {
        var pageRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueHydration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Database();
        var model = Model(PhysicalStorageForm.SharedDocuments);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var writer = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        var hooks = MongoDbPhysicalDocumentStoreExecutionHooks.None with
        {
            QueryPageRead = async (_, _, _) =>
            {
                pageRead.TrySetResult();
                await continueHydration.Task;
            }
        };
        var reader = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options: null,
            TimeProvider.System,
            hooks);
        await writer.SaveAsync(new SaveDocumentRequest(
            "workItem", "snapshot", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));

        var query = reader.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            take: 1));
        await pageRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await writer.SaveAsync(new SaveDocumentRequest(
            "workItem", "snapshot", "1", """{"status":"closed","rank":2}""", ExpectedVersion: 1))).Status);
        continueHydration.TrySetResult();

        var result = await query;
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("""{"status":"open","rank":1}""", Assert.Single(result.Documents).ContentJson);
    }

    [Fact]
    public async Task Linked_query_primary_hydration_does_not_lose_a_document_deleted_after_page_read()
    {
        var pageRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueHydration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Database();
        var model = Model(PhysicalStorageForm.SharedDocuments);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var writer = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        var reader = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options: null,
            TimeProvider.System,
            MongoDbPhysicalDocumentStoreExecutionHooks.None with
            {
                QueryPageRead = async (_, _, _) =>
                {
                    pageRead.TrySetResult();
                    await continueHydration.Task;
                }
            });
        await writer.SaveAsync(new SaveDocumentRequest(
            "workItem", "snapshot-delete", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));

        var query = reader.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]));
        await pageRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var deleted = await writer.DeleteAsync(new DeleteDocumentRequest(
            "workItem", "snapshot-delete", ExpectedVersion: 1));
        Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
        Assert.Equal("snapshot-delete", deleted.AuthoritativeId);
        continueHydration.TrySetResult();

        var result = await query;
        Assert.Equal("snapshot-delete", Assert.Single(result.Documents).Id);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Concurrent_same_identity_creates_converge_to_one_saved_result_without_provider_errors(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = Enumerable.Range(0, 16).Select(async index =>
        {
            await start.Task;
            return await store.SaveAsync(new SaveDocumentRequest(
                "workItem",
                "same-id",
                "1",
                $$"""{"status":"open","rank":{{index}}}""",
                ExpectedVersion: 0));
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(saves);

        Assert.Single(results, result => result.Status == DocumentStoreWriteStatus.Saved);
        Assert.Equal(15, results.Count(result => result.Status == DocumentStoreWriteStatus.ConcurrencyConflict));
        Assert.NotNull(await store.LoadAsync("workItem", "same-id"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Unit_of_work_conflict_is_terminal_and_rolls_back_prior_writes(PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "one", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "one", "1", """{"status":"closed","rank":2}""", ExpectedVersion: 0))).Status);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Contains("completed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.LoadAsync("workItem", "one"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Unit_of_work_rejects_kinds_outside_its_commit_scope_without_becoming_terminal(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));

        await Assert.ThrowsAsync<ArgumentException>(() => transaction.SaveAsync(new SaveDocumentRequest(
            "otherItem", "outside-save", "1", "{}", ExpectedVersion: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.DeleteAsync(new DeleteDocumentRequest(
            "otherItem", "outside-delete")));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.LoadAsync("otherItem", "outside-load"));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "inside", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0))).Status);
        await transaction.CommitAsync();
        Assert.NotNull(await store.LoadAsync("workItem", "inside"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Unit_of_work_non_success_rolls_back_and_makes_the_transaction_terminal(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "staged-before-non-success", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0))).Status);

        Assert.Equal(DocumentStoreWriteStatus.NotFound, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "missing", "1", "{}", ExpectedVersion: 1))).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.LoadAsync(
            "workItem", "staged-before-non-success"));
        Assert.Null(await store.LoadAsync("workItem", "staged-before-non-success"));
    }

    [Fact]
    public async Task Unit_of_work_unique_write_error_returns_a_structured_conflict_and_is_terminal()
    {
        var database = Database();
        var model = EntityEvolutionModel(includeStatus: true, uniqueStatus: true);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":1,"status":"alpha"}""", ExpectedVersion: 0))).Status);

        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "staged-before-conflict", "1", """{"rank":3,"status":"beta"}""", ExpectedVersion: 0))).Status);
        var conflict = await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "conflicting", "1", """{"rank":2,"status":"alpha"}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.LoadAsync("workItem", "existing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "after-terminal", "1", """{"rank":4,"status":"gamma"}""")));
        Assert.Null(await store.LoadAsync("workItem", "conflicting"));
        Assert.Null(await store.LoadAsync("workItem", "staged-before-conflict"));
    }

    [Fact]
    public async Task Applied_state_is_restart_safe_and_rejects_an_out_of_band_index_conflict()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, (await materializer.MaterializeAsync(model)).Outcome);

        var route = Assert.Single(model.Routes);
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var index = Assert.Single(route.Indexes);
        await collection.Indexes.DropOneAsync(index.Name.Identifier);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending(route.Envelope.Id.Identifier),
            new CreateIndexOptions { Name = index.Name.Identifier }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));
        Assert.Contains("conflicts with durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applied_state_rejects_a_missing_primary_collection_even_when_indexes_are_linked()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.SharedDocuments);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);

        var route = Assert.Single(model.Routes);
        await database.DropCollectionAsync(route.PrimaryStorage.Name.Identifier);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));
        Assert.Contains(route.PrimaryStorage.Name.Identifier, exception.Message, StringComparison.Ordinal);
        Assert.Contains("durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Physical_schema_rejects_a_view_at_a_resolved_collection_name_without_publishing_state()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.DedicatedDocumentTable);
        var route = Assert.Single(model.Routes);
        await database.CreateCollectionAsync("view_source");
        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["create"] = route.PrimaryStorage.Name.Identifier,
            ["viewOn"] = "view_source",
            ["pipeline"] = new BsonArray()
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model));

        Assert.Contains("writable native collection", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Physical_schema_rejects_a_capped_resolved_collection_without_publishing_state()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var route = Assert.Single(model.Routes);
        await database.CreateCollectionAsync(
            route.PrimaryStorage.Name.Identifier,
            new CreateCollectionOptions
            {
                Capped = true,
                MaxSize = 1024 * 1024
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model));

        Assert.Contains("capped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Durable_restart_rejects_a_primary_collection_replaced_by_a_view()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.DedicatedDocumentTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        await database.DropCollectionAsync(route.PrimaryStorage.Name.Identifier);
        await database.CreateCollectionAsync("replacement_source");
        await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["create"] = route.PrimaryStorage.Name.Identifier,
            ["viewOn"] = "replacement_source",
            ["pipeline"] = new BsonArray()
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));

        Assert.Contains("writable native collection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durable_restart_rejects_a_primary_collection_replaced_by_a_capped_collection()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.DedicatedDocumentTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        await database.DropCollectionAsync(route.PrimaryStorage.Name.Identifier);
        await database.CreateCollectionAsync(
            route.PrimaryStorage.Name.Identifier,
            new CreateCollectionOptions
            {
                Capped = true,
                MaxSize = 1024 * 1024
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));

        Assert.Contains("capped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applied_state_rejects_missing_backfill_completion_evidence()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);

        var ledger = database.GetCollection<BsonDocument>("groundwork_physical_schema_operations");
        var removed = await ledger.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq(
            "kind",
            PhysicalSchemaOperationKind.BackfillCanonicalJson.ToString()));
        Assert.Equal(1, removed.DeletedCount);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));
        Assert.Contains("backfill-canonical-json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("durable applied route state evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applied_state_does_not_trust_index_evidence_redirected_to_a_decoy_collection()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var keys = new BsonDocument();
        foreach (var column in index.Columns.OrderBy(column => column.Order))
            keys[column.Column.Identifier] = column.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        const string decoy = "decoy_indexes";
        await database.CreateCollectionAsync(decoy, new CreateCollectionOptions { Collation = new Collation("simple") });
        await database.GetCollection<BsonDocument>(decoy).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
            new CreateIndexOptions { Name = index.Name.Identifier, Unique = index.IsUnique }));
        await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier).Indexes.DropOneAsync(index.Name.Identifier);
        await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("kind", PhysicalSchemaOperationKind.CreatePhysicalIndex.ToString()),
            Builders<BsonDocument>.Update.Set("collection", decoy));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));
        Assert.Contains("evidence conflicts with durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_materializers_converge_and_units_of_work_commit_or_roll_back_atomically()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.DedicatedDocumentTable);
        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => new MongoDbGroundworkMaterializer(database).MaterializeAsync(model)));
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        await using (var rollback = await store.BeginAsync(DocumentCommitScope.Of("workItem")))
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await rollback.SaveAsync(
                new SaveDocumentRequest("workItem", "rolled-back", "1", """{"status":"open"}"""))).Status);
            await rollback.RollbackAsync();
        }
        Assert.Null(await store.LoadAsync("workItem", "rolled-back"));

        await using (var commit = await store.BeginAsync(DocumentCommitScope.Of("workItem")))
        {
            await commit.SaveAsync(new SaveDocumentRequest("workItem", "committed-a", "1", """{"status":"open"}"""));
            await commit.SaveAsync(new SaveDocumentRequest("workItem", "committed-b", "1", """{"status":"closed"}"""));
            await commit.CommitAsync();
        }
        Assert.NotNull(await store.LoadAsync("workItem", "committed-a"));
        Assert.NotNull(await store.LoadAsync("workItem", "committed-b"));
    }

    [Fact]
    public async Task Operation_evidence_is_isolated_by_manifest_provider_target()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var otherTarget = new PhysicalSchemaTarget(
            new StorageManifestIdentity("mongo.other-manifest"),
            model.Target.ManifestVersion,
            model.Provider,
            model.Routes);
        var executor = new MongoDbPhysicalSchemaExecutor(database);

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(model.Target, executor)).Outcome);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(otherTarget, executor)).Outcome);

        var evidence = await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations")
            .Find(Builders<BsonDocument>.Filter.Empty)
            .ToListAsync();
        var byTarget = evidence.GroupBy(document => document["target_id"]).ToArray();
        Assert.Equal(2, byTarget.Length);
        Assert.Equal(byTarget[0].Count(), byTarget[1].Count());
        Assert.NotEmpty(evidence
            .GroupBy(document => document["operation_id"].AsString)
            .Where(operation => operation.Count() == 2));
        Assert.Equal(evidence.Count, evidence.Select(document => document["_id"]).Distinct().Count());
    }

    [Fact]
    public async Task Delimiter_bearing_manifest_and_provider_identities_keep_lease_ledger_and_state_isolated()
    {
        var database = Database();
        var template = Model(PhysicalStorageForm.PhysicalEntityTable);
        var first = MongoDbPhysicalStorageModel.Compile(
            template.Manifest with { Identity = new StorageManifestIdentity("c") },
            new ProviderIdentity("a:b", "1"));
        var second = MongoDbPhysicalStorageModel.Compile(
            template.Manifest with { Identity = new StorageManifestIdentity("b:c") },
            new ProviderIdentity("a", "1"));
        var materializer = new MongoDbGroundworkMaterializer(database);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, (await materializer.MaterializeAsync(first)).Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, (await materializer.MaterializeAsync(second)).Outcome);

        Assert.Equal(2, await database.GetCollection<BsonDocument>("groundwork_physical_schema_locks")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
        Assert.Equal(2, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
        var evidence = await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations")
            .Find(Builders<BsonDocument>.Filter.Empty)
            .ToListAsync();
        Assert.Equal(2, evidence.Select(operation => operation["target_id"]).Distinct().Count());
    }

    [Fact]
    public async Task Lost_materialization_lease_cancels_in_flight_work_and_never_records_target_state()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var executor = new LeaseLossBlockingExecutor(
            new MongoDbPhysicalSchemaExecutor(
                database,
                leaseDuration: MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration));

        var application = PhysicalSchemaApplication.ApplyAsync(model.Target, executor);
        await executor.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await database.GetCollection<BsonDocument>("groundwork_physical_schema_locks").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity)),
            Builders<BsonDocument>.Update.Set("owner", "stolen-owner"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application);
        Assert.True(executor.OperationObservedLeaseLoss);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Minimum_and_default_schema_leases_renew_and_preserve_monotonic_fencing()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var renewedExpiryObserved = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var minimumExecutor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration,
            beforeBackfillWrite: null,
            afterLeaseRenewal: expiry => renewedExpiryObserved.TrySetResult(expiry));
        var locks = database.GetCollection<BsonDocument>("groundwork_physical_schema_locks");

        long firstFence;
        var minimumAcquisitionStarted = DateTime.UtcNow;
        await using (var lease = await minimumExecutor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
            var minimumAcquisitionCompleted = DateTime.UtcNow;
            var initial = await locks.Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity))).SingleAsync();
            firstFence = initial["fence"].ToInt64();
            var initialExpiry = initial["expires_at"].ToUniversalTime();
            Assert.InRange(
                initialExpiry,
                minimumAcquisitionStarted + MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration - TimeSpan.FromMilliseconds(1),
                minimumAcquisitionCompleted + MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration);
            var observedRenewedExpiry = await renewedExpiryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var renewed = await locks.Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity))).SingleAsync();
            var storedRenewedExpiry = renewed["expires_at"].ToUniversalTime();
            Assert.True(storedRenewedExpiry > initialExpiry);
            Assert.InRange(
                storedRenewedExpiry,
                observedRenewedExpiry.UtcDateTime - TimeSpan.FromMilliseconds(1),
                observedRenewedExpiry.UtcDateTime);
        }

        var defaultExecutor = new MongoDbPhysicalSchemaExecutor(database);
        var defaultAcquisitionStarted = DateTime.UtcNow;
        await using var defaultLease = await defaultExecutor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        var defaultAcquisitionCompleted = DateTime.UtcNow;
        var reacquired = await locks.Find(Builders<BsonDocument>.Filter.Eq(
            "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(model.Target.Identity))).SingleAsync();
        Assert.True(reacquired["fence"].ToInt64() > firstFence);
        Assert.InRange(
            reacquired["expires_at"].ToUniversalTime(),
            defaultAcquisitionStarted + MongoDbPhysicalSchemaExecutor.DefaultLeaseDuration - TimeSpan.FromMilliseconds(1),
            defaultAcquisitionCompleted + MongoDbPhysicalSchemaExecutor.DefaultLeaseDuration);
    }

    [Fact]
    public async Task Lease_stolen_after_operation_execution_cannot_record_operation_evidence()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var reachedEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: TimeSpan.FromMinutes(1),
            beforeBackfillWrite: null,
            beforeOperationEvidenceWrite: async cancellationToken =>
            {
                reachedEvidence.TrySetResult();
                await continueEvidence.Task.WaitAsync(cancellationToken);
            });

        var application = PhysicalSchemaApplication.ApplyAsync(model.Target, executor);
        await reachedEvidence.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await StealLeaseAsync(database, model.Target.Identity);
        continueEvidence.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Lease_stolen_before_applied_state_compare_and_swap_cannot_publish_target_state()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var reachedState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: TimeSpan.FromMinutes(1),
            beforeBackfillWrite: null,
            beforeOperationEvidenceWrite: null,
            beforeAppliedStateWrite: async cancellationToken =>
            {
                reachedState.TrySetResult();
                await continueState.Task.WaitAsync(cancellationToken);
            });

        var application = PhysicalSchemaApplication.ApplyAsync(model.Target, executor);
        await reachedState.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await StealLeaseAsync(database, model.Target.Identity);
        continueState.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application);
        Assert.True(await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty) > 0);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Schema_evidence_and_document_transactions_pin_majority_durability_and_snapshot_reads()
    {
        var commands = new ConcurrentQueue<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started =>
            commands.Enqueue(started.Command.DeepClone().AsBsonDocument));
        using var client = new MongoClient(settings);
        var database = client.GetDatabase($"groundwork_durability_{Guid.NewGuid():N}");
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        await store.SaveAsync(new SaveDocumentRequest("workItem", "1", "1", """{"status":"open"}"""));

        Assert.Contains(commands, command => command.ElementCount > 0 && command.GetElement(0).Name == "findAndModify" &&
            command.TryGetValue("writeConcern", out var concern) && concern["w"] == "majority");
        Assert.Contains(commands, command => command.ElementCount > 0 && command.GetElement(0).Name == "commitTransaction" &&
            command.TryGetValue("writeConcern", out var concern) && concern["w"] == "majority");
        Assert.Contains(commands, command => command.GetValue("startTransaction", false).ToBoolean() &&
            command.TryGetValue("readConcern", out var concern) && concern["level"] == "snapshot");
    }

    [Fact]
    public async Task Same_version_addition_backfills_existing_documents_and_restart_is_a_no_op()
    {
        var database = Database();
        var initial = EntityEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":7,"status":"open"}"""));

        var changed = EntityEvolutionModel(includeStatus: true);
        var materializer = new MongoDbGroundworkMaterializer(database);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, (await materializer.MaterializeAsync(changed)).Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, (await materializer.MaterializeAsync(changed)).Outcome);

        var route = Assert.Single(changed.Routes);
        var status = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        var persisted = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, "existing"))
            .SingleAsync();
        Assert.Equal("open", persisted[status.Column.Identifier].AsString);
        var store = new MongoDbPhysicalDocumentStore(database, changed, DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Single((await store.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]))).Documents);
    }

    [Fact]
    public async Task Unique_index_creation_checks_fully_backfilled_existing_documents_before_state_is_published()
    {
        var database = Database();
        var initial = EntityEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var store = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "one", "1", """{"rank":1,"status":"same"}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "two", "1", """{"rank":2,"status":"same"}"""));

        var changed = EntityEvolutionModel(includeStatus: true, uniqueStatus: true);
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(changed));

        var route = Assert.Single(changed.Routes);
        var status = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        var persisted = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, document => Assert.Equal("same", document[status.Column.Identifier].AsString));
        var appliedState = await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(initial.Target.Fingerprint, appliedState["target_fingerprint"].AsString);
        Assert.NotEqual(changed.Target.Fingerprint, appliedState["target_fingerprint"].AsString);
    }

    [Fact]
    public async Task Failed_unique_index_attempt_revalidates_backfill_before_a_fresh_client_publishes_target_state()
    {
        var database = Database();
        var initial = EntityEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "one", "1", """{"rank":1,"status":"same"}""", ExpectedVersion: 0));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "two", "1", """{"rank":2,"status":"same"}""", ExpectedVersion: 0));

        var changed = EntityEvolutionModel(includeStatus: true, uniqueStatus: true);
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(changed));
        var stateCollection = database.GetCollection<BsonDocument>("groundwork_physical_schema_state");
        var stateAfterFailure = await stateCollection.Find(
                Builders<BsonDocument>.Filter.Eq(
                    "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(initial.Target.Fingerprint, stateAfterFailure["target_fingerprint"].AsString);
        var historyAfterFailure = PhysicalSchemaAppliedStateSerializer.Deserialize(stateAfterFailure["state"].AsString);
        var retryPlan = PhysicalSchemaDiffPlanner.Plan(
            changed.Target,
            PhysicalSchemaHistoryState.FromApplied(historyAfterFailure),
            DateTimeOffset.UtcNow);
        var unpublishedBackfill = Assert.Single(retryPlan.Operations.OfType<BackfillCanonicalJsonOperation>(), operation =>
            operation.SourcePaths.Contains("status", StringComparer.Ordinal));
        var retainedEvidence = await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations")
            .Find(
                Builders<BsonDocument>.Filter.Eq(
                    "target_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)) &
                Builders<BsonDocument>.Filter.Eq("operation_id", unpublishedBackfill.Identity))
            .SingleAsync();
        Assert.Equal(unpublishedBackfill.Fingerprint, retainedEvidence["fingerprint"].AsString);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "one", "1", """{"rank":3,"status":"alpha"}""", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await initialStore.DeleteAsync(new DeleteDocumentRequest(
            "workItem", "two", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "two", "1", """{"rank":4,"status":"beta"}""", ExpectedVersion: 0))).Status);
        var stateBeforeRetry = await stateCollection.Find(
                Builders<BsonDocument>.Filter.Eq(
                    "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(initial.Target.Fingerprint, stateBeforeRetry["target_fingerprint"].AsString);

        using var restartedClient = new MongoClient(container.GetConnectionString());
        var restartedDatabase = restartedClient.GetDatabase(database.DatabaseNamespace.DatabaseName);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await new MongoDbGroundworkMaterializer(restartedDatabase).MaterializeAsync(changed)).Outcome);

        var stateAfterSuccess = await restartedDatabase.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(changed.Target.Fingerprint, stateAfterSuccess["target_fingerprint"].AsString);
        var changedStore = new MongoDbPhysicalDocumentStore(
            restartedDatabase,
            changed,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal("one", Assert.Single((await changedStore.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "alpha"))]))).Documents).Id);
        Assert.Equal("two", Assert.Single((await changedStore.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "beta"))]))).Documents).Id);

        var route = Assert.Single(changed.Routes);
        var expectedIndex = Assert.Single(route.Indexes);
        var actualIndex = (await (await restartedDatabase
                .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
                .Indexes.ListAsync())
            .ToListAsync()).Single(index => index["name"].AsString == expectedIndex.Name.Identifier);
        var expectedKeys = new BsonDocument();
        foreach (var column in expectedIndex.Columns.OrderBy(column => column.Order))
            expectedKeys[column.Column.Identifier] = column.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        Assert.Equal(expectedKeys, actualIndex["key"].AsBsonDocument);
        Assert.True(actualIndex["unique"].ToBoolean());
    }

    [Fact]
    public async Task Primary_backfill_cannot_overwrite_a_deleted_and_recreated_document_incarnation()
    {
        var database = Database();
        var initial = EntityEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":7,"status":"open"}"""));

        var changed = EntityEvolutionModel(includeStatus: true);
        var backfillRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBackfill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            async cancellationToken =>
            {
                backfillRead.TrySetResult();
                await continueBackfill.Task.WaitAsync(cancellationToken);
            });
        var application = PhysicalSchemaApplication.ApplyAsync(changed.Target, executor);
        await backfillRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var changedStore = new MongoDbPhysicalDocumentStore(database, changed, DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await changedStore.DeleteAsync(
            new DeleteDocumentRequest("workItem", "existing", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await changedStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":8,"status":"closed"}"""))).Status);
        continueBackfill.TrySetResult();

        await application;

        var route = Assert.Single(changed.Routes);
        var status = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        var persisted = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, "existing"))
            .SingleAsync();
        Assert.Equal(1, persisted[route.Envelope.Version.Identifier].ToInt64());
        Assert.Equal("closed", persisted[status.Column.Identifier].AsString);
    }

    [Fact]
    public async Task Linked_backfill_cannot_overwrite_a_newer_concurrent_store_projection()
    {
        var database = Database();
        var initial = SharedProjectionEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":7,"status":"open"}"""));

        var changed = SharedProjectionEvolutionModel(includeStatus: true);
        var backfillRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBackfill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            async cancellationToken =>
            {
                backfillRead.TrySetResult();
                await continueBackfill.Task.WaitAsync(cancellationToken);
            });
        var application = PhysicalSchemaApplication.ApplyAsync(changed.Target, executor);
        await backfillRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var changedStore = new MongoDbPhysicalDocumentStore(database, changed, DocumentStoreAccess.Scoped(new("tenant-a")));
        var saved = await changedStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":7,"status":"closed"}""", ExpectedVersion: 1));
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        continueBackfill.TrySetResult();

        await application;

        var route = Assert.Single(changed.Routes);
        var linked = await database.GetCollection<BsonDocument>(route.LinkedIndexStorage!.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        var status = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        Assert.Equal("closed", linked[status.Column.Identifier].AsString);
        Assert.Equal(2, linked[MongoDbPhysicalStorageFields.LinkedPrimaryVersion].ToInt64());
    }

    [Fact]
    public async Task Linked_backfill_cannot_resurrect_a_deleted_document_or_overwrite_its_recreated_incarnation()
    {
        var database = Database();
        var initial = SharedProjectionEvolutionModel(includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":7,"status":"open"}"""));

        var changed = SharedProjectionEvolutionModel(includeStatus: true);
        var backfillRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBackfill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            async cancellationToken =>
            {
                backfillRead.TrySetResult();
                await continueBackfill.Task.WaitAsync(cancellationToken);
            });
        var application = PhysicalSchemaApplication.ApplyAsync(changed.Target, executor);
        await backfillRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var changedStore = new MongoDbPhysicalDocumentStore(database, changed, DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await changedStore.DeleteAsync(
            new DeleteDocumentRequest("workItem", "existing", ExpectedVersion: 1))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await changedStore.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"rank":8,"status":"closed"}"""))).Status);
        continueBackfill.TrySetResult();

        await application;

        var route = Assert.Single(changed.Routes);
        var linked = await database.GetCollection<BsonDocument>(route.LinkedIndexStorage!.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, "existing"))
            .SingleAsync();
        var status = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        Assert.Equal("closed", linked[status.Column.Identifier].AsString);
        Assert.Equal(primary[MongoDbPhysicalStorageFields.Incarnation], linked[MongoDbPhysicalStorageFields.Incarnation]);
    }

    [Fact]
    public async Task Linked_backfill_surfaces_true_unique_collision_and_retains_old_applied_state()
    {
        var database = Database();
        var initial = SharedUniqueBackfillModel(includeCategory: false);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(initial);
        var store = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "existing", "1", """{"status":"alpha","category":"one"}"""));
        var route = Assert.Single(initial.Routes);
        var primaryCollection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var collision = (await primaryCollection.Find(Builders<BsonDocument>.Filter.Eq(
                route.Envelope.Id.Identifier,
                "existing")).SingleAsync()).DeepClone().AsBsonDocument;
        MongoDbPhysicalDocumentIdentity.WritePrimary(collision, route, "collision");
        collision[MongoDbPhysicalStorageFields.Incarnation] = Guid.NewGuid().ToString("N");
        collision[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(route.PrimaryKey, collision);
        await primaryCollection.InsertOneAsync(collision);
        var changed = SharedUniqueBackfillModel(includeCategory: true);

        await Assert.ThrowsAnyAsync<MongoException>(() => materializer.MaterializeAsync(changed));

        var state = await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(initial.Target.Fingerprint, state["target_fingerprint"].AsString);
    }

    [Theory]
    [InlineData("collation")]
    [InlineData("partial-omitted")]
    [InlineData("partial-scope")]
    [InlineData("sparse")]
    [InlineData("hidden")]
    public async Task Applied_state_rejects_provider_significant_index_option_conflicts(string option)
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        await collection.Indexes.DropOneAsync(index.Name.Identifier);
        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = index.Name.Identifier,
            Unique = index.IsUnique,
            Collation = option == "collation" ? new Collation("en") : Collation.Simple,
            PartialFilterExpression = option switch
            {
                "partial-omitted" or "sparse" => null,
                "partial-scope" => Builders<BsonDocument>.Filter.Exists(index.Columns[0].Column.Identifier) &
                                   Builders<BsonDocument>.Filter.Exists(index.Columns.Last().Column.Identifier),
                _ => Builders<BsonDocument>.Filter.Exists(index.Columns.Last().Column.Identifier)
            },
            Sparse = option == "sparse",
            Hidden = option == "hidden"
        };
        var keys = new BsonDocument();
        foreach (var column in index.Columns.OrderBy(column => column.Order))
            keys[column.Column.Identifier] = column.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
            options));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));
        Assert.Contains("conflicts with durable applied route state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applied_state_rejects_tampered_partial_filter_evidence()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(model);
        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        await database.GetCollection<BsonDocument>("groundwork_physical_schema_operations").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("kind", PhysicalSchemaOperationKind.CreatePhysicalIndex.ToString()),
            Builders<BsonDocument>.Update.Set(
                "partial_filter_expression",
                new BsonDocument
                {
                    [index.Columns[0].Column.Identifier] = new BsonDocument("$exists", true),
                    [index.Columns[^1].Column.Identifier] = new BsonDocument("$exists", true)
                }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => materializer.MaterializeAsync(model));

        Assert.Contains("evidence conflicts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_rejects_the_same_invalid_runtime_shape_as_normal_query_execution()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        var invalid = new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("status", "open"))]);

        var normal = await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(invalid));
        var explain = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExplainAsync(invalid));

        Assert.Equal(normal.Message, explain.Message);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Cursor_pages_resume_by_identity_across_a_reopened_store(PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form, pagingSupport: QueryPagingSupport.Cursor);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        foreach (var id in new[] { "c", "a", "b", "d", "e" })
            await store.SaveAsync(new SaveDocumentRequest("workItem", id, "1", """{"status":"open"}"""));
        var predicate = DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"));
        var query = new DocumentQuery(
            "workItem",
            "list-by-status",
            [predicate],
            [new DocumentQueryOrder("status")],
            take: 1);

        var first = await store.QueryAsync(query);
        var reopened = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var middleQuery = new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 2,
            continuation: first.NextContinuation);
        var explanation = await reopened.ExplainAsync(middleQuery);
        var middle = await reopened.QueryAsync(middleQuery);
        var final = await reopened.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 10,
            continuation: middle.NextContinuation));
        var route = model.Routes.Single();
        var expected = new[] { "a", "b", "c", "d", "e" }
            .OrderBy(id => route.Envelope.Identity.Project(id).LookupKey, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected[0], Assert.Single(first.Documents).Id);
        Assert.NotNull(first.NextContinuation);
        Assert.Equal(expected[1..3], middle.Documents.Select(document => document.Id));
        Assert.NotNull(middle.NextContinuation);
        Assert.Equal(expected[3..], final.Documents.Select(document => document.Id));
        Assert.Null(final.NextContinuation);
        Assert.Equal(5, final.TotalCount);
        var pagePlan = explanation.Commands.Single(command =>
            command.Identity == PhysicalDocumentQueryCommandIdentities.Page);
        Assert.Contains(route.Indexes.Single().Name.Identifier, pagePlan.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLSCAN", pagePlan.NativePlan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stage\" : \"SORT\"", pagePlan.NativePlan, StringComparison.Ordinal);
        Assert.Contains("\"stage\" : \"LIMIT\"", pagePlan.NativePlan, StringComparison.Ordinal);
        Assert.Equal(3, pagePlan.ProviderAppliedMaximumRows);
        Assert.Equal(
            explanation.Plan.Order.Select(order => order.Field.Identifier),
            pagePlan.ProviderAppliedOrder.Select(order => order.FieldIdentifier));
        Assert.True(pagePlan.ProviderAppliedOrder[^1].IsIdentityTieBreak);
        if (explanation.Plan.RequiresPrimaryLookup)
        {
            Assert.Equal(
                2,
                explanation.Commands.Single(command =>
                    command.Kind == PhysicalDocumentQueryCommandKind.PrimaryHydration).ProviderAppliedMaximumRows);
        }
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Latest_per_key_filters_before_grouping_and_pages_deterministic_representatives(
        PhysicalStorageForm form)
    {
        var aggregateCommands = new ConcurrentQueue<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.CommandName == "aggregate")
                aggregateCommands.Enqueue(started.Command.DeepClone().AsBsonDocument);
        });
        using var client = new MongoClient(settings);
        var database = client.GetDatabase($"groundwork_latest_{Guid.NewGuid():N}");
        var model = LatestPerKeyModel(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "alpha-hidden", "1", """{"category":"alpha","priority":0,"visible":false}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "alpha-low", "1", """{"category":"alpha","priority":1,"visible":true}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "alpha-high", "1", """{"category":"alpha","priority":3,"visible":true}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "beta-tie-b", "1", """{"category":"beta","priority":2,"visible":true}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "beta-tie-a", "1", """{"category":"beta","priority":2,"visible":true}"""));
        await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "gamma-hidden", "1", """{"category":"gamma","priority":1,"visible":false}"""));
        var query = new DocumentQuery(
            "workItem",
            "latest-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("visible", "true"))],
            [new DocumentQueryOrder("category"), new DocumentQueryOrder("priority")],
            skip: 1,
            take: 1,
            latestPerKeyPath: "category");

        aggregateCommands.Clear();
        var page = await store.QueryAsync(query);
        var pageAggregates = aggregateCommands.ToArray();
        var pipelines = pageAggregates.Select(command => command["pipeline"].AsBsonArray).ToArray();
        aggregateCommands.Clear();
        var takeNone = await store.QueryAsync(query.Page(skip: null, take: 0));
        var takeNoneAggregates = aggregateCommands.ToArray();
        var count = await store.CountAsync(query.Select(BoundedQueryResultOperation.Count));
        var first = await store.FirstOrDefaultAsync(
            query.Page(0, 1).Select(BoundedQueryResultOperation.First));
        var explanation = await store.ExplainAsync(query);
        var countExplanation = await store.ExplainAsync(
            query.Select(BoundedQueryResultOperation.Count));
        var firstExplanation = await store.ExplainAsync(
            query.Page(0, 1).Select(BoundedQueryResultOperation.First));
        var takeNoneExplanation = await store.ExplainAsync(query.Page(skip: null, take: 0));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("beta-tie-a", Assert.Single(page.Documents).Id);
        Assert.Equal(2, pageAggregates.Length);
        Assert.DoesNotContain(
            pipelines.SelectMany(pipeline => pipeline).Select(stage => stage.AsBsonDocument),
            stage => stage.Contains("$facet"));
        Assert.Contains(pipelines, pipeline => pipeline[^1].AsBsonDocument.Contains("$count"));
        Assert.Contains(pipelines, pipeline =>
            pipeline[^1].AsBsonDocument.Contains("$limit") &&
            pipeline[^2].AsBsonDocument.Contains("$skip"));
        Assert.Empty(takeNone.Documents);
        Assert.Equal(2, takeNone.TotalCount);
        Assert.Single(takeNoneAggregates);
        Assert.True(takeNoneAggregates[0]["pipeline"].AsBsonArray[^1].AsBsonDocument.Contains("$count"));
        Assert.Equal(2, count);
        Assert.True(await store.AnyAsync(query.Select(BoundedQueryResultOperation.Any)));
        Assert.Equal("alpha-low", first!.Id);
        Assert.Equal(
            form == PhysicalStorageForm.PhysicalEntityTable
                ?
                [
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.Page
                ]
                :
                [
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.Page,
                    PhysicalDocumentQueryCommandIdentities.PrimaryHydration
                ],
            explanation.Commands.Select(command => command.Identity));
        Assert.Equal(
            [PhysicalDocumentQueryCommandIdentities.Count],
            countExplanation.Commands.Select(command => command.Identity));
        Assert.Equal(
            [PhysicalDocumentQueryCommandIdentities.Count],
            takeNoneExplanation.Commands.Select(command => command.Identity));
        Assert.Equal(
            form == PhysicalStorageForm.PhysicalEntityTable
                ?
                [
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.First
                ]
                :
                [
                    PhysicalDocumentQueryCommandIdentities.Count,
                    PhysicalDocumentQueryCommandIdentities.First,
                    PhysicalDocumentQueryCommandIdentities.PrimaryHydration
                ],
            firstExplanation.Commands.Select(command => command.Identity));
        Assert.All(explanation.Commands, command => Assert.Equal(1, command.ProviderAppliedMaximumRows));
        var pageExplanation = explanation.Commands.Single(command =>
            command.Kind == PhysicalDocumentQueryCommandKind.Page);
        Assert.Equal(
            explanation.Plan.Order.Select(order => order.Field.Identifier),
            pageExplanation.ProviderAppliedOrder.Select(order => order.FieldIdentifier));
        Assert.All(
            pageExplanation.ProviderAppliedOrder,
            order => Assert.Equal(PhysicalSortDirection.Ascending, order.Direction));
        Assert.Equal(
            explanation.Plan.Order.Select(order => order.IsIdentityTieBreak),
            pageExplanation.ProviderAppliedOrder.Select(order => order.IsIdentityTieBreak));
        Assert.Contains(pageExplanation.ProviderAppliedOrder, order => order.IsIdentityTieBreak);
        Assert.DoesNotContain(
            "COLLSCAN",
            explanation.Commands.Single(command => command.Identity == PhysicalDocumentQueryCommandIdentities.Page).NativePlan,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_pages_apply_documented_live_view_mutation_semantics()
    {
        var database = Database();
        var model = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            pagingSupport: QueryPagingSupport.Cursor);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var firstClient = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        foreach (var id in new[] { "a", "b", "c" })
            await firstClient.SaveAsync(new SaveDocumentRequest("workItem", id, "1", """{"status":"open"}"""));
        var query = new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))],
            [new DocumentQueryOrder("status")],
            take: 1);
        var first = await firstClient.QueryAsync(query);
        var route = model.Routes.Single();
        var boundary = route.Envelope.Identity.Project(Assert.Single(first.Documents).Id).LookupKey;
        var unseen = new[] { "a", "b", "c" }
            .Where(id => id != first.Documents[0].Id)
            .OrderBy(id => route.Envelope.Identity.Project(id).LookupKey, StringComparer.Ordinal)
            .ToArray();
        var before = Enumerable.Range(0, 10_000)
            .Select(index => $"before-{index}")
            .First(id => StringComparer.Ordinal.Compare(
                route.Envelope.Identity.Project(id).LookupKey,
                boundary) < 0);
        var after = Enumerable.Range(0, 10_000)
            .Select(index => $"after-{index}")
            .First(id => StringComparer.Ordinal.Compare(
                route.Envelope.Identity.Project(id).LookupKey,
                boundary) > 0);
        var secondClient = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal(
            DocumentStoreWriteStatus.Deleted,
            (await secondClient.DeleteAsync(new DeleteDocumentRequest("workItem", unseen[0]))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await secondClient.SaveAsync(new SaveDocumentRequest(
                "workItem",
                unseen[1],
                "2",
                """{"status":"closed"}""",
                ExpectedVersion: 1))).Status);
        await secondClient.SaveAsync(new SaveDocumentRequest("workItem", before, "1", """{"status":"open"}"""));
        await secondClient.SaveAsync(new SaveDocumentRequest("workItem", after, "1", """{"status":"open"}"""));

        var resumed = await secondClient.QueryAsync(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            query.Clauses,
            query.Order,
            take: 10,
            continuation: first.NextContinuation));

        Assert.Equal(3, resumed.TotalCount);
        Assert.DoesNotContain(resumed.Documents, document => document.Id == before);
        Assert.Contains(resumed.Documents, document => document.Id == after);
        Assert.DoesNotContain(resumed.Documents, document => unseen.Contains(document.Id, StringComparer.Ordinal));
        Assert.Null(resumed.NextContinuation);
    }

    [Fact]
    public async Task Explain_returns_complete_scan_free_command_receipts_for_every_terminal_operation()
    {
        var database = Database();
        var model = Model(PhysicalStorageForm.SharedDocuments);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "1", "1", """{"status":"open"}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "2", "1", """{"status":"open"}"""));
        var baseQuery = new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]);
        var expected = new Dictionary<BoundedQueryResultOperation, string[]>
        {
            [BoundedQueryResultOperation.Documents] = ["count", "page", "primary-hydration"],
            [BoundedQueryResultOperation.Count] = ["count"],
            [BoundedQueryResultOperation.First] = ["count", "first", "primary-hydration"],
            [BoundedQueryResultOperation.Any] = ["any"]
        };

        var route = Assert.Single(model.Routes);
        foreach (var (operation, commandIdentities) in expected)
        {
            var query = operation == BoundedQueryResultOperation.Any
                ? baseQuery.Page(skip: 7, take: null).Select(operation)
                : baseQuery.Select(operation);
            var explanation = await store.ExplainAsync(query);

            Assert.Equal(commandIdentities, explanation.Commands.Select(command => command.Identity));
            Assert.Equal(route.Indexes.Single().Name, explanation.Plan.IndexName);
            Assert.Equal(
                PhysicalDocumentQueryInvocationFingerprint.Compute(
                    query,
                    explanation.Plan,
                    new DocumentScopeSelection("tenant-a", new("tenant-a"), false)),
                explanation.RuntimeInvocationFingerprint);
            Assert.All(explanation.Commands, command =>
            {
                Assert.Equal("mongodb-json", command.NativePlanFormat);
                Assert.DoesNotContain("COLLSCAN", command.NativePlan, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(command.NativePlan));
            });
            Assert.Contains(explanation.Commands, command =>
                command.NativePlan.Contains(route.Indexes.Single().Name.Identifier, StringComparison.Ordinal) ||
                command.Identity == PhysicalDocumentQueryCommandIdentities.PrimaryHydration);
            Assert.All(explanation.Commands, command =>
                Assert.Equal(
                    command.Kind switch
                    {
                        PhysicalDocumentQueryCommandKind.Page => int.MaxValue,
                        PhysicalDocumentQueryCommandKind.PrimaryHydration
                            when operation == BoundedQueryResultOperation.Documents => 2,
                        _ => 1
                    },
                    command.ProviderAppliedMaximumRows));
            foreach (var ordered in explanation.Commands.Where(command =>
                         command.Kind is PhysicalDocumentQueryCommandKind.Page or
                             PhysicalDocumentQueryCommandKind.First))
            {
                Assert.NotEmpty(ordered.ProviderAppliedOrder);
                Assert.True(ordered.ProviderAppliedOrder[^1].IsIdentityTieBreak);
            }
            if (operation == BoundedQueryResultOperation.Any)
            {
                Assert.True(await store.AnyAsync(query));
                Assert.DoesNotContain("SKIP", Assert.Single(explanation.Commands).NativePlan, StringComparison.Ordinal);
            }
        }

        var takeNone = await store.ExplainAsync(baseQuery.Page(skip: null, take: 0));
        Assert.Equal([PhysicalDocumentQueryCommandIdentities.Count], takeNone.Commands.Select(command => command.Identity));
    }

    [Fact]
    public void Scale_bearing_case_insensitive_substring_query_is_rejected_before_traffic()
    {
        var model = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains },
            BoundedQueryExecutionClass.ScaleBearing);

        var exception = Assert.Throws<InvalidOperationException>(() => new MongoDbPhysicalDocumentStore(
            Database(),
            model,
            DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("Contains", exception.Message, StringComparison.Ordinal);
        Assert.Contains("indexed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ordinary_case_insensitive_substring_query_remains_server_side()
    {
        var model = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains },
            BoundedQueryExecutionClass.Ordinary);

        var store = new MongoDbPhysicalDocumentStore(
            Database(),
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        Assert.NotNull(store);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DateTime_projection_preserves_ticks_offsets_ranges_and_unique_instants(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(
            form,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan
            },
            projectedType: PortablePhysicalType.DateTime,
            valueKind: IndexValueKind.DateTime,
            path: "occurredAt",
            isUnique: true);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "tick-zero", "1", """{"occurredAt":"2026-01-01T00:00:00.0000000Z"}"""))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "tick-one", "1", """{"occurredAt":"2026-01-01T00:00:00.0000001Z"}"""))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "hundred-microseconds", "1", """{"occurredAt":"2026-01-01T00:00:00.0001000Z"}"""))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "same-instant", "1", """{"occurredAt":"2025-12-31T19:00:00.0000000-05:00"}"""))).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "sub-tick", "1", """{"occurredAt":"2026-01-01T00:00:00.00000001Z"}""")));

        var result = await store.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-occurredAt",
            [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan(
                "occurredAt",
                "2025-12-31T19:00:00.0000000-05:00"))]));

        Assert.Equal(["tick-one", "hundred-microseconds"], result.Documents.Select(document => document.Id));
        var route = Assert.Single(model.Routes);
        var projection = route.ProjectedColumns.Single(column => column.Definition.Path == "occurredAt");
        var collection = database.GetCollection<BsonDocument>(
            projection.Target == ExecutableStorageObjectRole.PrimaryStorage
                ? route.PrimaryStorage.Name.Identifier
                : route.LinkedIndexStorage!.Name.Identifier);
        var values = await collection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
        Assert.All(values, document => Assert.Equal(BsonType.Int64, document[projection.Column.Identifier].BsonType));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Required_projection_rejects_absent_and_explicit_null_before_live_mutation(
        PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(form);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "absent", "1", """{"rank":1}""", ExpectedVersion: 0)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "null", "1", """{"status":null,"rank":1}""", ExpectedVersion: 0)));

        Assert.Null(await store.LoadAsync("workItem", "absent"));
        Assert.Null(await store.LoadAsync("workItem", "null"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Typed_default_is_identical_for_live_write_and_backfill(PhysicalStorageForm form)
    {
        var database = Database();
        var initial = ProjectionEvolutionModel(form, includeStatus: false);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest("workItem", "backfilled", "1", """{"rank":1}"""));

        var changed = ProjectionEvolutionModel(form, includeStatus: true, defaultValue: "pending");
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(changed);
        var store = new MongoDbPhysicalDocumentStore(database, changed, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "live", "1", """{"rank":2}"""));

        var result = await store.QueryAsync(new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "pending"))]));
        Assert.Equal(["backfilled", "live"], result.Documents.Select(document => document.Id).Order());
        var route = Assert.Single(changed.Routes);
        var projection = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "status");
        var collection = database.GetCollection<BsonDocument>(
            projection.Target == ExecutableStorageObjectRole.PrimaryStorage
                ? route.PrimaryStorage.Name.Identifier
                : route.LinkedIndexStorage!.Name.Identifier);
        var values = await collection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
        Assert.All(values, document => Assert.Equal("pending", document[projection.Column.Identifier].AsString));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Required_missing_backfill_cannot_publish_target_state(PhysicalStorageForm form)
    {
        var database = Database();
        var initial = ProjectionEvolutionModel(form, includeStatus: false);
        var materializer = new MongoDbGroundworkMaterializer(database);
        await materializer.MaterializeAsync(initial);
        var initialStore = new MongoDbPhysicalDocumentStore(database, initial, DocumentStoreAccess.Scoped(new("tenant-a")));
        await initialStore.SaveAsync(new SaveDocumentRequest("workItem", "missing", "1", """{"rank":1}"""));
        var changed = ProjectionEvolutionModel(form, includeStatus: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => materializer.MaterializeAsync(changed));

        var state = await database.GetCollection<BsonDocument>("groundwork_physical_schema_state")
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(changed.Target.Identity)))
            .SingleAsync();
        Assert.Equal(initial.Target.Fingerprint, state["target_fingerprint"].AsString);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Excluded_unique_index_distinguishes_missing_from_explicit_null(PhysicalStorageForm form)
    {
        var database = Database();
        var model = Model(
            form,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.NotEqual
            },
            isNullable: true,
            isUnique: true);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "missing-a", "1", """{"rank":1}""", ExpectedVersion: 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "missing-b", "1", """{"rank":2}""", ExpectedVersion: 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "null-a", "1", """{"status":null,"rank":3}""", ExpectedVersion: 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "null-b", "1", """{"status":null,"rank":4}""", ExpectedVersion: 0))).Status);

        var equalNull = await store.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", null))]));
        var notNull = await store.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.NotEqual("status", null))]));
        Assert.Equal(["null-a"], equalNull.Documents.Select(document => document.Id));
        Assert.DoesNotContain(notNull.Documents, document => document.Id.StartsWith("missing-", StringComparison.Ordinal));

        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var collection = database.GetCollection<BsonDocument>(
            index.Target == ExecutableStorageObjectRole.PrimaryStorage
                ? route.PrimaryStorage.Name.Identifier
                : route.LinkedIndexStorage!.Name.Identifier);
        var actual = (await (await collection.Indexes.ListAsync()).ToListAsync())
            .Single(document => document["name"].AsString == index.Name.Identifier);
        Assert.Equal(
            new BsonDocument(index.Columns.Last().Column.Identifier, new BsonDocument("$exists", true)),
            actual["partialFilterExpression"].AsBsonDocument);
    }

    [Fact]
    public async Task Included_as_null_omits_partial_membership_and_collapses_missing_with_null()
    {
        var database = Database();
        var model = Model(
            PhysicalStorageForm.PhysicalEntityTable,
            isNullable: true,
            missingValueBehavior: MissingValueBehavior.IncludedAsNull);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = new MongoDbPhysicalDocumentStore(database, model, DocumentStoreAccess.Scoped(new("tenant-a")));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "missing", "1", """{"rank":1}"""));
        await store.SaveAsync(new SaveDocumentRequest("workItem", "null", "1", """{"status":null,"rank":2}"""));

        var result = await store.QueryAsync(new DocumentQuery(
            "workItem", "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", null))]));
        Assert.Equal(["missing", "null"], result.Documents.Select(document => document.Id).Order());
        var route = Assert.Single(model.Routes);
        var index = Assert.Single(route.Indexes);
        var actual = (await (await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
                .Indexes.ListAsync()).ToListAsync())
            .Single(document => document["name"].AsString == index.Name.Identifier);
        Assert.False(actual.Contains("partialFilterExpression"));
    }

    private IMongoDatabase Database() =>
        new MongoClient(container.GetConnectionString()).GetDatabase($"groundwork_{Guid.NewGuid():N}");

    private async Task<(
        IMongoDatabase Database,
        MongoDbPhysicalStorageModel Model,
        MongoDbPhysicalDocumentStore Store)> CreateIdentityStoreAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase)
    {
        var database = Database();
        var model = Model(form, identityCasePolicy: identityCasePolicy);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        return (
            database,
            model,
            new MongoDbPhysicalDocumentStore(
                database,
                model,
                DocumentStoreAccess.Scoped(new("tenant-a"))));
    }

    private static void AssertIdentity(
        BsonDocument document,
        ExecutableDocumentIdentityRoute route,
        PortableStringIdentityProjection expected)
    {
        Assert.Equal(expected.OriginalValue, document[route.OriginalId.Identifier].AsString);
        Assert.Equal(expected.ComparisonKey, document[route.ComparisonKey.Identifier].AsString);
        Assert.Equal(expected.LookupKey, document[route.LookupKey.Identifier].AsString);
    }

    private static Task StealLeaseAsync(IMongoDatabase database, PhysicalSchemaTargetIdentity target) =>
        database.GetCollection<BsonDocument>("groundwork_physical_schema_locks").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", MongoDbPhysicalSchemaExecutor.TargetIdentityDocument(target)),
            Builders<BsonDocument>.Update
                .Set("owner", "stolen-owner")
                .Inc("fence", 1L));

    internal static MongoDbPhysicalStorageModel Model(
        PhysicalStorageForm form,
        IReadOnlySet<PortableQueryOperation>? operations = null,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.ScaleBearing,
        PortablePhysicalType projectedType = PortablePhysicalType.String,
        IndexValueKind valueKind = IndexValueKind.Keyword,
        string path = "status",
        bool isNullable = false,
        string? defaultValue = null,
        bool isUnique = false,
        MissingValueBehavior missingValueBehavior = MissingValueBehavior.Excluded,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.Ordinal,
        QueryPagingSupport pagingSupport = QueryPagingSupport.Offset)
    {
        var binding = new SharedStorageBinding("runtime");
        var projected = new ProjectedColumnDefinition(
            path,
            path,
            projectedType,
            Precision: projectedType == PortablePhysicalType.Decimal ? 18 : null,
            Scale: projectedType == PortablePhysicalType.Decimal ? 4 : null,
            IsNullable: isNullable,
            DefaultValue: defaultValue);
        var indexIdentity = $"by-{path}";
        var physicalIndexColumns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0),
            new(path, 1)
        };
        if (pagingSupport == QueryPagingSupport.Cursor)
            physicalIndexColumns.Add(new("id_lookup_key", physicalIndexColumns.Count));
        var physicalIndex = new PhysicalIndexDefinition(
            indexIdentity,
            physicalIndexColumns,
            isUnique,
            missingValueBehavior: missingValueBehavior);
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding, [projected], [physicalIndex], linkedProjectionLogicalName: "work_items_lookup"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "work_items", indexes: [physicalIndex], linkedProjectedColumns: [projected], linkedProjectionLogicalName: "work_items_lookup"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "work_items", [projected], indexes: [physicalIndex]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var logical = new LogicalIndexDeclaration(
            indexIdentity, [new IndexField(path)], valueKind, isUnique, missingValueBehavior);
        var query = new BoundedQueryDeclaration(
            $"list-by-{path}",
            logical.Identity,
            operations ?? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            pagingSupport,
            executionClass,
            supportsTotalCount: true);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"),
            "Work item",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(stringCasePolicy: identityCasePolicy),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logical],
                [query])
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity($"mongo.{form}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel LatestPerKeyModel(PhysicalStorageForm form)
    {
        var binding = new SharedStorageBinding("runtime");
        var columns = new ProjectedColumnDefinition[]
        {
            new("category", "category", PortablePhysicalType.String),
            new("priority", "priority", PortablePhysicalType.Int32),
            new("visible", "visible", PortablePhysicalType.Boolean)
        };
        var physicalIndex = new PhysicalIndexDefinition(
            "by-category-priority",
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("category", 1),
                new PhysicalIndexColumnDefinition("priority", 2)
            ]);
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                columns,
                [physicalIndex],
                linkedProjectionLogicalName: "work_items_latest_lookup"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "work_items_latest",
                indexes: [physicalIndex],
                linkedProjectedColumns: columns,
                linkedProjectionLogicalName: "work_items_latest_lookup"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "work_items_latest",
                columns,
                indexes: [physicalIndex]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var logical = new LogicalIndexDeclaration(
            physicalIndex.LogicalName,
            [new IndexField("category"), new IndexField("priority", IndexValueKind.Number)],
            IndexValueKind.String,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-category",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields:
            [
                new BoundedQuerySortField("category", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("priority", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count,
                BoundedQueryResultOperation.Any,
                BoundedQueryResultOperation.First
            },
            latestPerKeyPath: "category",
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "visible",
                    IndexValueKind.Boolean,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    isRequired: true)
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"),
            "Work item",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logical],
                [query])
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity($"mongo.latest-per-key.{form}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel ProjectionEvolutionModel(
        PhysicalStorageForm form,
        bool includeStatus,
        bool isNullable = false,
        string? defaultValue = null)
    {
        var binding = new SharedStorageBinding("runtime");
        var status = new ProjectedColumnDefinition(
            "status",
            "status",
            PortablePhysicalType.String,
            IsNullable: isNullable,
            DefaultValue: defaultValue);
        var rank = new ProjectedColumnDefinition(
            "rank",
            "rank",
            PortablePhysicalType.Int32,
            IsNullable: false);
        var projections = includeStatus ? new[] { rank, status } : [rank];
        var indexes = includeStatus
            ? new[]
            {
                new PhysicalIndexDefinition("by-status", [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("status", 1)])
            }
            : [];
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                projections,
                indexes,
                linkedProjectionLogicalName: "projection_items_lookup"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "projection_items",
                indexes: indexes,
                linkedProjectedColumns: projections,
                linkedProjectionLogicalName: "projection_items_lookup"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "projection_items",
                projections,
                indexes: indexes),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var logical = new LogicalIndexDeclaration(
            "by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-status",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"), "Work item", StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable, IdentityPolicy.StringId(), TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(), SerializationPolicy.Json(), [], [], PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                includeStatus ? [logical] : [],
                includeStatus ? [query] : [])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity($"mongo.projection.{form}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "projection_documents", new DocumentEnvelopeDefinition())]
                : []
        });
    }

    private static MongoDbPhysicalStorageModel EntityEvolutionModel(bool includeStatus, bool uniqueStatus = false)
    {
        var rank = new ProjectedColumnDefinition("rank", "rank", PortablePhysicalType.Int32, IsNullable: false);
        var status = new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String, IsNullable: false);
        var projections = includeStatus ? new[] { rank, status } : [rank];
        var indexes = includeStatus
            ? new[]
            {
                new PhysicalIndexDefinition("by-status", [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("status", 1)],
                    isUnique: uniqueStatus)
            }
            : [];
        var logical = new LogicalIndexDeclaration(
            "by-status", [new IndexField("status")], IndexValueKind.Keyword, uniqueStatus, MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-status", logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None, QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"), "Work item", StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable, IdentityPolicy.StringId(), TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(), SerializationPolicy.Json(), [], [], PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                    "evolution_items", projections, indexes: indexes)),
                includeStatus ? [logical] : [],
                includeStatus ? [query] : [])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity("mongo.entity.evolution"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []));
    }

    private static MongoDbPhysicalStorageModel SharedProjectionEvolutionModel(bool includeStatus)
    {
        var binding = new SharedStorageBinding("runtime");
        var rank = new ProjectedColumnDefinition("rank", "rank", PortablePhysicalType.Int32, IsNullable: false);
        var status = new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String, IsNullable: false);
        var definition = PhysicalTableDefinition.SharedDocuments(
            binding,
            includeStatus ? [rank, status] : [rank],
            linkedProjectionLogicalName: "evolution_items_lookup");
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"), "Work item", StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable, IdentityPolicy.StringId(), TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(), SerializationPolicy.Json(), [], [], PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition))
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity("mongo.shared.evolution"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(binding, "evolution_documents", new DocumentEnvelopeDefinition())]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel SharedUniqueBackfillModel(bool includeCategory)
    {
        var binding = new SharedStorageBinding("runtime");
        var status = new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String, IsNullable: false);
        var category = new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, IsNullable: false);
        var physicalIndex = new PhysicalIndexDefinition(
            "by-status",
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("status", 1)
            ],
            isUnique: true);
        var definition = PhysicalTableDefinition.SharedDocuments(
            binding,
            includeCategory ? [status, category] : [status],
            [physicalIndex],
            linkedProjectionLogicalName: "unique_backfill_lookup");
        var logical = new LogicalIndexDeclaration(
            "by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            true,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-status",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workItem"), "Work item", StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable, IdentityPolicy.StringId(), TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(), SerializationPolicy.Json(), [], [], PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logical],
                [query])
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity("mongo.shared.unique-backfill"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(binding, "unique_backfill_documents", new DocumentEnvelopeDefinition())]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static bool IsEmptyFirstBatch(BsonDocument reply) =>
        reply.TryGetValue("cursor", out var cursorValue) &&
        cursorValue.IsBsonDocument &&
        cursorValue.AsBsonDocument.TryGetValue("firstBatch", out var batchValue) &&
        batchValue.IsBsonArray &&
        batchValue.AsBsonArray.Count == 0;

    private sealed class LeaseLossBlockingExecutor(IPhysicalSchemaExecutor inner) : IPhysicalSchemaExecutor
    {
        public TaskCompletionSource OperationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool OperationObservedLeaseLoss { get; private set; }

        public ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
            PhysicalSchemaTargetIdentity target,
            CancellationToken cancellationToken) =>
            inner.AcquireApplicationLockAsync(target, cancellationToken);

        public ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(target, applicationLock, cancellationToken);

        public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken)
        {
            OperationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                OperationObservedLeaseLoss = true;
                throw;
            }
        }

        public ValueTask RecordAppliedStateAsync(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock,
            CancellationToken cancellationToken) =>
            inner.RecordAppliedStateAsync(state, expectedAppliedTargetFingerprint, applicationLock, cancellationToken);
    }
}
