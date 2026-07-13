using System.Collections.Concurrent;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
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

    [Fact]
    public async Task Mutation_explain_uses_the_provider_owned_primary_index()
    {
        var fixture = await CreateAsync();
        await SaveAsync(fixture.Documents, "stale", "stale");
        var route = Assert.Single(fixture.Model.Routes);
        var storage = fixture.Model.StorageByStorageUnit[DocumentKind];
        var query = storage.BoundedQueries.Single(candidate => candidate.Identity == "list-by-status");
        var expected = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.PrimaryStorage);
        var expectedLinked = MongoDbPhysicalMutationStorage.IndexName(
            route,
            query,
            ExecutableStorageObjectRole.LinkedIndexStorage);

        var explanation = await MongoDbPhysicalMutationRuntime.ExplainAsync(
            fixture.Documents,
            fixture.Model.Manifest,
            route,
            fixture.Model.Provider,
            Delete("explain-1", "stale"));

        Assert.Contains(expected, explanation.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("COLLSCAN", explanation.ToJson(), StringComparison.Ordinal);
        var primaryIndexes = await (await fixture.Database
            .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(primaryIndexes, index => index["name"].AsString == expected);
        var linkedIndexes = await (await fixture.Database
            .GetCollection<BsonDocument>(route.LinkedIndexStorage!.Name.Identifier)
            .Indexes.ListAsync()).ToListAsync();
        Assert.Contains(linkedIndexes, index => index["name"].AsString == expectedLinked);
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
}
