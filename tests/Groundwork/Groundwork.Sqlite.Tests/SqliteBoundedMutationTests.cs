using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteBoundedMutationTests
{
    [Fact]
    public void Reusable_relational_mutation_handler_is_not_a_public_provider_capability_surface()
    {
        Assert.False(typeof(RelationalPhysicalDocumentMutationHandler).IsPublic);
    }

    [Fact]
    public async Task Delete_is_bounded_exact_idempotent_and_rejects_operation_reuse()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("stale-a", "stale");
        await fixture.SaveAsync("stale-b", "stale");
        await fixture.SaveAsync("current", "current");
        var request = Delete("prune-1", "stale");

        var completed = await fixture.Mutations.ExecuteAsync(request);
        var replayed = await fixture.Mutations.ExecuteAsync(request);

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), completed);
        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Replayed, 2), replayed);
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "stale-a"));
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "stale-b"));
        Assert.NotNull(await fixture.Documents.LoadAsync(DocumentKind, "current"));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() =>
            fixture.Mutations.ExecuteAsync(Delete("prune-1", "current")));
    }

    [Fact]
    public async Task Transition_updates_canonical_document_and_linked_projection()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("pending-a", "pending");
        await fixture.SaveAsync("pending-b", "pending");
        await fixture.SaveAsync("active", "active");

        var result = await fixture.Mutations.ExecuteAsync(Transition("revoke-1"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Equal("revoked", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending-a"))!.ContentJson));
        Assert.Equal("revoked", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending-b"))!.ContentJson));
        Assert.Equal(2, await fixture.CountAsync("revoked"));
        Assert.Equal(0, await fixture.CountAsync("pending"));
        Assert.Equal(1, await fixture.CountAsync("active"));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task Physical_storage_forms_execute_exact_transition_and_delete_mutations(
        PhysicalStorageForm form)
    {
        await using var fixture = await CreateAsync(form: form);
        await fixture.SaveAsync("pending", "pending");
        await fixture.SaveAsync("stale", "stale");
        await fixture.SaveAsync("current", "current");

        var transitioned = await fixture.Mutations.ExecuteAsync(Transition($"{form}-transition"));
        var deleted = await fixture.Mutations.ExecuteAsync(Delete($"{form}-delete", "stale"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), transitioned);
        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), deleted);
        Assert.Equal("revoked", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "stale"));
        Assert.Equal("current", Category((await fixture.Documents.LoadAsync(DocumentKind, "current"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("revoked"));
        Assert.Equal(0, await fixture.CountAsync("pending"));
        Assert.Equal(0, await fixture.CountAsync("stale"));
        Assert.Equal(1, await fixture.CountAsync("current"));
    }

    [Fact]
    public async Task Compound_relationship_and_range_predicates_are_applied_server_side()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("expired-a", "authorization-a", 1);
        await fixture.SaveAsync("expired-b", "authorization-a", 9);
        await fixture.SaveAsync("future", "authorization-a", 10);
        await fixture.SaveAsync("other-authorization", "authorization-b", 1);

        var result = await fixture.Mutations.ExecuteAsync(new DocumentMutation(
            DocumentKind,
            "prune-by-category-cutoff",
            "range-1",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "authorization-a")),
                DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
            ]));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 2), result);
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "expired-a"));
        Assert.Null(await fixture.Documents.LoadAsync(DocumentKind, "expired-b"));
        Assert.NotNull(await fixture.Documents.LoadAsync(DocumentKind, "future"));
        Assert.NotNull(await fixture.Documents.LoadAsync(DocumentKind, "other-authorization"));
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_document_projection_and_ledger()
    {
        await using var fixture = await CreateAsync(point => point == RelationalPhysicalMutationExecutionPoint.BeforeCommit
            ? ValueTask.FromException(new SimulatedMutationFailureException())
            : ValueTask.CompletedTask);
        await fixture.SaveAsync("pending", "pending");

        await Assert.ThrowsAsync<SimulatedMutationFailureException>(() =>
            fixture.Mutations.ExecuteAsync(Transition("rollback-1")));

        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("pending"));
        Assert.Equal(0, await fixture.CountAsync("revoked"));
        var restarted = fixture.CreateMutationRuntime();
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await restarted.ExecuteAsync(Transition("rollback-1")));
    }

    [Fact]
    public async Task Rollback_and_disposal_failures_do_not_replace_the_primary_mutation_failure()
    {
        var transactionFaults = new MutationTransactionFaults();
        await using var fixture = await CreateAsync(point => point switch
        {
            RelationalPhysicalMutationExecutionPoint.BeforeCommit =>
                ValueTask.FromException(new SimulatedMutationFailureException()),
            _ => ValueTask.CompletedTask
        }, transaction => new FaultingMutationTransaction(transaction, transactionFaults));
        await fixture.SaveAsync("pending", "pending");

        var exception = await Assert.ThrowsAsync<SimulatedMutationFailureException>(() =>
            fixture.Mutations.ExecuteAsync(Transition("rollback-failure-1")));

        var cleanupFailures = Assert.IsType<List<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(
            cleanupFailures,
            failure => Assert.IsType<SimulatedRollbackFailureException>(failure),
            failure => Assert.IsType<SimulatedMutationTransactionDisposalFailureException>(failure));
        Assert.Equal(1, transactionFaults.RollbackCallCount);
        Assert.Equal(1, transactionFaults.DisposeCallCount);
        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("pending"));
        Assert.Equal(0, await fixture.CountAsync("revoked"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(Transition("rollback-failure-1")));
    }

    [Fact]
    public async Task Rollback_and_disposal_failures_do_not_replace_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var transactionFaults = new MutationTransactionFaults();
        await using var fixture = await CreateAsync(point => point switch
        {
            RelationalPhysicalMutationExecutionPoint.BeforeCommit => CancelMutation(cancellation),
            _ => ValueTask.CompletedTask
        }, transaction => new FaultingMutationTransaction(transaction, transactionFaults));
        await fixture.SaveAsync("pending", "pending");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Mutations.ExecuteAsync(Transition("rollback-cancellation-1")));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var cleanupFailures = Assert.IsType<List<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(
            cleanupFailures,
            failure => Assert.IsType<SimulatedRollbackFailureException>(failure),
            failure => Assert.IsType<SimulatedMutationTransactionDisposalFailureException>(failure));
        Assert.Equal(1, transactionFaults.RollbackCallCount);
        Assert.Equal(1, transactionFaults.DisposeCallCount);
        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("pending"));
        Assert.Equal(0, await fixture.CountAsync("revoked"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(Transition("rollback-cancellation-1")));
    }

    [Fact]
    public async Task Delete_failure_before_commit_restores_primary_linked_and_ledger_state()
    {
        await using var fixture = await CreateAsync(point => point == RelationalPhysicalMutationExecutionPoint.BeforeCommit
            ? ValueTask.FromException(new SimulatedMutationFailureException())
            : ValueTask.CompletedTask);
        await fixture.SaveAsync("stale", "stale");
        var request = Delete("delete-rollback-1", "stale");

        await Assert.ThrowsAsync<SimulatedMutationFailureException>(() =>
            fixture.Mutations.ExecuteAsync(request));

        Assert.NotNull(await fixture.Documents.LoadAsync(DocumentKind, "stale"));
        Assert.Equal(1, await fixture.CountAsync("stale"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Cancellation_before_commit_rolls_back_document_projection_and_ledger()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await CreateAsync(point =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.BeforeCommit)
                return ValueTask.CompletedTask;
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        });
        await fixture.SaveAsync("pending", "pending");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Mutations.ExecuteAsync(Transition("cancel-1")));

        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("pending"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(Transition("cancel-1")));
    }

    [Fact]
    public async Task Retry_after_acknowledgement_loss_returns_original_exact_outcome()
    {
        var loseAcknowledgement = true;
        await using var fixture = await CreateAsync(point =>
        {
            if (point == RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement && loseAcknowledgement)
            {
                loseAcknowledgement = false;
                return ValueTask.FromException(new SimulatedMutationAcknowledgementLossException());
            }
            return ValueTask.CompletedTask;
        });
        await fixture.SaveAsync("pending-a", "pending");
        await fixture.SaveAsync("pending-b", "pending");
        var request = Transition("ack-loss-1");

        await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() =>
            fixture.Mutations.ExecuteAsync(request));

        Assert.Equal(2, await fixture.CountAsync("revoked"));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 2),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Restart_after_acknowledgement_loss_replays_the_durable_outcome()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-mutation-{Guid.NewGuid():N}.db");
        try
        {
            var (manifest, target) = CreateModel();
            var route = target.Routes.Single();
            var request = Transition("restart-ack-loss-1");
            await using (var firstConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await firstConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(
                    target,
                    new SqlitePhysicalSchemaExecutor(firstConnection));
                var firstStore = new SqlitePhysicalDocumentStore(
                    firstConnection,
                    manifest,
                    target.Routes,
                    DocumentStoreAccess.Global);
                await SaveAsync(firstStore, "pending", "pending", 1);
                var mutations = SqlitePhysicalMutationRuntime.Create(
                    firstStore,
                    manifest,
                    route,
                    target.Provider,
                    point => point == RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement
                        ? ValueTask.FromException(new SimulatedMutationAcknowledgementLossException())
                        : ValueTask.CompletedTask);

                await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() =>
                    mutations.ExecuteAsync(request));
            }

            await using var restartedConnection = new SqliteConnection($"Data Source={databasePath}");
            await restartedConnection.OpenAsync();
            var restartedStore = new SqlitePhysicalDocumentStore(
                restartedConnection,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);

            var replay = await SqlitePhysicalMutationRuntime.Create(
                    restartedStore,
                    manifest,
                    route,
                    target.Provider)
                .ExecuteAsync(request);

            Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Replayed, 1), replay);
            Assert.Equal("revoked", Category((await restartedStore.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Provider_upgrade_after_acknowledgement_loss_replays_the_durable_outcome()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-mutation-{Guid.NewGuid():N}.db");
        try
        {
            var (manifest, target) = CreateModel();
            var route = target.Routes.Single();
            var request = Transition("rolling-upgrade-ack-loss-1");
            var firstProvider = new ProviderIdentity(target.Provider.Name, "1.0.0");
            var upgradedProvider = new ProviderIdentity(target.Provider.Name, "2.0.0");
            await using (var firstConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await firstConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(
                    target,
                    new SqlitePhysicalSchemaExecutor(firstConnection));
                var firstStore = new SqlitePhysicalDocumentStore(
                    firstConnection,
                    manifest,
                    target.Routes,
                    DocumentStoreAccess.Global);
                await SaveAsync(firstStore, "pending", "pending", 1);
                var mutations = SqlitePhysicalMutationRuntime.Create(
                    firstStore,
                    manifest,
                    route,
                    firstProvider,
                    point => point == RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement
                        ? ValueTask.FromException(new SimulatedMutationAcknowledgementLossException())
                        : ValueTask.CompletedTask);

                await Assert.ThrowsAsync<SimulatedMutationAcknowledgementLossException>(() =>
                    mutations.ExecuteAsync(request));
            }

            await using var restartedConnection = new SqliteConnection($"Data Source={databasePath}");
            await restartedConnection.OpenAsync();
            var restartedStore = new SqlitePhysicalDocumentStore(
                restartedConnection,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);

            var replay = await SqlitePhysicalMutationRuntime.Create(
                    restartedStore,
                    manifest,
                    route,
                    upgradedProvider)
                .ExecuteAsync(request);

            Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Replayed, 1), replay);
            Assert.Equal("revoked", Category((await restartedStore.LoadAsync(DocumentKind, "pending"))!.ContentJson));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Concurrent_retry_of_one_operation_returns_one_result_and_one_exact_replay()
    {
        await using var fixture = await CreateAsync();
        for (var index = 0; index < 10; index++)
            await fixture.SaveAsync($"pending-{index}", "pending");
        var request = Transition("concurrent-1");

        var results = await Task.WhenAll(
            fixture.Mutations.ExecuteAsync(request),
            fixture.Mutations.ExecuteAsync(request));

        Assert.Equal(
            [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
            results.Select(result => result.Status).Order().ToArray());
        Assert.All(results, result => Assert.Equal(10, result.AffectedCount));
        Assert.Equal(10, await fixture.CountAsync("revoked"));
    }

    [Fact]
    public async Task Concurrent_retry_across_independent_file_sessions_is_serialized_by_the_writer_boundary()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-mutation-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var (manifest, target) = CreateModel();
            await using (var materializationConnection = new SqliteConnection(connectionString))
            {
                await materializationConnection.OpenAsync();
                await PhysicalSchemaApplication.ApplyAsync(
                    target,
                    new SqlitePhysicalSchemaExecutor(materializationConnection));
            }
            var route = target.Routes.Single();
            var firstStore = new SqlitePhysicalDocumentStore(
                connectionString,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);
            var secondStore = new SqlitePhysicalDocumentStore(
                connectionString,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);
            for (var index = 0; index < 5; index++)
                await SaveAsync(firstStore, $"pending-{index}", "pending", 1);
            var request = Transition("cross-session-concurrent-1");

            var results = await Task.WhenAll(
                SqlitePhysicalMutationRuntime.Create(firstStore, manifest, route, target.Provider)
                    .ExecuteAsync(request),
                SqlitePhysicalMutationRuntime.Create(secondStore, manifest, route, target.Provider)
                    .ExecuteAsync(request));

            Assert.Equal(
                [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
                results.Select(result => result.Status).Order().ToArray());
            Assert.All(results, result => Assert.Equal(5, result.AffectedCount));
            var query = SqlitePhysicalQueryRuntime.Create(firstStore, manifest, route, target.Provider);
            Assert.Equal(5, await query.CountAsync(new DocumentQuery(
                DocumentKind,
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "revoked"))],
                resultOperation: BoundedQueryResultOperation.Count)));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Concurrent_retry_across_direct_file_connections_is_serialized_by_the_writer_boundary()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-mutation-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var (manifest, target) = CreateModel();
            await using var firstConnection = new SqliteConnection(connectionString);
            await using var secondConnection = new SqliteConnection(connectionString);
            await firstConnection.OpenAsync();
            await secondConnection.OpenAsync();
            await PhysicalSchemaApplication.ApplyAsync(
                target,
                new SqlitePhysicalSchemaExecutor(firstConnection));
            var route = target.Routes.Single();
            var firstStore = new SqlitePhysicalDocumentStore(
                firstConnection,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);
            var secondStore = new SqlitePhysicalDocumentStore(
                secondConnection,
                manifest,
                target.Routes,
                DocumentStoreAccess.Global);
            for (var index = 0; index < 5; index++)
                await SaveAsync(firstStore, $"pending-{index}", "pending", 1);
            var request = Transition("direct-connection-concurrent-1");

            var results = await Task.WhenAll(
                SqlitePhysicalMutationRuntime.Create(firstStore, manifest, route, target.Provider)
                    .ExecuteAsync(request),
                SqlitePhysicalMutationRuntime.Create(secondStore, manifest, route, target.Provider)
                    .ExecuteAsync(request));

            Assert.Equal(
                [BoundedMutationStatus.Completed, BoundedMutationStatus.Replayed],
                results.Select(result => result.Status).Order().ToArray());
            Assert.All(results, result => Assert.Equal(5, result.AffectedCount));
            var query = SqlitePhysicalQueryRuntime.Create(firstStore, manifest, route, target.Provider);
            Assert.Equal(5, await query.CountAsync(new DocumentQuery(
                DocumentKind,
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "revoked"))],
                resultOperation: BoundedQueryResultOperation.Count)));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Mutation_scope_is_inherited_from_the_store_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var (manifest, target) = CreateModel(scoped: true);
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var tenantA = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-a")));
        var tenantB = new SqlitePhysicalDocumentStore(
            connection,
            manifest,
            target.Routes,
            DocumentStoreAccess.Scoped(new StorageScope("tenant-b")));
        await SaveAsync(tenantA, "same-id", "stale", 1);
        await SaveAsync(tenantB, "same-id", "stale", 1);

        var result = await SqlitePhysicalMutationRuntime.Create(tenantA, manifest, route, target.Provider)
            .ExecuteAsync(Delete("tenant-a-prune", "stale"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        Assert.Null(await tenantA.LoadAsync(DocumentKind, "same-id"));
        Assert.NotNull(await tenantB.LoadAsync(DocumentKind, "same-id"));
    }

    [Fact]
    public async Task Mutation_selector_uses_declared_physical_index()
    {
        await using var fixture = await CreateAsync();

        var explanation = await fixture.ExplainDeleteAsync("stale");

        var expectedIndex = fixture.Route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier;
        Assert.True(explanation.Contains(expectedIndex, StringComparison.Ordinal), explanation);
        Assert.DoesNotContain("SCAN configuration_projection", explanation, StringComparison.OrdinalIgnoreCase);
    }

    private const string DocumentKind = "configurationDocument";

    private static DocumentMutation Delete(string operationId, string category) =>
        new(DocumentKind, "prune-by-category", operationId,
        [
            DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))
        ]);

    private static DocumentMutation Transition(string operationId) =>
        new(DocumentKind, "revoke-pending", operationId);

    private static string Category(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("category").GetString()!;

    private static ValueTask CancelMutation(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return ValueTask.FromCanceled(cancellation.Token);
    }

    private static async Task<Fixture> CreateAsync(
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept = null,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction>? mutationTransactionFactory = null,
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var (manifest, target) = CreateModel(form: form);
            await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
            var route = target.Routes.Single();
            var documents = mutationTransactionFactory is null
                ? new SqlitePhysicalDocumentStore(
                    connection,
                    manifest,
                    target.Routes,
                    DocumentStoreAccess.Global)
                : new SqlitePhysicalDocumentStore(
                    connection,
                    manifest,
                    target.Routes,
                    DocumentStoreAccess.Global,
                    mutationTransactionFactory);
            return new Fixture(
                connection,
                manifest,
                target,
                route,
                documents,
                SqlitePhysicalMutationRuntime.Create(documents, manifest, route, target.Provider, intercept));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) CreateModel(
        bool scoped = false,
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable)
    {
        var (template, _) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            scoped: scoped);
        var unit = template.StorageUnits.Single();
        var storage = unit.PhysicalStorage!;
        var compound = storage.LogicalIndexes.Single(index => index.Identity == "by-category-priority");
        var cutoffQuery = new BoundedQueryDeclaration(
            "prune-by-category-cutoff",
            compound.Identity,
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
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField(
                    "priority",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.LessThan })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation> { BoundedQueryResultOperation.Count });
        var manifest = template with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries.Append(cutoffQuery).ToArray(),
                        storage.NameOverrides,
                        [
                            new BoundedMutationDeclaration(
                                "prune-by-category",
                                "list-by-category",
                                BoundedMutationAction.Delete()),
                            new BoundedMutationDeclaration(
                                "revoke-pending",
                                "list-by-category",
                                BoundedMutationAction.Transition("category", ["pending"], "revoked")),
                            new BoundedMutationDeclaration(
                                "prune-by-category-cutoff",
                                "prune-by-category-cutoff",
                                BoundedMutationAction.Delete())
                        ])
                }
            ]
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }

    private sealed class Fixture(
        SqliteConnection connection,
        Groundwork.Core.Manifests.StorageManifest manifest,
        PhysicalSchemaTarget target,
        ExecutableStorageRoute route,
        SqlitePhysicalDocumentStore documents,
        IBoundedDocumentMutationStore mutations) : IAsyncDisposable
    {
        public ExecutableStorageRoute Route { get; } = route;
        public SqlitePhysicalDocumentStore Documents { get; } = documents;
        public IBoundedDocumentMutationStore Mutations { get; } = mutations;

        public Task SaveAsync(string id, string category, int priority = 1) =>
            SqliteBoundedMutationTests.SaveAsync(Documents, id, category, priority);

        public IBoundedDocumentMutationStore CreateMutationRuntime() =>
            SqlitePhysicalMutationRuntime.Create(Documents, manifest, Route, target.Provider);

        public async Task<long> CountAsync(string category)
        {
            var query = SqlitePhysicalQueryRuntime.Create(Documents, manifest, Route, target.Provider);
            return await query.CountAsync(new DocumentQuery(
                DocumentKind,
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))],
                resultOperation: BoundedQueryResultOperation.Count));
        }

        public async Task<string> ExplainDeleteAsync(string category) =>
            await SqlitePhysicalMutationRuntime.ExplainAsync(
                connection,
                Documents,
                manifest,
                Route,
                target.Provider,
                Delete("explain", category));

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class SimulatedMutationFailureException : Exception;

    private sealed class SimulatedMutationAcknowledgementLossException : Exception;

    private sealed class SimulatedRollbackFailureException : Exception;

    private sealed class SimulatedMutationTransactionDisposalFailureException : Exception;

    private sealed class MutationTransactionFaults
    {
        public int RollbackCallCount { get; set; }
        public int DisposeCallCount { get; set; }
    }

    private sealed class FaultingMutationTransaction(
        DbTransaction transaction,
        MutationTransactionFaults faults) : IRelationalPhysicalMutationTransaction
    {
        private bool rollbackAttempted;

        public DbTransaction Transaction => transaction;

        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            rollbackAttempted = true;
            faults.RollbackCallCount++;
            await transaction.RollbackAsync(cancellationToken);
            throw new SimulatedRollbackFailureException();
        }

        public async ValueTask DisposeAsync()
        {
            faults.DisposeCallCount++;
            await transaction.DisposeAsync();
            if (rollbackAttempted)
                throw new SimulatedMutationTransactionDisposalFailureException();
        }
    }

    private static async Task SaveAsync(
        SqlitePhysicalDocumentStore documents,
        string id,
        string category,
        int priority)
    {
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await documents.SaveAsync(new SaveDocumentRequest(
            DocumentKind,
            id,
            "1",
            $$"""{"category":"{{category}}","priority":{{priority}}}""",
            ExpectedVersion: 0))).Status);
    }
}
