using System.Collections.Concurrent;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbBoundedMutationTests : IAsyncLifetime
{
    private const string DocumentKind = "workItem";
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-mutations-rs")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public void Mutation_mirror_fields_are_partitioned_by_storage_unit()
    {
        var first = MongoDbPhysicalMutationStorage.Field(new StorageUnitIdentity("feature-a"), "status");
        var second = MongoDbPhysicalMutationStorage.Field(new StorageUnitIdentity("feature-b"), "status");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Transition_updates_canonical_document_and_linked_projection()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var route = Assert.Single(model.Routes);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            route,
            model.Provider);
        await SaveAsync(documents, "pending-a", "pending");
        await SaveAsync(documents, "pending-b", "pending");
        await SaveAsync(documents, "active", "active");

        var result = await mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "revoke-pending",
            "revoke-1"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Equal("revoked", Status((await documents.LoadAsync(DocumentKind, "pending-a"))!.ContentJson));
        Assert.Equal("revoked", Status((await documents.LoadAsync(DocumentKind, "pending-b"))!.ContentJson));
        Assert.Equal(2, await documents.CountAsync(Query("revoked").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(0, await documents.CountAsync(Query("pending").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(1, await documents.CountAsync(Query("active").Select(BoundedQueryResultOperation.Count)));
    }

    [Fact]
    public async Task Delete_is_exact_idempotent_and_rejects_operation_reuse()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var route = Assert.Single(model.Routes);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            route,
            model.Provider);
        await SaveAsync(documents, "stale-a", "stale");
        await SaveAsync(documents, "stale-b", "stale");
        await SaveAsync(documents, "current", "current");
        var request = Delete("prune-1", "stale");

        var completed = await mutations.ExecuteAsync(request);
        var replayed = await mutations.ExecuteAsync(request);

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), completed);
        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Replayed, 2), replayed);
        Assert.Null(await documents.LoadAsync(DocumentKind, "stale-a"));
        Assert.Null(await documents.LoadAsync(DocumentKind, "stale-b"));
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "current"));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() =>
            mutations.ExecuteAsync(Delete("prune-1", "current")));
    }

    [Fact]
    public async Task Runtime_rejects_a_provider_that_does_not_match_the_compiled_store()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                documents,
                model.Manifest,
                Assert.Single(model.Routes),
                new(model.Provider.Name, "incompatible")));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_rejects_a_manifest_that_does_not_match_the_compiled_store()
    {
        var fixture = await CreateAsync();
        var hostile = fixture.Model.Manifest with
        {
            Identity = new StorageManifestIdentity("hostile.manifest")
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                fixture.Documents,
                hostile,
                Assert.Single(fixture.Model.Routes),
                fixture.Model.Provider));

        Assert.Contains("manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_rejects_altered_declarations_under_the_same_manifest_identity_and_version()
    {
        var fixture = await CreateAsync();
        var unit = Assert.Single(fixture.Model.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        var hostile = fixture.Model.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        storage.NameOverrides,
                        storage.BoundedMutations.Select(mutation =>
                            mutation.Identity == "revoke-pending"
                                ? new BoundedMutationDeclaration(
                                    mutation.Identity,
                                    mutation.PredicateQueryIdentity,
                                    BoundedMutationAction.Delete())
                                : mutation).ToArray())
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                fixture.Documents,
                hostile,
                Assert.Single(fixture.Model.Routes),
                fixture.Model.Provider));

        Assert.Contains("canonical manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_rejects_unrelated_unit_changes_under_the_same_manifest_identity_and_version()
    {
        var fixture = await CreateAsync();
        var unit = Assert.Single(fixture.Model.Manifest.StorageUnits);
        var hostile = fixture.Model.Manifest with
        {
            StorageUnits =
            [
                unit,
                unit with
                {
                    Identity = new StorageUnitIdentity("unrelated"),
                    Tenancy = TenancyPolicy.Global,
                    Serialization = new SerializationPolicy(SerializationKind.ProviderNative)
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                fixture.Documents,
                hostile,
                Assert.Single(fixture.Model.Routes),
                fixture.Model.Provider));

        Assert.Contains("canonical manifest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutation_schema_work_is_published_in_the_durable_physical_schema_plan()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();

        var result = await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);

        Assert.NotNull(result.AppliedState);
        Assert.Contains(result.AppliedState.AppliedOperations, operation =>
            operation.SubjectIdentity == "prune-by-status");
        Assert.Contains(result.AppliedState.AppliedOperations, operation =>
            operation.SubjectIdentity == "revoke-pending");
        Assert.Single(model.Target.ProviderDefinitions.Where(definition =>
            definition.Kind == MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind));
        Assert.Single(result.Plan.Operations
            .OfType<ApplyProviderPhysicalSchemaDefinitionOperation>()
            .Where(operation =>
                operation.Definition.Kind == MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind));
        Assert.Equal(
            new[]
            {
                MongoDbPhysicalMutationSchemaBinding.DefinitionKind,
                MongoDbPhysicalMutationSchemaBinding.DefinitionKind,
                MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind
            },
            result.Plan.Operations
                .OfType<ApplyProviderPhysicalSchemaDefinitionOperation>()
                .Select(operation => operation.Definition.Kind)
                .ToArray());

        var route = Assert.Single(model.Routes);
        var query = model.StorageByStorageUnit[DocumentKind].BoundedQueries.Single(candidate =>
            candidate.Identity == "list-by-status");
        var expectedIndex = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.PrimaryStorage);
        var indexes = await (await database
            .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Single(indexes.Where(index => index["name"].AsString == expectedIndex));
    }

    [Fact]
    public async Task Conflicting_mutation_index_fails_without_publishing_applied_state()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        var route = Assert.Single(model.Routes);
        var storage = model.StorageByStorageUnit[DocumentKind];
        var query = storage.BoundedQueries.Single(candidate => candidate.Identity == "list-by-status");
        var indexName = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.PrimaryStorage);
        await database.CreateCollectionAsync(route.PrimaryStorage.Name.Identifier);
        await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending(route.Envelope.Id.Identifier),
                new CreateIndexOptions { Name = indexName }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model));
        var inspection = await new MongoDbPhysicalSchemaExecutor(database)
            .InspectHistoryAsync(model.Target, CancellationToken.None);

        Assert.Contains("bounded-mutation index", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(inspection.History.AppliedState);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Failure_before_publication_replays_schema_work_and_backfill_on_restart(
        PhysicalStorageForm form)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model(form);
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var originalDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(originalDocuments, "stale-before-failure", "stale");
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            beforeBackfillWrite: null,
            beforeAppliedStateWrite: _ => ValueTask.FromException(new SimulatedSchemaFailureException()));

        await Assert.ThrowsAsync<SimulatedSchemaFailureException>(() =>
            PhysicalSchemaApplication.ApplyAsync(mutationModel.Target, executor));
        var failedInspection = await new MongoDbPhysicalSchemaExecutor(database)
            .InspectHistoryAsync(originalModel.Target, CancellationToken.None);
        Assert.Equal(originalModel.Target.Fingerprint, failedInspection.History.AppliedState?.TargetFingerprint);

        var restarted = await PhysicalSchemaApplication.ApplyAsync(
            mutationModel.Target,
            new MongoDbPhysicalSchemaExecutor(database));
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            mutationModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            mutationModel.Manifest,
            Assert.Single(mutationModel.Routes),
            mutationModel.Provider);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, restarted.Outcome);
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(Delete($"{form}-restart-after-publication-failure", "stale")));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Interrupted_backfill_is_restart_safe_across_document_reincarnation(
        PhysicalStorageForm form)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model(form);
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var originalDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(originalDocuments, "reincarnated", "stale");
        var evolvedDocuments = new MongoDbPhysicalDocumentStore(
            database,
            mutationModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var changedIncarnation = false;
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            beforeBackfillWrite: async _ =>
            {
                if (changedIncarnation)
                    return;
                changedIncarnation = true;
                Assert.Equal(
                    DocumentStoreWriteStatus.Deleted,
                    (await evolvedDocuments.DeleteAsync(new DeleteDocumentRequest(
                        DocumentKind,
                        "reincarnated"))).Status);
                await SaveAsync(evolvedDocuments, "reincarnated", "current");
            });

        var application = await PhysicalSchemaApplication.ApplyAsync(mutationModel.Target, executor);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            evolvedDocuments,
            mutationModel.Manifest,
            Assert.Single(mutationModel.Routes),
            mutationModel.Provider);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, application.Outcome);
        Assert.Equal("current", Status((await evolvedDocuments.LoadAsync(DocumentKind, "reincarnated"))!.ContentJson));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(Delete($"{form}-reincarnation-visible", "current")));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Adding_mutations_backfills_preexisting_primary_and_linked_documents(
        PhysicalStorageForm form)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model(form);
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var originalDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(originalDocuments, "stale-before-declaration", "stale");

        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(mutationModel);
        var evolvedDocuments = new MongoDbPhysicalDocumentStore(
            database,
            mutationModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            evolvedDocuments,
            mutationModel.Manifest,
            Assert.Single(mutationModel.Routes),
            mutationModel.Provider);

        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(Delete($"{form}-backfilled", "stale")));
        Assert.Null(await evolvedDocuments.LoadAsync(DocumentKind, "stale-before-declaration"));
    }

    [Fact]
    public async Task Orphan_linked_mutation_rows_prevent_schema_publication()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model();
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var originalDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(originalDocuments, "canonical", "stale");
        var route = Assert.Single(originalModel.Routes);
        var relationship = route.LinkedRelationship!;
        var linkedCollection = database.GetCollection<BsonDocument>(
            route.LinkedIndexStorage!.Name.Identifier);
        var orphan = (await linkedCollection.Find(FilterDefinition<BsonDocument>.Empty)
            .SingleAsync()).DeepClone().AsBsonDocument;
        orphan[MongoDbPhysicalStorageFields.Id] = ObjectId.GenerateNewId();
        orphan[relationship.DocumentId.Identifier] = "orphan";
        await linkedCollection.InsertOneAsync(orphan);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(mutationModel));
        var inspection = await new MongoDbPhysicalSchemaExecutor(database)
            .InspectHistoryAsync(originalModel.Target, CancellationToken.None);

        Assert.Contains("orphan or duplicate linked mirror state", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalModel.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
    }

    [Fact]
    public async Task Linked_identity_collision_fails_closed_during_public_schema_validation()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(mutationModel);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            mutationModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(documents, "canonical", "stale");
        var route = Assert.Single(mutationModel.Routes);
        var relationship = route.LinkedRelationship!;
        var linkedCollection = database.GetCollection<BsonDocument>(
            route.LinkedIndexStorage!.Name.Identifier);
        var linked = await linkedCollection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var requested = route.Envelope.Identity.Project("collision");
        var retainedLookup = linked[relationship.Identity.LookupKey.Identifier].AsString;
        linked[relationship.Identity.OriginalId.Identifier] = requested.OriginalValue;
        linked[relationship.Identity.ComparisonKey.Identifier] = requested.ComparisonKey;
        await linkedCollection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq(
                MongoDbPhysicalStorageFields.Id,
                linked[MongoDbPhysicalStorageFields.Id]),
            linked);

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(mutationModel));
        var inspection = await new MongoDbPhysicalSchemaExecutor(database)
            .InspectHistoryAsync(mutationModel.Target, CancellationToken.None);

        Assert.Equal(DocumentKind, exception.DocumentKind);
        Assert.Equal(requested.OriginalValue, exception.RequestedId);
        Assert.Equal("canonical", exception.RetainedId);
        Assert.Equal(retainedLookup, exception.LookupKey);
        Assert.Equal(mutationModel.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Stale_writer_is_fenced_between_final_validation_and_publication(
        PhysicalStorageForm form)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model(form);
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var staleDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var staleWriteWasRejected = false;
        var executor = new MongoDbPhysicalSchemaExecutor(
            database,
            timeProvider: null,
            leaseDuration: null,
            beforeBackfillWrite: null,
            beforeAppliedStateWrite: async _ =>
            {
                await Assert.ThrowsAnyAsync<MongoException>(() =>
                    SaveAsync(staleDocuments, $"{form}-publication-race", "stale"));
                staleWriteWasRejected = true;
            });

        var result = await PhysicalSchemaApplication.ApplyAsync(mutationModel.Target, executor);
        var currentDocuments = new MongoDbPhysicalDocumentStore(
            database,
            mutationModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(currentDocuments, $"{form}-current-writer", "current");

        Assert.True(staleWriteWasRejected);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Null(await currentDocuments.LoadAsync(DocumentKind, $"{form}-publication-race"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Published_mutation_schema_rejects_rolling_stale_writers(
        PhysicalStorageForm form)
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var mutationModel = Model(form);
        var originalModel = WithoutMutations(mutationModel);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(originalModel);
        var staleDocuments = new MongoDbPhysicalDocumentStore(
            database,
            originalModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(mutationModel);

        await Assert.ThrowsAnyAsync<MongoException>(() =>
            SaveAsync(staleDocuments, $"{form}-rolling-stale", "stale"));
    }

    [Fact]
    public async Task Mutation_write_fences_compose_and_live_validation_proves_their_presence()
    {
        var fixture = await CreateAsync();
        var route = Assert.Single(fixture.Model.Routes);
        var bindings = fixture.Model.MutationBindingsByStorageUnit[DocumentKind];
        var metadata = await CollectionMetadataAsync(
            fixture.Database,
            route.PrimaryStorage.Name.Identifier);
        var validatorJson = metadata["options"]["validator"].ToJson();

        Assert.All(bindings, binding =>
        {
            Assert.Contains(binding.Schema.FenceField, validatorJson, StringComparison.Ordinal);
            Assert.Contains(binding.Schema.Fingerprint, validatorJson, StringComparison.Ordinal);
        });
        var restart = await new MongoDbGroundworkMaterializer(fixture.Database)
            .MaterializeAsync(fixture.Model);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);

        await fixture.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = route.PrimaryStorage.Name.Identifier,
            ["validator"] = new BsonDocument()
        });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MongoDbGroundworkMaterializer(fixture.Database).MaterializeAsync(fixture.Model));
        Assert.Contains("write fence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_rejects_a_route_that_does_not_match_the_compiled_store()
    {
        var fixture = await CreateAsync();
        var hostile = Assert.Single(MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            path: "status").Routes);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalMutationRuntime.Create(
                fixture.Documents,
                fixture.Model.Manifest,
                hostile,
                fixture.Model.Provider));

        Assert.Contains("route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_document_projection_and_ledger()
    {
        var fixture = await CreateAsync(point =>
            point == MongoDbPhysicalMutationExecutionPoint.BeforeCommit
                ? ValueTask.FromException(new SimulatedMutationFailureException())
                : ValueTask.CompletedTask);
        await SaveAsync(fixture.Documents, "pending", "pending");
        var request = new DocumentMutation(DocumentKind, "revoke-pending", "rollback-1");

        await Assert.ThrowsAsync<SimulatedMutationFailureException>(() =>
            fixture.Mutations.ExecuteAsync(request));

        Assert.Equal("pending", Status((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(1, await fixture.Documents.CountAsync(Query("pending").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Retry_after_acknowledgement_loss_replays_the_durable_exact_outcome()
    {
        var loseAcknowledgement = true;
        var fixture = await CreateAsync(point =>
        {
            if (point != MongoDbPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement ||
                !loseAcknowledgement)
            {
                return ValueTask.CompletedTask;
            }
            loseAcknowledgement = false;
            return ValueTask.FromException(new SimulatedMutationAcknowledgementLossException());
        });
        await SaveAsync(fixture.Documents, "pending-a", "pending");
        await SaveAsync(fixture.Documents, "pending-b", "pending");
        var request = new DocumentMutation(DocumentKind, "revoke-pending", "ack-loss-1");

        await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() =>
            fixture.Mutations.ExecuteAsync(request));

        Assert.Equal(2, await fixture.Documents.CountAsync(Query("revoked").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 2),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Restart_and_provider_upgrade_after_acknowledgement_loss_replay_durable_outcome()
    {
        var fixture = await CreateAsync(point =>
            point == MongoDbPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement
                ? ValueTask.FromException(new SimulatedMutationAcknowledgementLossException())
                : ValueTask.CompletedTask);
        await SaveAsync(fixture.Documents, "pending", "pending");
        var request = new DocumentMutation(DocumentKind, "revoke-pending", "upgrade-ack-loss-1");

        await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() =>
            fixture.Mutations.ExecuteAsync(request));

        var upgradedProvider = new Groundwork.Core.Capabilities.ProviderIdentity(
            fixture.Model.Provider.Name,
            "2.0.0");
        var upgradedModel = MongoDbPhysicalStorageModel.Compile(
            fixture.Model.Manifest,
            upgradedProvider);
        var restartedDocuments = new MongoDbPhysicalDocumentStore(
            fixture.Database,
            upgradedModel,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var restartedMutations = MongoDbPhysicalMutationRuntime.Create(
            restartedDocuments,
            upgradedModel.Manifest,
            Assert.Single(upgradedModel.Routes),
            upgradedProvider);

        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 1),
            await restartedMutations.ExecuteAsync(request));
        Assert.Equal(
            "revoked",
            Status((await restartedDocuments.LoadAsync(DocumentKind, "pending"))!.ContentJson));
    }

    [Fact]
    public async Task Provider_upgrade_revalidates_without_replacing_identical_mutation_schema_definitions()
    {
        var fixture = await CreateAsync();
        var upgradedProvider = new Groundwork.Core.Capabilities.ProviderIdentity(
            fixture.Model.Provider.Name,
            "2.0.0");
        var upgradedModel = MongoDbPhysicalStorageModel.Compile(
            fixture.Model.Manifest,
            upgradedProvider);

        var result = await new MongoDbGroundworkMaterializer(fixture.Database)
            .MaterializeAsync(upgradedModel);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Equal(upgradedProvider, result.AppliedState?.Provider);
        Assert.DoesNotContain(
            result.Plan.Operations,
            operation => operation is ApplyProviderPhysicalSchemaDefinitionOperation);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task All_physical_forms_execute_set_based_transition_and_delete(
        PhysicalStorageForm form)
    {
        var fixture = await CreateAsync(form: form);
        await SaveAsync(fixture.Documents, "pending", "pending");
        await SaveAsync(fixture.Documents, "stale", "stale");
        await SaveAsync(fixture.Documents, "current", "current");

        var transitioned = await fixture.Mutations.ExecuteAsync(
            new DocumentMutation(DocumentKind, "revoke-pending", $"{form}-transition"));
        var deleted = await fixture.Mutations.ExecuteAsync(Delete($"{form}-delete", "stale"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), transitioned);
        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), deleted);
        Assert.Equal("revoked", Status((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "stale"));
        Assert.NotNull(await fixture.Documents.LoadAsync(DocumentKind, "current"));
    }

    [Fact]
    public async Task Nested_transition_updates_addressable_canonical_json_and_survives_a_later_save()
    {
        var fixture = await CreateAsync(path: "state.status");
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(
            new SaveDocumentRequest(
                DocumentKind,
                "nested",
                "1",
                """{"state":{"status":"pending"},"large":9223372036854775807,"decimal":1234567890.1234}""",
                ExpectedVersion: 0))).Status);

        var transitioned = await fixture.Mutations.ExecuteAsync(
            new DocumentMutation(DocumentKind, "revoke-pending", "nested-transition"));
        var afterTransition = (await fixture.Documents.LoadAsync(DocumentKind, "nested"))!;
        using var transitionedJson = JsonDocument.Parse(afterTransition.ContentJson);

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), transitioned);
        Assert.Equal("revoked", transitionedJson.RootElement.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal(long.MaxValue, transitionedJson.RootElement.GetProperty("large").GetInt64());
        Assert.Equal(1234567890.1234m, transitionedJson.RootElement.GetProperty("decimal").GetDecimal());
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(
            new SaveDocumentRequest(
                DocumentKind,
                "nested",
                "1",
                """{"state":{"status":"archived"},"large":9223372036854775807,"decimal":1234567890.1234}""",
                ExpectedVersion: afterTransition.Version))).Status);
        using var savedJson = JsonDocument.Parse(
            (await fixture.Documents.LoadAsync(DocumentKind, "nested"))!.ContentJson);
        Assert.Equal("archived", savedJson.RootElement.GetProperty("state").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Canonical_json_roundtrips_numbers_outside_the_bson_numeric_envelope()
    {
        const string hugeInteger = "12345678901234567890123456789012345678901234567890";
        const string exponentBoundary = "9.999999999999999999999999999999999999e+7000";
        var fixture = await CreateAsync();

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await fixture.Documents.SaveAsync(
            new SaveDocumentRequest(
                DocumentKind,
                "large-numbers",
                "1",
                $$"""{"status":"current","huge":{{hugeInteger}},"exponent":{{exponentBoundary}}}""",
                ExpectedVersion: 0))).Status);

        using var loaded = JsonDocument.Parse(
            (await fixture.Documents.LoadAsync(DocumentKind, "large-numbers"))!.ContentJson);
        Assert.Equal(hugeInteger, loaded.RootElement.GetProperty("huge").GetRawText());
        Assert.Equal(exponentBoundary, loaded.RootElement.GetProperty("exponent").GetRawText());
    }

    [Fact]
    public async Task Explicit_null_matches_a_mutation_while_an_omitted_excluded_value_does_not()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model(isNullable: true);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            Assert.Single(model.Routes),
            model.Provider);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await documents.SaveAsync(new SaveDocumentRequest(
            DocumentKind, "missing", "1", """{"rank":1}""", ExpectedVersion: 0))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await documents.SaveAsync(new SaveDocumentRequest(
            DocumentKind, "null", "1", """{"status":null,"rank":2}""", ExpectedVersion: 0))).Status);

        var result = await mutations.ExecuteAsync(Delete("null-prune-1", null));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "missing"));
        Assert.Null(await documents.LoadAsync(DocumentKind, "null"));
    }

    [Fact]
    public async Task Relationship_and_expiry_range_predicates_execute_server_side()
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = RelationshipExpiryModel();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            Assert.Single(model.Routes),
            model.Provider);
        await SaveAuthorizationAsync(documents, "expired-a", "authorization-a", "2025-01-01T00:00:00Z");
        await SaveAuthorizationAsync(documents, "expired-b", "authorization-a", "2025-12-31T23:59:59.9999999Z");
        await SaveAuthorizationAsync(documents, "future", "authorization-a", "2026-01-01T00:00:00Z");
        await SaveAuthorizationAsync(documents, "other", "authorization-b", "2025-01-01T00:00:00Z");

        var result = await mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "prune-by-authorization-expiry",
            "authorization-prune-1",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("authorizationId", "authorization-a")),
                DocumentQueryClause.Of(DocumentQueryComparison.LessThan("expiresAt", "2026-01-01T00:00:00Z"))
            ]));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Null(await documents.LoadAsync(DocumentKind, "expired-a"));
        Assert.Null(await documents.LoadAsync(DocumentKind, "expired-b"));
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "future"));
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "other"));
    }

    [Fact]
    public async Task Linked_form_mirrors_primary_schema_version_for_exact_set_based_deletes()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = SchemaVersionModel();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            Assert.Single(model.Routes),
            model.Provider);
        await SaveAsync(documents, "schema-1", "current");

        var result = await mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "prune-by-schema-version",
            "schema-version-prune-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("schemaVersion", "1"))]));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.Null(await documents.LoadAsync(DocumentKind, "schema-1"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Mutation_explain_proves_the_exact_primary_and_linked_executable_selectors(
        PhysicalStorageForm form)
    {
        var fixture = await CreateAsync(form: form);
        await SaveAsync(fixture.Documents, "stale", "stale");
        var route = Assert.Single(fixture.Model.Routes);
        var storage = fixture.Model.StorageByStorageUnit[DocumentKind];
        var query = storage.BoundedQueries.Single(candidate => candidate.Identity == "list-by-status");
        var expected = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.PrimaryStorage);

        var explanation = await MongoDbPhysicalMutationRuntime.ExplainAsync(
            fixture.Documents,
            fixture.Model.Manifest,
            route,
            fixture.Model.Provider,
            Delete($"{form}-explain-1", "stale"));

        Assert.Equal(expected, explanation["primary"]["indexName"].AsString);
        Assert.Equal(expected, explanation["primary"]["winningPlanIndex"].AsString);
        var primaryIndexes = await (await fixture.Database
            .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(primaryIndexes, index => index["name"].AsString == expected);
        if (route.LinkedIndexStorage is null)
        {
            Assert.True(explanation["linked"].IsBsonNull);
            return;
        }

        var expectedLinked = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.LinkedIndexStorage);
        Assert.Equal(expectedLinked, explanation["linked"]["indexName"].AsString);
        Assert.Equal(expectedLinked, explanation["linked"]["winningPlanIndex"].AsString);
        var linkedIndexes = await (await fixture.Database
            .GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(linkedIndexes, index => index["name"].AsString == expectedLinked);
    }

    [Fact]
    public async Task Identity_mutation_explain_and_replay_use_projected_evidence_for_primary_and_linked_storage()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = IdentityMutationModel();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        const string savedId = "metric-\U00010428-\u00e9";
        const string equivalentId = "METRIC-\U00010400-\u00c9";
        await SaveAsync(documents, savedId, "stale");
        var route = Assert.Single(model.Routes);
        var request = new DocumentMutation(
            DocumentKind,
            "prune-by-id",
            "identity-prune-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, equivalentId))]);

        var explanation = await MongoDbPhysicalMutationRuntime.ExplainAsync(
            documents,
            model.Manifest,
            route,
            model.Provider,
            request);

        AssertIdentityExplanation(explanation["primary"].AsBsonDocument, route.Envelope.Identity);
        AssertIdentityExplanation(explanation["linked"].AsBsonDocument, route.LinkedRelationship!.Identity);
        var mutations = MongoDbPhysicalMutationRuntime.Create(documents, model.Manifest, route, model.Provider);
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(request));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 1),
            await mutations.ExecuteAsync(request));
        Assert.Null(await documents.LoadAsync(DocumentKind, savedId));
    }

    [Fact]
    public async Task Identity_prefix_mutation_uses_indexed_comparison_ranges_for_primary_and_linked_storage()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = IdentityMutationModel(PortableQueryOperation.StartsWith);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        await SaveAsync(documents, "metric-\U00010428-\u00e9-one", "stale");
        await SaveAsync(documents, "METRIC-\U00010400-\u00c9-two", "stale");
        await SaveAsync(documents, "other", "current");
        var route = Assert.Single(model.Routes);
        var request = new DocumentMutation(
            DocumentKind,
            "prune-by-id",
            "identity-prefix-prune-1",
            [
                DocumentQueryClause.Of(new DocumentQueryComparison(
                    PhysicalDocumentFieldPaths.Id,
                    QueryComparisonOperator.StartsWith,
                    ["METRIC-\U00010400-\u00c9-"]))
            ]);

        var explanation = await MongoDbPhysicalMutationRuntime.ExplainAsync(
            documents,
            model.Manifest,
            route,
            model.Provider,
            request);

        AssertOrderedIdentityExplanation(explanation["primary"].AsBsonDocument, route.Envelope.Identity);
        AssertOrderedIdentityExplanation(explanation["linked"].AsBsonDocument, route.LinkedRelationship!.Identity);
        var mutations = MongoDbPhysicalMutationRuntime.Create(documents, model.Manifest, route, model.Provider);
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 2),
            await mutations.ExecuteAsync(request));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 2),
            await mutations.ExecuteAsync(request));
        Assert.NotNull(await documents.LoadAsync(DocumentKind, "other"));
    }

    [Fact]
    public async Task Transition_and_delete_use_set_based_multi_writes_without_reading_physical_documents()
    {
        var commands = new ConcurrentQueue<(string Name, BsonDocument Command)>();
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(started =>
            commands.Enqueue((started.CommandName, started.Command.DeepClone().AsBsonDocument)));
        var database = new MongoClient(settings).GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var route = Assert.Single(model.Routes);
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            route,
            model.Provider);
        await SaveAsync(documents, "pending-a", "pending");
        await SaveAsync(documents, "pending-b", "pending");
        await SaveAsync(documents, "stale", "stale");
        commands.Clear();

        await mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "revoke-pending",
            "native-evidence-1"));

        var physicalNames = new HashSet<string>(StringComparer.Ordinal)
        {
            route.PrimaryStorage.Name.Identifier,
            route.LinkedIndexStorage!.Name.Identifier
        };
        Assert.DoesNotContain(commands, command =>
            command.Name == "find" &&
            physicalNames.Contains(command.Command["find"].AsString));
        var updates = commands.Where(command =>
            command.Name == "update" &&
            physicalNames.Contains(command.Command["update"].AsString)).ToArray();
        Assert.Equal(2, updates.Length);
        Assert.All(updates, command => Assert.True(
            command.Command["updates"].AsBsonArray[0].AsBsonDocument["multi"].AsBoolean));

        commands.Clear();
        await mutations.ExecuteAsync(Delete("native-delete-evidence-1", "stale"));

        Assert.DoesNotContain(commands, command =>
            command.Name == "find" &&
            physicalNames.Contains(command.Command["find"].AsString));
        var deletes = commands.Where(command =>
            command.Name == "delete" &&
            physicalNames.Contains(command.Command["delete"].AsString)).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.All(deletes, command => Assert.Equal(
            0,
            command.Command["deletes"].AsBsonArray[0].AsBsonDocument["limit"].AsInt32));
    }

    [Fact]
    public async Task Mutation_scope_is_inherited_from_the_store_session()
    {
        var database = new MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var tenantA = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var tenantB = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-b")));
        await SaveAsync(tenantA, "same-id", "stale");
        await SaveAsync(tenantB, "same-id", "stale");

        var result = await MongoDbPhysicalMutationRuntime.Create(
                tenantA,
                model.Manifest,
                Assert.Single(model.Routes),
                model.Provider)
            .ExecuteAsync(Delete("tenant-a-prune", "stale"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.Null(await tenantA.LoadAsync(DocumentKind, "same-id"));
        Assert.NotNull(await tenantB.LoadAsync(DocumentKind, "same-id"));
    }

    [Fact]
    public async Task Cancellation_before_commit_rolls_back_primary_linked_and_ledger()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = await CreateAsync(point =>
        {
            if (point != MongoDbPhysicalMutationExecutionPoint.BeforeCommit)
                return ValueTask.CompletedTask;
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        });
        await SaveAsync(fixture.Documents, "pending", "pending");
        var request = new DocumentMutation(DocumentKind, "revoke-pending", "cancel-1");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Mutations.ExecuteAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("pending", Status((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Concurrent_revoke_and_prune_retries_converge_on_one_durable_outcome_each()
    {
        var fixture = await CreateAsync();
        for (var index = 0; index < 5; index++)
        {
            await SaveAsync(fixture.Documents, $"pending-{index}", "pending");
            await SaveAsync(fixture.Documents, $"stale-{index}", "stale");
        }
        var transition = new DocumentMutation(DocumentKind, "revoke-pending", "concurrent-transition");
        var prune = Delete("concurrent-prune", "stale");

        var transitionResults = await Task.WhenAll(
            fixture.Mutations.ExecuteAsync(transition),
            fixture.Mutations.ExecuteAsync(transition));
        var pruneResults = await Task.WhenAll(
            fixture.Mutations.ExecuteAsync(prune),
            fixture.Mutations.ExecuteAsync(prune));

        Assert.Equal(
            [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
            transitionResults.Select(result => result.Status).Order().ToArray());
        Assert.All(transitionResults, result => Assert.Equal(5, result.AffectedCount));
        Assert.Equal(
            [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
            pruneResults.Select(result => result.Status).Order().ToArray());
        Assert.All(pruneResults, result => Assert.Equal(5, result.AffectedCount));
        Assert.Equal(5, await fixture.Documents.CountAsync(
            Query("revoked").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(0, await fixture.Documents.CountAsync(
            Query("stale").Select(BoundedQueryResultOperation.Count)));
    }

    [Fact]
    public async Task Concurrent_save_and_mutation_retry_without_splitting_primary_and_linked_state()
    {
        var mutationReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = await CreateAsync(async point =>
        {
            if (point != MongoDbPhysicalMutationExecutionPoint.BeforeCommit)
                return;
            mutationReady.TrySetResult(true);
            await releaseMutation.Task;
        });
        await SaveAsync(fixture.Documents, "contended", "pending");

        var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new MongoDbPhysicalDocumentStore(
            fixture.Database,
            fixture.Model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options: null,
            timeProvider: TimeProvider.System,
            hooks: MongoDbPhysicalDocumentStoreExecutionHooks.None with
            {
                TransactionBodyStarting = (_, _, _) =>
                {
                    saveStarted.TrySetResult(true);
                    return ValueTask.CompletedTask;
                }
            });
        var mutation = fixture.Mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "revoke-pending",
            "save-mutation-interleaving"));
        await mutationReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var save = writer.SaveAsync(new SaveDocumentRequest(
            DocumentKind,
            "contended",
            "1",
            """{"status":"archived","rank":1}""",
            ExpectedVersion: 1));
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseMutation.TrySetResult(true);

        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutation);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await save).Status);
        Assert.Equal(
            "revoked",
            Status((await fixture.Documents.LoadAsync(DocumentKind, "contended"))!.ContentJson));
        Assert.Equal(1, await fixture.Documents.CountAsync(
            Query("revoked").Select(BoundedQueryResultOperation.Count)));
        Assert.Equal(0, await fixture.Documents.CountAsync(
            Query("pending").Select(BoundedQueryResultOperation.Count)));
    }

    internal static MongoDbPhysicalStorageModel Model(
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable,
        string path = "status",
        bool isNullable = false)
    {
        var template = MongoDbPhysicalStorageConformanceTests.Model(
            form,
            path: path,
            isNullable: isNullable);
        var unit = Assert.Single(template.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        var manifest = template.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        storage.NameOverrides,
                        [
                            new BoundedMutationDeclaration(
                                "prune-by-status",
                                $"list-by-{path}",
                                BoundedMutationAction.Delete()),
                            new BoundedMutationDeclaration(
                                "revoke-pending",
                                $"list-by-{path}",
                                BoundedMutationAction.Transition(path, ["pending"], "revoked"))
                        ])
                }
            ]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel WithoutMutations(MongoDbPhysicalStorageModel mutationModel)
    {
        var unit = Assert.Single(mutationModel.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        return MongoDbPhysicalStorageModel.Compile(mutationModel.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        storage.NameOverrides)
                }
            ]
        });
    }

    private static MongoDbPhysicalStorageModel RelationshipExpiryModel()
    {
        var template = Model();
        var unit = Assert.Single(template.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        var definition = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(storage.Policy).Definition;
        var authorization = new ProjectedColumnDefinition(
            "authorizationId",
            "authorizationId",
            PortablePhysicalType.String,
            IsNullable: false);
        var expiry = new ProjectedColumnDefinition(
            "expiresAt",
            "expiresAt",
            PortablePhysicalType.DateTime,
            IsNullable: false);
        var compoundPhysical = new PhysicalIndexDefinition(
            "by-authorization-expiry",
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("authorizationId", 1),
                new PhysicalIndexColumnDefinition("expiresAt", 2)
            ]);
        var table = PhysicalTableDefinition.DedicatedDocumentTable(
            definition.FeatureDefaultLogicalName!,
            definition.Envelope,
            definition.Indexes.Append(compoundPhysical).ToArray(),
            definition.SchemaVersion,
            definition.Evolution,
            definition.ProjectedColumns.Concat([authorization, expiry]).ToArray(),
            definition.LinkedProjectionLogicalName,
            definition.LinkedKey);
        var logical = new LogicalIndexDeclaration(
            "by-authorization-expiry",
            [
                new IndexField("authorizationId", IndexValueKind.Keyword),
                new IndexField("expiresAt", IndexValueKind.DateTime)
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "prune-by-authorization-expiry",
            logical.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.LessThan
            },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "authorizationId",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "expiresAt",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.LessThan })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Count
            });
        var manifest = template.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        PhysicalStoragePolicy.Explicit(table),
                        storage.LogicalIndexes.Append(logical).ToArray(),
                        storage.BoundedQueries.Append(query).ToArray(),
                        storage.NameOverrides,
                        storage.BoundedMutations.Append(new BoundedMutationDeclaration(
                            "prune-by-authorization-expiry",
                            query.Identity,
                            BoundedMutationAction.Delete())).ToArray())
                }
            ]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel SchemaVersionModel()
    {
        var template = Model();
        var unit = Assert.Single(template.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        var definition = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(storage.Policy).Definition;
        var physicalIndex = new PhysicalIndexDefinition(
            "by-schema-version",
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("schema_version", 1)
            ],
            target: PhysicalIndexStorageTarget.PrimaryStorage);
        var table = PhysicalTableDefinition.DedicatedDocumentTable(
            definition.FeatureDefaultLogicalName!,
            definition.Envelope,
            definition.Indexes.Append(physicalIndex).ToArray(),
            definition.SchemaVersion,
            definition.Evolution,
            definition.ProjectedColumns,
            definition.LinkedProjectionLogicalName,
            definition.LinkedKey);
        var logicalIndex = new LogicalIndexDeclaration(
            "by-schema-version",
            [new IndexField("schemaVersion")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-schema-version",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var manifest = template.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        PhysicalStoragePolicy.Explicit(table),
                        storage.LogicalIndexes.Append(logicalIndex).ToArray(),
                        storage.BoundedQueries.Append(query).ToArray(),
                        storage.NameOverrides,
                        storage.BoundedMutations.Append(new BoundedMutationDeclaration(
                            "prune-by-schema-version",
                            query.Identity,
                            BoundedMutationAction.Delete())).ToArray())
                }
            ]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalStorageModel IdentityMutationModel(
        PortableQueryOperation operation = PortableQueryOperation.Equal)
    {
        var binding = new SharedStorageBinding("runtime");
        var logicalIndex = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var physicalIndex = new PhysicalIndexDefinition(
            logicalIndex.Identity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                ..(operation == PortableQueryOperation.Equal
                    ? new[]
                    {
                        new PhysicalIndexColumnDefinition("id_lookup_key", 1),
                        new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                    }
                    : [new PhysicalIndexColumnDefinition("id_comparison_key", 1)])
            ]);
        var definition = PhysicalTableDefinition.SharedDocuments(
            binding,
            linkedIndexes: [physicalIndex],
            linkedProjectionLogicalName: "work_items_lookup");
        var query = new BoundedQueryDeclaration(
            "find-by-id",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Work item",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
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
                [logicalIndex],
                [query],
                boundedMutations:
                [
                    new BoundedMutationDeclaration(
                        "prune-by-id",
                        query.Identity,
                        BoundedMutationAction.Delete())
                ])
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity("mongo.identity-mutation"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages =
            [
                new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())
            ]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static void AssertIdentityExplanation(
        BsonDocument explanation,
        ExecutableDocumentIdentityRoute identity)
    {
        var fields = BsonFieldNames(explanation["filter"])
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(identity.LookupKey.Identifier, fields);
        Assert.Contains(identity.ComparisonKey.Identifier, fields);
        Assert.DoesNotContain(identity.OriginalId.Identifier, fields);
        Assert.Equal(explanation["indexName"], explanation["winningPlanIndex"]);
    }

    private static void AssertOrderedIdentityExplanation(
        BsonDocument explanation,
        ExecutableDocumentIdentityRoute identity)
    {
        var fields = BsonFieldNames(explanation["filter"])
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(identity.ComparisonKey.Identifier, fields);
        Assert.Contains("$gte", fields);
        Assert.Contains("$lt", fields);
        Assert.DoesNotContain("$regularExpression", fields);
        Assert.DoesNotContain(identity.LookupKey.Identifier, fields);
        Assert.DoesNotContain(identity.OriginalId.Identifier, fields);
        Assert.Equal(explanation["indexName"], explanation["winningPlanIndex"]);
    }

    private static IEnumerable<string> BsonFieldNames(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            foreach (var element in value.AsBsonDocument)
            {
                yield return element.Name;
                foreach (var nested in BsonFieldNames(element.Value))
                    yield return nested;
            }
            yield break;
        }
        if (!value.IsBsonArray)
            yield break;
        foreach (var nested in value.AsBsonArray.SelectMany(BsonFieldNames))
            yield return nested;
    }

    private async Task<Fixture> CreateAsync(
        Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept = null,
        DocumentStoreAccess? access = null,
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable,
        string path = "status")
    {
        var database = new MongoDB.Driver.MongoClient(container.GetConnectionString())
            .GetDatabase($"groundwork_{Guid.NewGuid():N}");
        var model = Model(form, path);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var documents = new MongoDbPhysicalDocumentStore(
            database,
            model,
            access ?? DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = MongoDbPhysicalMutationRuntime.Create(
            documents,
            model.Manifest,
            Assert.Single(model.Routes),
            model.Provider,
            intercept);
        return new Fixture(database, model, documents, mutations);
    }

    private static DocumentQuery Query(string status) =>
        new(
            DocumentKind,
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", status))]);

    private static DocumentMutation Delete(string operationId, string? status) =>
        new(
            DocumentKind,
            "prune-by-status",
            operationId,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", status))]);

    private static string Status(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;

    private static async Task<BsonDocument> CollectionMetadataAsync(
        IMongoDatabase database,
        string collection)
    {
        using var cursor = await database.ListCollectionsAsync(new ListCollectionsOptions
        {
            Filter = Builders<BsonDocument>.Filter.Eq("name", collection)
        });
        return (await cursor.ToListAsync()).Single();
    }

    private static async Task SaveAsync(
        MongoDbPhysicalDocumentStore documents,
        string id,
        string status)
    {
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await documents.SaveAsync(new SaveDocumentRequest(
                DocumentKind,
                id,
                "1",
                $$"""{"status":"{{status}}","rank":1}""",
                ExpectedVersion: 0))).Status);
    }

    private static async Task SaveAuthorizationAsync(
        MongoDbPhysicalDocumentStore documents,
        string id,
        string authorizationId,
        string expiresAt)
    {
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await documents.SaveAsync(new SaveDocumentRequest(
                DocumentKind,
                id,
                "1",
                $$"""{"status":"valid","authorizationId":"{{authorizationId}}","expiresAt":"{{expiresAt}}"}""",
                ExpectedVersion: 0))).Status);
    }

    private sealed record Fixture(
        MongoDB.Driver.IMongoDatabase Database,
        MongoDbPhysicalStorageModel Model,
        MongoDbPhysicalDocumentStore Documents,
        IBoundedDocumentMutationStore Mutations)
    {
        public IBoundedDocumentMutationStore CreateMutationRuntime() =>
            MongoDbPhysicalMutationRuntime.Create(
                Documents,
                Model.Manifest,
                Assert.Single(Model.Routes),
                Model.Provider);
    }

    private sealed class SimulatedMutationFailureException : Exception;

    private sealed class SimulatedMutationAcknowledgementLossException : Exception;

    private sealed class SimulatedSchemaFailureException : Exception;
}
