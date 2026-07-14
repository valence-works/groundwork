using System.Text.Json;
using Groundwork.Core.Manifests;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using Groundwork.Documents.Scoping;
using Groundwork.Core.Scoping;
using Groundwork.TestInfrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbDocumentStoreTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-rs")
        .Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public void DocumentStoreConstructionRequiresFactoryAdmission() =>
        Assert.Empty(typeof(MongoDbDocumentStore).GetConstructors());

    [Theory]
    [InlineData(StorageIdentityKind.Guid)]
    [InlineData(StorageIdentityKind.Composite)]
    public async Task FactoryAndStorePreserveOrdinalProjectionForAdmittedNonStringIdentityKinds(
        StorageIdentityKind identityKind)
    {
        var databaseName = $"groundwork_{Guid.NewGuid():N}";
        var client = new MongoClient(container.GetConnectionString());
        var manifest = WithIdentityKind(identityKind);
        try
        {
            await using var handle = await MongoDbDocumentStoreFactory.CreateAsync(
                container.GetConnectionString(),
                databaseName,
                manifest,
                MongoDbTestManifests.Provider,
                DocumentStoreAccess.Global);

            var upper = await handle.Store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "A-B", "1.0.0", """{"key":"upper"}"""));
            var lower = await handle.Store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "a-b", "1.0.0", """{"key":"lower"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, upper.Status);
            Assert.Equal(DocumentStoreWriteStatus.Saved, lower.Status);
            var updated = await handle.Store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "A-B", "1.0.0", """{"key":"updated"}""", ExpectedVersion: 1));
            var deleted = await handle.Store.DeleteAsync(new DeleteDocumentRequest(
                "configurationDocument", "a-b", ExpectedVersion: 1));
            Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
            Assert.Equal(DocumentStoreWriteStatus.Deleted, deleted.Status);
            Assert.Contains("updated", (await handle.Store.LoadAsync("configurationDocument", "A-B"))!.ContentJson);
            Assert.Null(await handle.Store.LoadAsync("configurationDocument", "a-b"));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task UnicodeIdentitySpellingConflictPreservesAuthoritativeDocument()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(
            container.GetConnectionString(),
            MongoDbTestManifests.UnicodeIdentityManifest());

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
        Assert.Contains("alpha", (await harness.Store.LoadAsync("configurationDocument", "Straße-Σς"))!.ContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeIdentityLoadAndDeleteReturnAuthoritativeOriginal()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(
            container.GetConnectionString(),
            MongoDbTestManifests.UnicodeIdentityManifest());
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
    public async Task LookupHashCollisionThrowsDedicatedIntegrityException()
    {
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString(), manifest);
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "retained", "1.0.0", """{"key":"retained"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "requested", "1.0.0", """{"key":"requested"}"""));
        var collection = harness.Database.GetCollection<BsonDocument>(
            Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single()));
        var requested = await collection.Find(Builders<BsonDocument>.Filter.Eq("id_original", "requested")).SingleAsync();
        await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "requested"));
        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("id_original", "retained"),
            Builders<BsonDocument>.Update.Set("id_lookup_key", requested["id_lookup_key"].AsString));

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            harness.Store.LoadAsync("configurationDocument", "requested"));

        Assert.Equal("requested", exception.RequestedId);
        Assert.Equal("retained", exception.RetainedId);
    }

    [Fact]
    public async Task MaterializationRejectsDocumentIdentityPolicyDrift()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var materializer = new MongoDbGroundworkMaterializer(database);
        try
        {
            await materializer.MaterializeAsync(
                MongoDbTestManifests.MetadataManifest(),
                MongoDbTestManifests.Provider);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                materializer.MaterializeAsync(
                    MongoDbTestManifests.UnicodeIdentityManifest(),
                    MongoDbTestManifests.Provider));

            Assert.Contains("configurationDocument", exception.Message, StringComparison.Ordinal);
            Assert.Contains("identity schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task MaterializationEvolvesPrePolicyRowsBeforeRecordingIdentitySchemaState()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(collectionName, new CreateCollectionOptions { Collation = new Collation("simple") });
            var collection = database.GetCollection<BsonDocument>(collectionName);
            await collection.InsertOneAsync(LegacyMongoDocument("𐐀"));

            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);
            var store = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.Global);

            var loaded = await store.LoadAsync("configurationDocument", "𐐨");

            Assert.Equal("𐐀", loaded!.Id);
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task MaterializationRejectsPrePolicyIdentityCollisionsWithoutRecordingState()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(collectionName, new CreateCollectionOptions { Collation = new Collation("simple") });
            var collection = database.GetCollection<BsonDocument>(collectionName);
            await collection.InsertManyAsync(new[] { LegacyMongoDocument("A"), LegacyMongoDocument("a") });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider));

            Assert.Contains("collide", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("id_lookup_key")));
            var state = database.GetCollection<BsonDocument>(Groundwork.MongoDb.MongoDbGroundworkNames.IdentitySchemaCollection);
            Assert.Equal(0, await state.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task MaterializationFailsClosedWhenPrePolicyStorageHasNoOriginalIdentity()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(collectionName, new CreateCollectionOptions { Collation = new Collation("simple") });
            var collection = database.GetCollection<BsonDocument>(collectionName);
            var document = LegacyMongoDocument("lost");
            document["_id"] = new BsonDocument("scope", "__groundwork_global__");
            await collection.InsertOneAsync(document);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider));

            Assert.Contains("original identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task ConcurrentMaterializersAdmitOneEquivalentIdentitySchema()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider)));

            var state = database.GetCollection<BsonDocument>(Groundwork.MongoDb.MongoDbGroundworkNames.IdentitySchemaCollection);
            Assert.Equal(1, await state.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var documents = database.GetCollection<BsonDocument>(
                Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single()));
            var indexes = await (await documents.Indexes.ListAsync()).ToListAsync();
            Assert.Single(indexes.Where(index => index["name"] == "ux_groundwork_document_identity_lookup"));
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task LeaseLossDuringBackfillLetsTheNextPolicyConvergeEveryRowAndPublishAlone()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var firstRowCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirstOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? firstMaterialization = null;
        var firstManifest = MongoDbTestManifests.MetadataManifest();
        var secondManifest = MongoDbTestManifests.UnicodeIdentityManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(firstManifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions { Collation = new Collation("simple") });
            var documents = database.GetCollection<BsonDocument>(collectionName);
            var legacyRows = new[]
            {
                LegacyMongoDocument("Straße-Σς"),
                LegacyMongoDocument("second"),
                LegacyMongoDocument("third")
            };
            foreach (var row in legacyRows)
                row["content"].AsBsonDocument["key"] = row["_id"].AsBsonDocument["id"].AsString;
            await documents.InsertManyAsync(legacyRows);
            var hooks = new MongoDbIdentitySchemaAdmissionHooks(async (rowNumber, cancellationToken) =>
            {
                if (rowNumber != 1)
                    return;
                firstRowCommitted.TrySetResult();
                await resumeFirstOwner.Task.WaitAsync(cancellationToken);
            });
            firstMaterialization = new MongoDbGroundworkMaterializer(database, hooks)
                .MaterializeAsync(firstManifest, MongoDbTestManifests.Provider);
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));
            var firstBoundary = await Task.WhenAny(
                firstRowCommitted.Task,
                firstMaterialization,
                timeout);
            if (firstBoundary == firstMaterialization)
                await firstMaterialization;
            Assert.Same(firstRowCommitted.Task, firstBoundary);

            var locks = database.GetCollection<BsonDocument>(
                Groundwork.MongoDb.MongoDbGroundworkNames.IdentitySchemaLockCollection);
            var firstFence = (await locks.Find(
                    Builders<BsonDocument>.Filter.Eq("_id", "document-store-identity-schema"))
                .SingleAsync())["fence"].ToInt64();
            var expireOnServer = new PipelineUpdateDefinition<BsonDocument>(
                new[]
                {
                    new BsonDocument("$set", new BsonDocument("expires_utc", "$$NOW"))
                });
            Assert.Equal(1, (await locks.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", "document-store-identity-schema"),
                expireOnServer)).ModifiedCount);

            await new MongoDbGroundworkMaterializer(database)
                .MaterializeAsync(secondManifest, MongoDbTestManifests.Provider);
            var secondFence = (await locks.Find(
                    Builders<BsonDocument>.Filter.Eq("_id", "document-store-identity-schema"))
                .SingleAsync())["fence"].ToInt64();
            resumeFirstOwner.TrySetResult();
            var lostLease = await Assert.ThrowsAsync<InvalidOperationException>(() => firstMaterialization);

            Assert.Contains("lease", lostLease.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(secondFence > firstFence);
            var unicodePolicy = PortableStringComparison.ForIdentityPolicy(
                StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
            foreach (var document in await documents.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
            {
                var original = document["_id"].AsBsonDocument["id"].AsString;
                var expected = PortableStringComparison.ProjectIdentity(original, unicodePolicy);
                Assert.Equal(original, document["id_original"].AsString);
                Assert.Equal(expected.ComparisonKey, document["id_comparison_key"].AsString);
                Assert.Equal(expected.LookupKey, document["id_lookup_key"].AsString);
            }

            var states = await database
                .GetCollection<BsonDocument>(Groundwork.MongoDb.MongoDbGroundworkNames.IdentitySchemaCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync();
            var state = Assert.Single(states);
            Assert.Equal(
                DocumentStoreIdentitySchemaState.Capture(secondManifest.StorageUnits.Single().IdentityPolicy),
                DocumentStoreIdentitySchemaState.FromCanonicalJson(state["state_json"].AsString));
        }
        finally
        {
            resumeFirstOwner.TrySetResult();
            if (firstMaterialization is not null)
            {
                try
                {
                    await firstMaterialization;
                }
                catch
                {
                    // The test deliberately revokes the first materializer's lease.
                }
            }
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task UnitOfWorkDuplicateAbortsBeforeClassifyingAuthoritativeIdentityOutsideSession()
    {
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString(), manifest);
        await using var unitOfWork = await harness.Store.BeginAsync(
            Groundwork.Documents.UnitOfWork.DocumentCommitScope.Of("configurationDocument"));
        Assert.Null(await unitOfWork.LoadAsync("configurationDocument", "𐐀"));
        var authoritative = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "𐐨", "1", """{"key":"authoritative"}"""));

        var conflict = await unitOfWork.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "𐐀", "1", """{"key":"loser"}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, authoritative.Status);
        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, conflict.Status);
        Assert.Equal("𐐨", conflict.AuthoritativeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.LoadAsync("configurationDocument", "𐐨"));
        Assert.Equal("𐐨", (await harness.Store.LoadAsync("configurationDocument", "𐐀"))!.Id);
    }

    [Fact]
    public async Task UnitOfWorkLookupCollisionAbortsWithoutPartialCommitBeforeFailingIntegrityClassification()
    {
        var manifest = MongoDbTestManifests.UnicodeIdentityManifest();
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString(), manifest);
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "retained", "1", """{"key":"retained"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "requested", "1", """{"key":"requested"}"""));
        var collection = harness.Database.GetCollection<BsonDocument>(
            Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single()));
        var requested = await collection.Find(Builders<BsonDocument>.Filter.Eq("id_original", "requested")).SingleAsync();
        var requestedLookup = requested["id_lookup_key"].AsString;
        await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", "requested"));

        await using var unitOfWork = await harness.Store.BeginAsync(
            Groundwork.Documents.UnitOfWork.DocumentCommitScope.Of("configurationDocument"));
        Assert.Null(await unitOfWork.LoadAsync("configurationDocument", "requested"));
        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("id_original", "retained"),
            Builders<BsonDocument>.Update.Set("id_lookup_key", requestedLookup));

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            unitOfWork.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "requested", "1", """{"key":"loser"}""", ExpectedVersion: 0)));

        Assert.Equal("requested", exception.RequestedId);
        Assert.Equal("retained", exception.RetainedId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.LoadAsync("configurationDocument", "retained"));
        Assert.Equal(1, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal("retained", (await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["content"]["key"].AsString);
    }

    [Fact]
    public async Task UnitOfWorkUniqueConflictIsTerminalAndRollsBackEarlierWrites()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "authoritative", "1", """{"key":"reserved"}"""));
        await using var unitOfWork = await harness.Store.BeginAsync(
            Groundwork.Documents.UnitOfWork.DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await unitOfWork.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "pending", "1", """{"key":"pending"}"""))).Status);

        var conflict = await unitOfWork.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "loser", "1", """{"key":"reserved"}"""));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.LoadAsync("configurationDocument", "pending"));
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "pending"));
        Assert.Null(await harness.Store.LoadAsync("configurationDocument", "loser"));
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", "authoritative"));
    }

    [Fact]
    public async Task UnitOfWorkDeleteWriteConflictClassifiesOutsideSessionWithoutReplay()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "target", "1", """{"key":"first"}"""));
        await using var unitOfWork = await harness.Store.BeginAsync(
            Groundwork.Documents.UnitOfWork.DocumentCommitScope.Of("configurationDocument"));
        Assert.Equal(1, (await unitOfWork.LoadAsync("configurationDocument", "target"))!.Version);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "target", "1", """{"key":"second"}""", ExpectedVersion: 1))).Status);

        var conflict = await unitOfWork.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument", "target", ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.LoadAsync("configurationDocument", "target"));
        Assert.Equal(2, (await harness.Store.LoadAsync("configurationDocument", "target"))!.Version);
    }

    [Fact]
    public async Task MaterializationRejectsPreexistingNonBinaryCollectionCollation()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.MetadataManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions
                {
                    Collation = new Collation("en", strength: CollationStrength.Secondary)
                });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider));

            Assert.Contains(collectionName, exception.Message, StringComparison.Ordinal);
            Assert.Contains("simple", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task MaterializationRejectsConflictingIdentityLookupIndex()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.MetadataManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions { Collation = new Collation("simple") });
            var collection = database.GetCollection<BsonDocument>(collectionName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("storage_scope").Ascending("id_lookup_key"),
                new CreateIndexOptions { Name = "ux_groundwork_document_identity_lookup", Unique = false }));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider));

            Assert.Contains("identity lookup", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task MaterializationCreatesCollectionsWithSimpleBinaryCollation()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var manifest = MongoDbTestManifests.MetadataManifest();
        var collectionName = Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single());
        try
        {
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);

            using var cursor = await database.ListCollectionsAsync(
                new ListCollectionsOptions
                {
                    Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName)
                });
            var collection = Assert.Single(await cursor.ToListAsync());
            var options = collection["options"].AsBsonDocument;
            Assert.True(
                !options.TryGetValue("collation", out var collation) || collation["locale"] == "simple",
                options.ToJson());
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task SatisfiesSharedStorageScopeBlackBoxContract()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        try
        {
            var template = MongoDbTestManifests.MetadataManifest();
            var manifest = template with
            {
                Identity = new Groundwork.Core.Manifests.StorageManifestIdentity("scoped.mongo.conformance"),
                StorageUnits =
                [
                    template.StorageUnits.Single() with
                    {
                        Tenancy = Groundwork.Core.Manifests.TenancyPolicy.Scoped,
                        Physicalization = Groundwork.Core.Manifests.PhysicalizationPolicy.Optimized
                    }
                ]
            };
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);

            await StorageScopeDocumentStoreConformance.VerifyAsync(
                manifest,
                (targetManifest, access) => Task.FromResult<IDocumentStore>(
                    new MongoDbDocumentStore(database, targetManifest, access)));
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task StorageScopeIsEnforcedAcrossCrudQueryUniqueOccAndRestart()
    {
        var client = new MongoClient(container.GetConnectionString());
        var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
        try
        {
            var template = MongoDbTestManifests.MetadataManifest();
            var manifest = template with
            {
                Identity = new Groundwork.Core.Manifests.StorageManifestIdentity("scoped.mongo.documents"),
                StorageUnits =
                [
                    template.StorageUnits.Single() with
                    {
                        Tenancy = Groundwork.Core.Manifests.TenancyPolicy.Scoped,
                        Physicalization = Groundwork.Core.Manifests.PhysicalizationPolicy.Optimized
                    }
                ]
            };
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);
            var collection = database.GetCollection<BsonDocument>(
                Groundwork.MongoDb.MongoDbGroundworkNames.CollectionName(manifest.StorageUnits.Single()));
            var indexDocuments = await (await collection.Indexes.ListAsync())
                .ToListAsync();
            var byKey = indexDocuments.Single(index => index["name"] == "by-key")["key"].AsBsonDocument;
            Assert.Equal(2, byKey.ElementCount);
            Assert.Equal("storage_scope", byKey.GetElement(0).Name);
            Assert.StartsWith("physicalized.", byKey.GetElement(1).Name, StringComparison.Ordinal);
            var a = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
            var b = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.Scoped(new StorageScope("TENANT-A")));
            var unicode = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.Scoped(new StorageScope("租户-Å")));
            var all = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.PrivilegedAcrossScopes(
                new PrivilegedStorageAccess("scope conformance")));
            const string kind = "configurationDocument";
            const string key = "same-key";

            var savedA = await a.SaveAsync(new SaveDocumentRequest(kind, "same-id", "1", $$"""{"key":"{{key}}","category":"A"}"""));
            var savedB = await b.SaveAsync(new SaveDocumentRequest(kind, "same-id", "1", $$"""{"key":"{{key}}","category":"B"}"""));
            var savedUnicode = await unicode.SaveAsync(new SaveDocumentRequest(kind, "same-id", "1", $$"""{"key":"{{key}}","category":"Unicode"}"""));
            await a.SaveAsync(new SaveDocumentRequest(kind, "only-a", "1", """{"key":"only-a","category":"A"}"""));

            Assert.Equal(DocumentStoreWriteStatus.Saved, savedA.Status);
            Assert.Equal(DocumentStoreWriteStatus.Saved, savedB.Status);
            Assert.Equal("tenant-a", savedA.Document!.Scope!.Value);
            Assert.Equal("TENANT-A", savedB.Document!.Scope!.Value);
            Assert.Equal("租户-Å", savedUnicode.Document!.Scope!.Value);
            var persistedA = await collection.Find(
                    Builders<BsonDocument>.Filter.Eq("_id.scope", "tenant-a") &
                    Builders<BsonDocument>.Filter.Eq("_id.id", "same-id"))
                .SingleAsync();
            Assert.Equal("tenant-a", persistedA["storage_scope"].AsString);
            Assert.Equal("tenant-a", persistedA["_id"].AsBsonDocument["scope"].AsString);
            Assert.Equal("same-id", persistedA["_id"].AsBsonDocument["id"].AsString);
            Assert.Null(await b.LoadAsync(kind, "only-a"));
            Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.SaveAsync(new SaveDocumentRequest(
                kind, "only-a", "1", """{"key":"stolen"}""", ExpectedVersion: 1))).Status);
            Assert.Equal(DocumentStoreWriteStatus.NotFound, (await b.DeleteAsync(new DeleteDocumentRequest(kind, "only-a"))).Status);
            Assert.NotNull(await a.LoadAsync(kind, "only-a"));
            Assert.Single(await a.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
            Assert.Single(await b.QueryAsync(new DocumentStoreQuery(kind, "by-key", key)));
            Assert.Equal(3, (await all.QueryAsync(new DocumentStoreQuery(kind, "by-key", key))).Count);
            Assert.Equal(2, (await a.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
            Assert.Equal(1, (await b.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
            Assert.Equal(4, (await all.QueryAsync(new PortableDocumentQuery(kind))).TotalCount);
            Assert.True(await a.AnyAsync(new PortableDocumentQuery(kind)));
            Assert.Equal("tenant-a", (await a.FirstOrDefaultAsync(new PortableDocumentQuery(kind)))!.Scope!.Value);
            Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await a.SaveAsync(new SaveDocumentRequest(
                kind, NewId(), "1", $$"""{"key":"{{key}}"}"""))).Status);

            await using (var unitOfWork = await a.BeginAsync(Groundwork.Documents.UnitOfWork.DocumentCommitScope.Of(kind)))
            {
                await unitOfWork.SaveAsync(new SaveDocumentRequest(kind, "rolled-back", "1", """{"key":"rollback"}"""));
                await unitOfWork.RollbackAsync();
            }
            Assert.Null(await a.LoadAsync(kind, "rolled-back"));

            var restarted = new MongoDbDocumentStore(database, manifest, DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
            Assert.NotNull(await restarted.LoadAsync(kind, "same-id"));
        }
        finally
        {
            await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task SaveLoadUpdateQueryAndDeleteMaintainIndexes()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
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

        Assert.Single(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", firstKey)));

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
        Assert.Equal(id, deleted.AuthoritativeId);
        Assert.Null(await store.LoadAsync("configurationDocument", id));
        Assert.Empty(await store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", secondKey)));
    }

    [Fact]
    public async Task UndeclaredIndexQueryFailsClearly()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());

        var exception = await Assert.ThrowsAsync<UndeclaredDocumentIndexException>(() =>
            harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "missing-index", NewValue("alpha"))));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("missing-index", exception.IndexName);
    }

    [Fact]
    public async Task UniqueIndexesAreEnforcedByMongoDb()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
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
    public async Task QueryWithZeroTakeReturnsNoDocuments()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        var key = NewValue("zero-take");

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            $$"""{"key":"{{key}}","category":"system"}"""));

        var documents = await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", key, take: 0));

        Assert.Empty(documents);
    }

    [Fact]
    public async Task SparseUniqueIndexesAllowMissingValues()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());

        var first = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            """{"category":"system"}"""));
        var second = await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            NewId(),
            "1.0.0",
            """{"category":"system"}"""));

        Assert.Equal(DocumentStoreWriteStatus.Saved, first.Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, second.Status);
    }

    [Fact]
    public async Task ConcurrentUnguardedSavesForSameIdReturnStructuredResults()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        var id = NewId();
        var saves = Enumerable
            .Range(0, 8)
            .Select(index => harness.Store.SaveAsync(new SaveDocumentRequest(
                "configurationDocument",
                id,
                "1.0.0",
                $$"""{"key":"{{NewValue($"same-id-{index}")}}","category":"system"}""")));

        var results = await Task.WhenAll(saves);
        var expectedStatuses = new HashSet<DocumentStoreWriteStatus>
        {
            DocumentStoreWriteStatus.Saved,
            DocumentStoreWriteStatus.ConcurrencyConflict
        };

        Assert.All(results, result => Assert.Contains(
            result.Status,
            expectedStatuses));
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", id));
    }

    [Fact]
    public async Task LoadedContentJsonRemainsStandardJsonForLargeNumbers()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        var id = NewId();
        const long largeValue = 1717254000000;

        await harness.Store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1.0.0",
            $$"""{"key":"{{NewValue("large")}}","category":"system","value":{{largeValue}}}"""));

        var loaded = await harness.Store.LoadAsync("configurationDocument", id);

        Assert.NotNull(loaded);
        using var content = JsonDocument.Parse(loaded.ContentJson);
        Assert.Equal(largeValue, content.RootElement.GetProperty("value").GetInt64());
    }

    [Fact]
    public async Task StaleExpectedVersionDoesNotUpdateDocument()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        var id = NewId();
        var firstKey = NewValue("alpha");
        var secondKey = NewValue("beta");
        var staleKey = NewValue("gamma");

        await harness.Store.SaveAsync(new SaveDocumentRequest("configurationDocument", id, "1.0.0", $$"""{"key":"{{firstKey}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest("configurationDocument", id, "1.0.0", $$"""{"key":"{{secondKey}}","category":"system"}""", ExpectedVersion: 1));

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
    public async Task StaleExpectedVersionDoesNotDeleteDocument()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
        var id = NewId();
        var key = NewValue("beta");

        await harness.Store.SaveAsync(new SaveDocumentRequest("configurationDocument", id, "1.0.0", $$"""{"key":"{{NewValue("alpha")}}","category":"system"}"""));
        await harness.Store.SaveAsync(new SaveDocumentRequest("configurationDocument", id, "1.0.0", $$"""{"key":"{{key}}","category":"system"}""", ExpectedVersion: 1));

        var stale = await harness.Store.DeleteAsync(new DeleteDocumentRequest("configurationDocument", id, ExpectedVersion: 1));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, stale.Status);
        Assert.NotNull(await harness.Store.LoadAsync("configurationDocument", id));
        Assert.Single(await harness.Store.QueryAsync(new DocumentStoreQuery("configurationDocument", "by-key", key)));
    }

    [Fact]
    public async Task ExpectedVersionZeroCreatesWhenAbsentAndConflictsWhenPresent()
    {
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
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

        // Create-only against an existing document is refused and mutates nothing.
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
        await using var harness = await MongoDbDocumentStoreHarness.Create(container.GetConnectionString());
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

    private static BsonDocument LegacyMongoDocument(string id) => new()
    {
        ["_id"] = new BsonDocument
        {
            ["scope"] = "__groundwork_global__",
            ["id"] = id
        },
        ["storage_scope"] = "__groundwork_global__",
        ["schema_version"] = "1",
        ["version"] = 1L,
        ["content"] = new BsonDocument("key", "legacy"),
        ["created_utc"] = "2026-01-01T00:00:00Z",
        ["updated_utc"] = "2026-01-01T00:00:00Z"
    };

    private static string NewId() => $"doc-{Guid.NewGuid():N}";

    private static string NewValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static StorageManifest WithIdentityKind(StorageIdentityKind kind)
    {
        var manifest = MongoDbTestManifests.MetadataManifest();
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

    private sealed class MongoDbDocumentStoreHarness : IAsyncDisposable
    {
        private MongoDbDocumentStoreHarness(IMongoClient client, IMongoDatabase database, MongoDbDocumentStore store)
        {
            Client = client;
            Database = database;
            Store = store;
        }

        private IMongoClient Client { get; }
        public IMongoDatabase Database { get; }
        public MongoDbDocumentStore Store { get; }

        public static async Task<MongoDbDocumentStoreHarness> Create(
            string connectionString,
            Groundwork.Core.Manifests.StorageManifest? manifest = null)
        {
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase($"groundwork_{Guid.NewGuid():N}");
            manifest ??= MongoDbTestManifests.MetadataManifest();
            await new MongoDbGroundworkMaterializer(database).MaterializeAsync(manifest, MongoDbTestManifests.Provider);
            return new MongoDbDocumentStoreHarness(client, database, new MongoDbDocumentStore(database, manifest, Groundwork.Documents.Scoping.DocumentStoreAccess.Global));
        }

        public async ValueTask DisposeAsync() =>
            await Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }
}
