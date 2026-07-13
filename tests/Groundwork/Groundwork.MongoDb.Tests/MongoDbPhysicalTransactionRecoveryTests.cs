using System.Collections.Concurrent;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace Groundwork.MongoDb.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoDbPhysicalTransactionRecoveryCollection
{
    public const string Name = "MongoDB physical transaction recovery";
}

[Collection(MongoDbPhysicalTransactionRecoveryCollection.Name)]
public sealed class MongoDbPhysicalTransactionRecoveryTests : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:7.0.24")
        .WithReplicaSet("groundwork-rs")
        .WithCommand("--setParameter", "enableTestCommands=1")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Transient_transaction_error_retries_the_body_from_a_fresh_session_once()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(body: (session, _, _) =>
        {
            bodies.Enqueue(session);
            return ValueTask.CompletedTask;
        }));
        await ConfigureCommandFailureAsync(database, "insert", new BsonDocument("times", 1), 112, ["TransientTransactionError"]);

        var result = await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "retry-once", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, result.Status);
        Assert.Equal(1, (await store.LoadAsync("workItem", "retry-once"))!.Version);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(2, bodies.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public async Task Transient_transaction_retry_attempt_budget_terminates_persistent_failure()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumTransactionAttempts = 2,
            TransactionRetryTimeout = TimeSpan.FromMinutes(1)
        }, Hooks(body: (session, _, _) =>
        {
            bodies.Enqueue(session);
            return ValueTask.CompletedTask;
        }));
        await ConfigureCommandFailureAsync(database, "insert", "alwaysOn", 112, ["TransientTransactionError"]);
        try
        {
            await Assert.ThrowsAnyAsync<MongoException>(() => store.SaveAsync(new SaveDocumentRequest(
                "workItem", "retry-exhausted", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0)));
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }

        Assert.Equal(2, bodies.Count);
        Assert.Equal(2, bodies.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Null(await store.LoadAsync("workItem", "retry-exhausted"));
    }

    [Fact]
    public async Task Transient_transaction_retry_elapsed_budget_can_disable_body_retry()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumTransactionAttempts = 10,
            TransactionRetryTimeout = TimeSpan.Zero
        }, Hooks(body: (session, _, _) =>
        {
            bodies.Enqueue(session);
            return ValueTask.CompletedTask;
        }));
        await ConfigureCommandFailureAsync(database, "insert", "alwaysOn", 112, ["TransientTransactionError"]);
        try
        {
            await Assert.ThrowsAnyAsync<MongoException>(() => store.SaveAsync(new SaveDocumentRequest(
                "workItem", "retry-timeout", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0)));
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }

        Assert.Single(bodies);
        Assert.Null(await store.LoadAsync("workItem", "retry-timeout"));
    }

    [Fact]
    public async Task Unknown_commit_result_retries_only_commit_on_the_same_session()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var commits = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(
            body: (session, _, _) =>
            {
                bodies.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            commit: (session, _, _) =>
            {
                commits.Enqueue(session);
                return ValueTask.CompletedTask;
            }));
        await ConfigureCommandFailureAsync(database, "commitTransaction", new BsonDocument("times", 1), 91, ["UnknownTransactionCommitResult"]);

        var result = await store.SaveAsync(new SaveDocumentRequest(
            "workItem", "commit-retry", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.Saved, result.Status);
        Assert.Equal(1, (await store.LoadAsync("workItem", "commit-retry"))!.Version);
        Assert.Single(bodies);
        Assert.Equal(2, commits.Count);
        Assert.Single(commits.Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public async Task Persistent_unknown_commit_result_exhausts_to_acknowledgement_uncertain()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var commits = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumCommitAttempts = 2,
            CommitRetryTimeout = TimeSpan.FromMinutes(1)
        }, Hooks(
            body: (session, _, _) =>
            {
                bodies.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            commit: (session, _, _) =>
            {
                commits.Enqueue(session);
                return ValueTask.CompletedTask;
            }));
        await ConfigureCommandFailureAsync(database, "commitTransaction", "alwaysOn", 91, ["UnknownTransactionCommitResult"]);
        try
        {
            var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
                store.SaveAsync(new SaveDocumentRequest(
                    "workItem", "commit-exhausted", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0)));

            Assert.Equal(["workItem"], exception.DocumentKinds);
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }

        Assert.Single(bodies);
        Assert.Equal(2, commits.Count);
        Assert.Single(commits.Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public async Task Unknown_commit_elapsed_budget_can_disable_commit_retry()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var commits = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumCommitAttempts = 10,
            CommitRetryTimeout = TimeSpan.Zero
        }, Hooks(
            body: (session, _, _) =>
            {
                bodies.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            commit: (session, _, _) =>
            {
                commits.Enqueue(session);
                return ValueTask.CompletedTask;
            }));
        await ConfigureCommandFailureAsync(
            database,
            "commitTransaction",
            new BsonDocument("times", 1),
            91,
            ["UnknownTransactionCommitResult"]);

        await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
            store.SaveAsync(new SaveDocumentRequest(
                "workItem", "commit-timeout", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0)));

        Assert.Single(bodies);
        Assert.Single(commits);
    }

    [Fact]
    public async Task Nonretryable_error_after_unknown_commit_result_remains_acknowledgement_uncertain()
    {
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var commits = new ConcurrentQueue<IClientSessionHandle>();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(
            body: (session, _, _) =>
            {
                bodies.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            commit: (session, _, _) =>
            {
                commits.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            unknown: async (_, _, _, _) =>
                await ConfigureCommandFailureAsync(database, "commitTransaction", new BsonDocument("times", 1), 2, [])));
        await ConfigureCommandFailureAsync(
            database,
            "commitTransaction",
            new BsonDocument("times", 1),
            91,
            ["UnknownTransactionCommitResult"]);

        var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
            store.SaveAsync(new SaveDocumentRequest(
                "workItem", "commit-later-terminal", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0)));

        Assert.IsAssignableFrom<MongoException>(exception.InnerException);
        Assert.Single(bodies);
        Assert.Equal(2, commits.Count);
        Assert.Single(commits.Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public async Task Explicit_unit_of_work_reports_immutable_normalized_scope_after_caller_mutation()
    {
        var database = Database();
        var model = MultiKindModel();
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumCommitAttempts = 1,
            CommitRetryTimeout = TimeSpan.FromMinutes(1)
        });
        var callerKinds = new List<string> { "workItem", "auditItem" };
        var scope = new DocumentCommitScope(callerKinds);
        await using var transaction = await store.BeginAsync(scope);
        callerKinds.Clear();
        callerKinds.Add("replacement");
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "scope-snapshot", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0))).Status);
        await ConfigureCommandFailureAsync(
            database,
            "commitTransaction",
            new BsonDocument("times", 1),
            91,
            ["UnknownTransactionCommitResult"]);

        var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
            transaction.CommitAsync());

        Assert.Equal(["auditItem", "workItem"], exception.DocumentKinds);
        Assert.Equal(
            "The document transaction for [auditItem, workItem] may have committed before acknowledgement was lost.",
            exception.Message);
        await AssertTerminalAsync(transaction);
    }

    [Fact]
    public async Task Factory_rejects_invalid_retry_options_before_creating_database_state()
    {
        var databaseName = $"groundwork_invalid_options_{Guid.NewGuid():N}";
        var database = new MongoClient(container.GetConnectionString()).GetDatabase(databaseName);
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MongoDbDocumentStoreFactory.CreatePhysicalAsync(
                container.GetConnectionString(),
                databaseName,
                model.Manifest,
                model.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a")),
                options: new MongoDbPhysicalDocumentStoreOptions { MaximumTransactionAttempts = 0 }));

        using var collections = await database.ListCollectionNamesAsync();
        Assert.Empty(await collections.ToListAsync());
    }

    [Fact]
    public async Task Below_minimum_schema_lease_is_rejected_before_creating_database_state()
    {
        var database = Database();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MongoDbPhysicalSchemaExecutor(
            database,
            leaseDuration: MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration - TimeSpan.FromTicks(1)));

        using var collections = await database.ListCollectionNamesAsync();
        Assert.Empty(await collections.ToListAsync());
    }

    [Fact]
    public async Task Cancellation_observed_immediately_after_unknown_commit_result_remains_acknowledgement_uncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var bodies = new ConcurrentQueue<IClientSessionHandle>();
        var unknownObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAfterUnknown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, new MongoDbPhysicalDocumentStoreOptions
        {
            MaximumCommitAttempts = 10,
            CommitRetryTimeout = TimeSpan.FromMinutes(1)
        }, Hooks(
            body: (session, _, _) =>
            {
                bodies.Enqueue(session);
                return ValueTask.CompletedTask;
            },
            unknown: async (_, _, _, _) =>
            {
                unknownObserved.TrySetResult();
                await continueAfterUnknown.Task;
            }));
        await ConfigureCommandFailureAsync(database, "commitTransaction", "alwaysOn", 91, ["UnknownTransactionCommitResult"]);
        try
        {
            var save = store.SaveAsync(new SaveDocumentRequest(
                "workItem", "commit-cancelled", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0), cancellation.Token);
            await unknownObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            continueAfterUnknown.TrySetResult();
            var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
                save);

            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            Assert.Equal(["workItem"], exception.DocumentKinds);
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }

        Assert.Single(bodies);
    }

    [Fact]
    public async Task Cancellation_during_post_unknown_commit_retry_delay_remains_acknowledgement_uncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var unknownObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelayStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRetryDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(
            unknown: (_, _, _, _) =>
            {
                unknownObserved.TrySetResult();
                return ValueTask.CompletedTask;
            },
            beforeCommitRetryDelay: async (_, _, _) =>
            {
                retryDelayStarting.TrySetResult();
                await continueRetryDelay.Task;
            }));
        await ConfigureCommandFailureAsync(database, "commitTransaction", "alwaysOn", 91, ["UnknownTransactionCommitResult"]);
        try
        {
            var save = store.SaveAsync(new SaveDocumentRequest(
                "workItem", "commit-delay-cancelled", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0), cancellation.Token);
            await retryDelayStarting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(unknownObserved.Task.IsCompletedSuccessfully);
            cancellation.Cancel();
            continueRetryDelay.TrySetResult();
            var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() => save);

            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }
    }

    [Fact]
    public async Task Cancellation_at_next_post_unknown_commit_attempt_remains_acknowledgement_uncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var unknownObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelayCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAfterRetryDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(
            unknown: (_, _, _, _) =>
            {
                unknownObserved.TrySetResult();
                return ValueTask.CompletedTask;
            },
            commitRetryDelayCompleted: async (_, _, _) =>
            {
                retryDelayCompleted.TrySetResult();
                await continueAfterRetryDelay.Task;
            }));
        await ConfigureCommandFailureAsync(database, "commitTransaction", "alwaysOn", 91, ["UnknownTransactionCommitResult"]);
        try
        {
            var save = store.SaveAsync(new SaveDocumentRequest(
                "workItem", "commit-next-attempt-cancelled", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0), cancellation.Token);
            await retryDelayCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(unknownObserved.Task.IsCompletedSuccessfully);
            cancellation.Cancel();
            continueAfterRetryDelay.TrySetResult();
            var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() => save);

            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }
        finally
        {
            await DisableCommandFailureAsync(database);
        }
    }

    [Fact]
    public async Task Cancellation_after_initial_commit_invocation_is_acknowledgement_uncertain()
    {
        using var cancellation = new CancellationTokenSource();
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(commitInvoked: (_, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }));

        var exception = await Assert.ThrowsAsync<DocumentCommitAcknowledgementUncertainException>(() =>
            store.SaveAsync(new SaveDocumentRequest(
                "workItem", "cancel-invoked", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0),
                cancellation.Token));

        Assert.Equal(["workItem"], exception.DocumentKinds);
    }

    [Fact]
    public async Task Cancellation_before_initial_commit_invocation_remains_ordinary_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var commitInvoked = false;
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model, hooks: Hooks(
            commit: (_, _, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled(cancellation.Token);
            },
            commitInvoked: (_, _, _) =>
            {
                commitInvoked = true;
                return ValueTask.CompletedTask;
            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "cancel-before-invocation", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0),
            cancellation.Token));
        Assert.False(commitInvoked);
    }

    [Fact]
    public async Task Explicit_unit_of_work_transient_failure_is_structured_terminal_and_rolls_back_prior_write()
    {
        var database = Database();
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        await new MongoDbGroundworkMaterializer(database).MaterializeAsync(model);
        var store = Store(database, model);

        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("workItem"));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "staged-before-transient", "1", """{"status":"open","rank":1}""", ExpectedVersion: 0))).Status);
        await ConfigureCommandFailureAsync(database, "insert", new BsonDocument("times", 1), 112, ["TransientTransactionError"]);

        var conflict = await transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "transient", "1", """{"status":"open","rank":2}""", ExpectedVersion: 0));

        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, conflict.Status);
        Assert.Null(await store.LoadAsync("workItem", "staged-before-transient"));
        await AssertTerminalAsync(transaction);
    }

    private IMongoDatabase Database() =>
        new MongoClient(container.GetConnectionString()).GetDatabase($"groundwork_recovery_{Guid.NewGuid():N}");

    private static MongoDbPhysicalDocumentStore Store(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        MongoDbPhysicalDocumentStoreOptions? options = null,
        MongoDbPhysicalDocumentStoreExecutionHooks? hooks = null) =>
        new(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options,
            TimeProvider.System,
            hooks);

    private static MongoDbPhysicalStorageModel MultiKindModel()
    {
        var first = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var firstUnit = Assert.Single(first.Manifest.StorageUnits);
        var firstPhysicalStorage = firstUnit.PhysicalStorage!;
        var firstDefinition = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(firstPhysicalStorage.Policy).Definition;
        var secondUnit = firstUnit with
        {
            Identity = new("auditItem"),
            DisplayName = "Audit item",
            PhysicalStorage = new StorageUnitPhysicalStorage(
                firstPhysicalStorage.ProvisioningMode,
                PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                    "audit_items",
                    firstDefinition.ProjectedColumns,
                    indexes: firstDefinition.Indexes)),
                firstPhysicalStorage.LogicalIndexes,
                firstPhysicalStorage.BoundedQueries,
                firstPhysicalStorage.NameOverrides)
        };
        var manifest = first.Manifest with
        {
            Identity = new("mongo.multikind"),
            StorageUnits = [firstUnit, secondUnit]
        };
        return MongoDbPhysicalStorageModel.Compile(manifest);
    }

    private static MongoDbPhysicalDocumentStoreExecutionHooks Hooks(
        Func<IClientSessionHandle, int, CancellationToken, ValueTask>? body = null,
        Func<IClientSessionHandle, int, CancellationToken, ValueTask>? commit = null,
        Func<IClientSessionHandle, int, CancellationToken, ValueTask>? commitInvoked = null,
        Func<IClientSessionHandle, int, MongoException, CancellationToken, ValueTask>? unknown = null,
        Func<IClientSessionHandle, int, CancellationToken, ValueTask>? beforeCommitRetryDelay = null,
        Func<IClientSessionHandle, int, CancellationToken, ValueTask>? commitRetryDelayCompleted = null) =>
        new(
            body ?? ((_, _, _) => ValueTask.CompletedTask),
            commit ?? ((_, _, _) => ValueTask.CompletedTask),
            unknown ?? ((_, _, _, _) => ValueTask.CompletedTask),
            beforeCommitRetryDelay ?? ((_, _, _) => ValueTask.CompletedTask),
            commitRetryDelayCompleted ?? ((_, _, _) => ValueTask.CompletedTask))
        {
            CommitInvoked = commitInvoked ?? ((_, _, _) => ValueTask.CompletedTask)
        };

    private static async Task AssertTerminalAsync(IDocumentUnitOfWork transaction)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.SaveAsync(new SaveDocumentRequest(
            "workItem", "after-terminal", "1", """{"status":"open","rank":3}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.LoadAsync("workItem", "after-terminal"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
    }

    private static async Task ConfigureCommandFailureAsync(
        IMongoDatabase database,
        string command,
        BsonValue mode,
        int errorCode,
        IReadOnlyList<string> labels)
    {
        var data = new BsonDocument
        {
            { "failCommands", new BsonArray { command } },
            { "errorCode", errorCode },
            { "errorLabels", new BsonArray(labels) }
        };
        await database.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", mode },
            { "data", data }
        });
    }

    private static Task DisableCommandFailureAsync(IMongoDatabase database) =>
        database.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", "off" }
        });
}
