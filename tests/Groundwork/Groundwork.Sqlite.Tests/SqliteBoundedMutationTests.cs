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
    public async Task Public_runtime_explains_the_admitted_mutation_with_observed_native_index_evidence()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("evidence-target", "stale");
        var request = Delete("native-evidence", "stale");
        var mutations = Assert.IsAssignableFrom<IPhysicalDocumentMutationExplainer>(fixture.Mutations);

        var plan = mutations.ResolvePlan(request);
        var evidence = await mutations.ExplainAsync(request);

        Assert.Equal(plan, evidence.Plan);
        Assert.Equal(
            [
                PhysicalDocumentMutationCommandIdentities.CandidateDiscovery,
                PhysicalDocumentMutationCommandIdentities.PredicateRecheck
            ],
            evidence.Commands.Select(command => command.Identity));
        Assert.All(evidence.Commands, command => Assert.Equal("sqlite-query-plan", command.NativePlanFormat));
        var selectors = evidence.Commands
            .SelectMany(command => command.Selectors)
            .DistinctBy(selector => selector.Target)
            .ToArray();
        Assert.Equal(2, selectors.Length);
        var primary = selectors.Single(selector =>
            selector.Target == ExecutableStorageObjectRole.PrimaryStorage);
        var linked = selectors.Single(selector =>
            selector.Target == ExecutableStorageObjectRole.LinkedIndexStorage);
        Assert.Equal(plan.Predicate.PrimaryObject, primary.StorageObject);
        Assert.Equal(plan.Predicate.LookupObject, linked.StorageObject);
        Assert.Null(primary.Index);
        Assert.Equal(plan.Predicate.IndexName, linked.Index);
        Assert.Equal(plan.Predicate.IndexName!.Identifier, linked.ObservedIndexIdentifier);
        Assert.All(
            evidence.Commands,
            command => Assert.Contains(linked.Index!.Identifier, command.NativePlan, StringComparison.Ordinal));

        var executed = new List<(string Identity, string CommandText)>();
        var executionRuntime = fixture.CreateObservedMutationRuntime((identity, command) =>
        {
            executed.Add((identity, command.CommandText));
            return ValueTask.CompletedTask;
        });
        Assert.Equal(BoundedMutationStatus.Completed, (await executionRuntime.ExecuteAsync(request)).Status);
        Assert.Equal(
            evidence.Commands.Select(command => (command.Identity, command.RenderedCommand!)),
            executed);
    }

    [Theory]
    [InlineData("wrong-target")]
    [InlineData("wrong-storage")]
    [InlineData("wrong-index")]
    [InlineData("missing-primary")]
    [InlineData("scan-linked")]
    [InlineData("scan-primary")]
    public void Native_plan_inspector_fails_closed_on_target_or_index_drift(string drift)
    {
        var (manifest, target) = CreateModel();
        var route = target.Routes.Single();
        var storage = manifest.StorageUnits.Single().PhysicalStorage!;
        var plan = PhysicalMutationPlanCompiler.Compile(
                route,
                storage,
                SqlitePhysicalQueryRuntime.Capabilities(target.Provider))
            .Plans.Single(candidate => candidate.MutationIdentity == "prune-by-category");
        var primaryTarget = drift == "wrong-target" ? "x" : "p";
        var linkedIndex = drift == "wrong-index" ? "wrong_index" : plan.Predicate.IndexName!.Identifier;
        var primaryOperation = drift == "scan-primary" ? "SCAN" : "SEARCH";
        var linkedOperation = drift == "scan-linked" ? "SCAN" : "SEARCH";
        var primary = drift == "missing-primary"
            ? string.Empty
            : $"{primaryOperation} {primaryTarget} USING INDEX primary_identity_index (storage_scope=?)";
        var content = string.Join(
            Environment.NewLine,
            new[]
            {
                $"{linkedOperation} l USING INDEX {linkedIndex} (category=?)",
                primary
            }.Where(line => line.Length != 0));
        var primaryStorage = drift == "wrong-storage"
            ? "wrong_primary"
            : route.PrimaryStorage.Name.Identifier;
        var rendered =
            $"SELECT * FROM \"{route.LinkedIndexStorage!.Name.Identifier}\" AS l " +
            $"JOIN \"{primaryStorage}\" p ON 1 = 1;";

        Assert.Throws<InvalidOperationException>(() =>
            SqliteNativeMutationPlanInspector.Inspect(
                rendered,
                new RelationalPhysicalNativeQueryPlan("sqlite-query-plan", content),
                plan,
                route));
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

    [Fact]
    public async Task Linked_mutation_hydrates_primary_through_Unicode_equivalent_identity_evidence()
    {
        await using var fixture = await CreateAsync(
            stringCasePolicy: Groundwork.Core.Manifests.StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        await fixture.SaveAsync("Configuration-One", "pending");
        await fixture.ChangeLinkedIdentityEvidenceAsync(originalId: "configuration-one");

        var result = await fixture.Mutations.ExecuteAsync(Transition("unicode-equivalent-identity"));

        Assert.Equal(new BoundedMutationResult(BoundedMutationStatus.Completed, 1), result);
        var retained = await fixture.Documents.LoadAsync(DocumentKind, "Configuration-One");
        Assert.Equal("revoked", Category(retained!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("revoked"));
    }

    [Fact]
    public async Task Linked_mutation_rejects_lookup_collision_evidence_and_rolls_back_operation()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("Primary-Id", "pending");
        await fixture.ChangeLinkedIdentityEvidenceAsync(
            originalId: "Collision-Id",
            comparisonKey: "different-comparison");

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            fixture.Mutations.ExecuteAsync(Transition("lookup-collision")));

        Assert.Equal(DocumentKind, exception.DocumentKind);
        Assert.Equal("Collision-Id", exception.RequestedId);
        Assert.Equal("Primary-Id", exception.RetainedId);
        Assert.Equal(fixture.Route.Envelope.Identity.Project("Primary-Id").LookupKey, exception.LookupKey);
        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "Primary-Id"))!.ContentJson));
        Assert.Equal(0, await fixture.CountMutationOperationsAsync());
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, 2)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, 2)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, 1)]
    public async Task Large_selection_uses_a_constant_number_of_set_based_lock_commands(
        PhysicalStorageForm form,
        int expectedLockCommands)
    {
        var lockCommands = 0;
        await using var fixture = await CreateAsync(
            point =>
            {
                if (point == RelationalPhysicalMutationExecutionPoint.BeforeRowLockCommand)
                    Interlocked.Increment(ref lockCommands);
                return ValueTask.CompletedTask;
            },
            form: form);
        for (var index = 0; index < 128; index++)
            await fixture.SaveAsync($"pending-{index}", "pending");

        var result = await fixture.Mutations.ExecuteAsync(Transition($"{form}-large-selection"));

        Assert.Equal(128, result.AffectedCount);
        Assert.Equal(expectedLockCommands, lockCommands);
        Assert.Equal(128, await fixture.CountAsync("revoked"));
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
    public async Task Primary_affected_count_mismatch_rolls_back_without_ledger_evidence()
    {
        await using var fixture = await CreateAsync(form: PhysicalStorageForm.PhysicalEntityTable);
        await fixture.SaveAsync("primary-count-mismatch", "pending");
        var mutations = fixture.CreateMutationRuntime(async (point, connection, transaction, cancellationToken) =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                return;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"UPDATE {Q(fixture.Route.PrimaryStorage.Name.Identifier)} SET " +
                $"{Q(fixture.Route.Envelope.Version.Identifier)} = {Q(fixture.Route.Envelope.Version.Identifier)} + 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
        var request = Transition("primary-count-mismatch");

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(request));

        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "primary-count-mismatch"))!.ContentJson));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await fixture.CreateMutationRuntime().ExecuteAsync(request));
    }

    [Fact]
    public async Task Required_linked_affected_count_mismatch_rolls_back_without_ledger_evidence()
    {
        await using var fixture = await CreateAsync();
        await fixture.SaveAsync("linked-count-mismatch", "pending");
        var mutations = fixture.CreateMutationRuntime(async (point, connection, transaction, cancellationToken) =>
        {
            if (point != RelationalPhysicalMutationExecutionPoint.AfterSelection)
                return;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {Q(fixture.Route.LinkedIndexStorage!.Name.Identifier)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        });
        var request = Transition("linked-count-mismatch");

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(request));

        Assert.Equal("pending", Category((await fixture.Documents.LoadAsync(DocumentKind, "linked-count-mismatch"))!.ContentJson));
        Assert.Equal(1, await fixture.CountAsync("pending"));
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

    private static string Q(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static ValueTask CancelMutation(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return ValueTask.FromCanceled(cancellation.Token);
    }

    private static async Task<Fixture> CreateAsync(
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept = null,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction>? mutationTransactionFactory = null,
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable,
        Groundwork.Core.Manifests.StringIdentityCasePolicy stringCasePolicy =
            Groundwork.Core.Manifests.StringIdentityCasePolicy.Ordinal)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            var (manifest, target) = CreateModel(form: form, stringCasePolicy: stringCasePolicy);
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
        PhysicalStorageForm form = PhysicalStorageForm.DedicatedDocumentTable,
        Groundwork.Core.Manifests.StringIdentityCasePolicy stringCasePolicy =
            Groundwork.Core.Manifests.StringIdentityCasePolicy.Ordinal)
    {
        var (template, _) = SqlitePhysicalSchemaExecutorTests.CreateModel(
            form,
            includePriority: true,
            scoped: scoped,
            stringCasePolicy: stringCasePolicy);
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

        public async Task ChangeLinkedIdentityEvidenceAsync(
            string originalId,
            string? comparisonKey = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE {Q(Route.LinkedIndexStorage!.Name.Identifier)} SET " +
                $"{Q(Route.LinkedRelationship!.DocumentId.Identifier)} = @originalId" +
                (comparisonKey is null
                    ? string.Empty
                    : $", {Q(Route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey") +
                ";";
            command.Parameters.AddWithValue("@originalId", originalId);
            if (comparisonKey is not null)
                command.Parameters.AddWithValue("@comparisonKey", comparisonKey);
            await command.ExecuteNonQueryAsync();
        }

        public IBoundedDocumentMutationStore CreateMutationRuntime() =>
            SqlitePhysicalMutationRuntime.Create(Documents, manifest, Route, target.Provider);

        public IBoundedDocumentMutationStore CreateMutationRuntime(RelationalPhysicalMutationInterceptor intercept) =>
            RelationalPhysicalMutationRuntime.CreateWithInterceptor(
                new RelationalPhysicalMutationRuntimeContext(
                    Documents,
                    manifest,
                    Route,
                    target.Provider,
                    target.Provider.Name,
                    "sqlite"),
                intercept);

        public IBoundedDocumentMutationStore CreateObservedMutationRuntime(
            RelationalPhysicalMutationSelectionObserver observer) =>
            RelationalPhysicalMutationRuntime.CreateWithSelectionObserver(
                new RelationalPhysicalMutationRuntimeContext(
                    Documents,
                    manifest,
                    Route,
                    target.Provider,
                    target.Provider.Name,
                    "sqlite"),
                observer);

        public async Task<long> CountAsync(string category)
        {
            var query = SqlitePhysicalQueryRuntime.Create(Documents, manifest, Route, target.Provider);
            return await query.CountAsync(new DocumentQuery(
                DocumentKind,
                "list-by-category",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", category))],
                resultOperation: BoundedQueryResultOperation.Count));
        }

        public async Task<long> CountMutationOperationsAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM {Q(RelationalPhysicalStorageColumns.MutationOperationsTable)};";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
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
