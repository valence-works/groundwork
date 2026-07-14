using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.TestInfrastructure;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Testcontainers.PostgreSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class PostgreSqlPhysicalStorageContainer : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17.6-alpine3.22")
        .WithDatabase("groundwork")
        .WithUsername("groundwork")
        .WithPassword("groundwork")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class PostgreSqlRelationalPhysicalStorageConformanceTests(
    PostgreSqlPhysicalStorageContainer fixture)
    : RelationalServerPhysicalIdentityConformance, IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    private readonly PostgreSqlContainer container = fixture.Container;

    [Fact]
    public void PrimaryInsertConflictTargetsOnlyTheCompiledIdentityKey()
    {
        var sql = new PostgreSqlPhysicalDocumentDialect().InsertPrimaryIfAbsent(
            "documents",
            ["kind", "scope", "lookup"],
            ["@kind", "@scope", "@lookup"],
            ["kind", "scope", "lookup"],
            []);

        Assert.Contains(
            "ON CONFLICT (\"kind\", \"scope\", \"lookup\") DO NOTHING",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public Task Bounded_transition_updates_the_exact_indexed_identity_set() =>
        RelationalBoundedMutationServerAssertions.TransitionUpdatesExactIndexedIdentitySetAsync(MutationHarness());

    [Fact]
    public Task Concurrent_bounded_retry_completes_once_and_replays_the_exact_count() =>
        RelationalBoundedMutationServerAssertions.ConcurrentRetryReplaysExactResultAsync(MutationHarness());

    [Fact]
    public Task Concurrent_distinct_transitions_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.ConcurrentDistinctTransitionsSerializeSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Direct_connection_mutations_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.DirectConnectionDistinctTransitionSerializesSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Concurrent_distinct_deletes_serialize_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.ConcurrentDistinctDeletesSerializeSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Ordinary_save_and_delete_serialize_with_the_selected_set() =>
        RelationalBoundedMutationServerAssertions.OrdinaryCrudSerializesWithSelectedSetAsync(MutationHarness());

    [Fact]
    public Task Linked_ordinary_crud_interleavings_serialize_in_pooled_and_direct_sessions() =>
        RelationalBoundedMutationServerAssertions.LinkedOrdinaryCrudInterleavingsSerializeAsync(MutationHarness());

    [Fact]
    public Task Large_selection_uses_constant_set_based_lock_commands() =>
        RelationalBoundedMutationServerAssertions.LargeSelectionUsesConstantSetBasedLockCommandsAsync(MutationHarness());

    [Fact]
    public Task Bounded_transition_and_range_delete_cover_all_relational_storage_forms() =>
        RelationalBoundedMutationServerAssertions.PhysicalFormsExecuteTransitionAndRangeDeleteAsync(MutationHarness());

    [Fact]
    public Task Bounded_typed_transitions_preserve_canonical_and_projected_values() =>
        RelationalBoundedMutationServerAssertions.TypedTransitionsPreserveCanonicalAndProjectedValuesAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_scope_is_inherited_from_the_store_session() =>
        RelationalBoundedMutationServerAssertions.MutationScopeIsInheritedFromStoreSessionAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_failure_before_commit_rolls_back_and_can_retry() =>
        RelationalBoundedMutationServerAssertions.FailureBeforeCommitRollsBackAndRetryCompletesAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_cancellation_rolls_back_and_preserves_the_token() =>
        RelationalBoundedMutationServerAssertions.CancellationBeforeCommitRollsBackAndPreservesTokenAsync(MutationHarness());

    [Fact]
    public Task Bounded_mutation_acknowledgement_loss_restarts_and_replays_across_provider_upgrade() =>
        RelationalBoundedMutationServerAssertions.AcknowledgementLossRestartAndProviderUpgradeReplayAsync(MutationHarness());

    [Fact]
    public async Task Bounded_mutation_ledger_supports_unbounded_operation_identity()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true, IncludeRangeDelete: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "long-operation", "1", "{\"category\":\"pending\",\"priority\":1}"))).Status);
        var operationId = string.Concat(Enumerable.Range(0, 100).Select(index =>
            Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(index)))));
        Assert.Equal(6400, Encoding.UTF8.GetByteCount(operationId));
        var request = new DocumentMutation("configurationDocument", "revoke-pending", operationId);
        var mutations = PostgreSqlPhysicalMutationRuntime.Create(store, model.Manifest, route, model.Target.Provider);

        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(request));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 1),
            await mutations.ExecuteAsync(request));
        await Assert.ThrowsAsync<BoundedMutationOperationConflictException>(() => mutations.ExecuteAsync(
            new DocumentMutation(
                "configurationDocument",
                "prune-by-category-cutoff",
                operationId,
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "pending")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("priority", "10"))
                ])));
    }

    [Fact]
    public async Task Bounded_mutation_selector_uses_the_declared_physical_index()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            mutationOptions: new(IncludeCategoryTransition: true));
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "plan-target", "1", "{\"category\":\"pending\"}"))).Status);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "plan-noise", "1", "{\"category\":\"tools\"}"))).Status);
        await SeedPlanNoiseAsync(route);
        await AnalyzeRouteAsync(route);
        var mutationContext = new RelationalPhysicalMutationRuntimeContext(
            store,
            model.Manifest,
            route,
            model.Target.Provider,
            PostgreSqlGroundworkCapabilities.Provider.Name,
            "postgresql");
        var selection = RelationalPhysicalMutationRuntime.BuildSelectionCommand(
            mutationContext,
            new DocumentMutation("configurationDocument", "revoke-pending", "explain"));

        var plan = await ExplainAsync(selection);

        var expectedIndex = route.Indexes.Single(index => index.Identity == "by-category").Name.Identifier;
        Assert.True(plan.Contains(expectedIndex, StringComparison.Ordinal), plan);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
    }

    [Fact]
    public Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafe()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 1 }.ConnectionString;
        return RelationalPhysicalServerAssertions.ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(connectionString));
    }

    [Fact]
    public Task Unpublished_backfill_acknowledgement_loss_replays_interleaved_writes() =>
        RelationalPhysicalServerAssertions.UnpublishedBackfillAcknowledgementLossReplaysInterleavedWritesAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(),
                manifest,
                routes,
                DocumentStoreAccess.Global),
            "postgresql");

    [Fact]
    public async Task Concurrent_distinct_targets_can_bootstrap_a_clean_schema()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"groundwork_bootstrap_{suffix}";
        await ExecuteAdminAsync($"CREATE SCHEMA {Q(schema)};");
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = schema
        }.ConnectionString;
        try
        {
            var first = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"first-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var second = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.DedicatedDocumentTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"second-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var results = await Task.WhenAll(
                PhysicalSchemaApplication.ApplyAsync(first.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString)),
                PhysicalSchemaApplication.ApplyAsync(second.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString)));
            Assert.All(results, result => Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP SCHEMA {Q(schema)} CASCADE;");
        }
    }

    [Theory]
    [InlineData(InfrastructureTamper.WrongObjectKind)]
    [InlineData(InfrastructureTamper.ExtraOperationsColumn)]
    [InlineData(InfrastructureTamper.MissingStateColumn)]
    [InlineData(InfrastructureTamper.NullableLockOwner)]
    [InlineData(InfrastructureTamper.WrongLockOwnerType)]
    [InlineData(InfrastructureTamper.WrongStateCollation)]
    [InlineData(InfrastructureTamper.SameNameCShadowCollation)]
    [InlineData(InfrastructureTamper.MissingStatePrimaryKey)]
    [InlineData(InfrastructureTamper.ReorderedOperationsPrimaryKey)]
    [InlineData(InfrastructureTamper.LegacyMutationLedgerPrimaryKey)]
    [InlineData(InfrastructureTamper.WrongMutationLedgerHashExpression)]
    [InlineData(InfrastructureTamper.WrongMutationHashFunction)]
    public async Task Restart_rejects_malformed_infrastructure_before_target_fence_mutation(InfrastructureTamper tamper)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"groundwork_infrastructure_{suffix}";
        await ExecuteAdminAsync($"CREATE SCHEMA {Q(schema)};");
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = schema
        }.ConnectionString;
        try
        {
            var initial = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"initial-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            await TamperInfrastructureAsync(connectionString, schema, tamper);

            var rejected = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"rejected-{suffix}",
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var unused = await new PostgreSqlPhysicalSchemaExecutor(connectionString)
                    .AcquireApplicationLockAsync(rejected.Target.Identity, CancellationToken.None);
            });
            Assert.Contains("Physical-schema infrastructure", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, await CountFenceAsync(connectionString, rejected.Target.Identity));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP SCHEMA {Q(schema)} CASCADE;");
        }
    }

    [Fact]
    public Task Application_lock_disposal_is_heartbeat_race_safe() =>
        RelationalPhysicalServerAssertions.ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));

    [Fact]
    public Task Terminated_lock_backend_disposal_is_immediate_and_idempotent() =>
        RelationalPhysicalServerAssertions.TerminatedApplicationLockDisposalIsIdempotentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync);

    [Fact]
    public async Task Exhausted_fence_fails_without_poisoning_the_session_lock()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var executor = new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString());
        await using (await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None))
        {
        }
        await SetFenceAsync(model.Target.Identity, long.MaxValue);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var rejected = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None);
        });
        Assert.Contains("fence is exhausted", exception.Message, StringComparison.Ordinal);

        await SetFenceAsync(model.Target.Identity, 41);
        await using var successor = await executor.AcquireApplicationLockAsync(model.Target.Identity, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task Terminated_lock_backend_cannot_publish_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostOperationLockCannotPublishEvidenceAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            TableExistsAsync);

    [Fact]
    public Task Terminated_lock_backend_cannot_publish_applied_state() =>
        RelationalPhysicalServerAssertions.LostStateLockCannotPublishAppliedStateAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Non_provider_failure_after_real_lock_loss_marks_ownership_lost_and_uses_stable_error() =>
        RelationalPhysicalServerAssertions.NonProviderFailureAfterRealLockLossUsesStableErrorAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            beforeState => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(),
                null,
                beforeState),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task Ordinary_invalid_operation_preserves_owned_lock_and_original_error() =>
        RelationalPhysicalServerAssertions.OrdinaryInvalidOperationPreservesOwnedLockAndOriginalErrorAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            beforeState => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(),
                null,
                beforeState));

    [Fact]
    public Task Terminated_lock_backend_cannot_commit_backfill_or_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostBackfillLockCannotCommitDataOrEvidenceAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new PostgreSqlPhysicalSchemaExecutor(
                container.GetConnectionString(), beforeOperation, beforeState),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            PostgreSqlPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            CountProjectedValuesAsync);

    [Fact]
    public Task DecimalLiveAndBackfillValuesUseTheSameNativeSemantics() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "postgresql",
            PortablePhysicalType.Decimal,
            "12.3400",
            "12.34",
            "12.34",
            precision: 18,
            scale: 4);

    [Fact]
    public Task DateTimeLiveAndBackfillValuesPreserveEquivalentUtcTicks() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "postgresql",
            PortablePhysicalType.DateTime,
            "\"2026-01-01T00:00:00.0000001+01:00\"",
            "\"2025-12-31T23:00:00.0000001Z\"",
            "2025-12-31T23:00:00.0000001Z");

    [Fact]
    public async Task ExistingIncompatibleSchemaIsRejectedInsteadOfAcceptedByName()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {Q(model.Target.Routes.Single().PrimaryStorage.Name.Identifier)} (\"wrong\" integer NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())));
    }

    [Fact]
    public Task NullableUniqueProjectionUsesPortableNullDistinctSemantics() =>
        RelationalPhysicalServerAssertions.NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new PostgreSqlPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global));

    [Fact]
    public async Task Icu_database_preserves_exact_identity_linked_backfill_and_catalog_C_restart_validation()
    {
        var database = $"groundwork_icu_{Guid.NewGuid():N}";
        var connectionString = await CreateIcuDatabaseAsync(database);
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var collation = connection.CreateCommand();
                collation.CommandText = "CREATE COLLATION gw_nondeterministic (provider = icu, locale = 'und-u-ks-level1', deterministic = false);";
                await collation.ExecuteNonQueryAsync();
            }
            var instance = Guid.NewGuid().ToString("N")[..8];
            var initial = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.SharedDocuments,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: false,
                instance: instance,
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            var additive = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.SharedDocuments,
                PostgreSqlGroundworkCapabilities.Provider,
                includePriority: true,
                instance: instance,
                normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
            await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            var store = new PostgreSqlPhysicalDocumentStore(
                connectionString, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
            foreach (var id in new[] { "Case", "case", "café", "cafe" })
            {
                Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
                    "configurationDocument", id, "1", "{\"category\":\"tools\",\"priority\":7}", 0))).Status);
            }

            await PhysicalSchemaApplication.ApplyAsync(additive.Target, new PostgreSqlPhysicalSchemaExecutor(connectionString));
            var additiveStore = new PostgreSqlPhysicalDocumentStore(
                connectionString, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
            foreach (var id in new[] { "Case", "case", "café", "cafe" })
                Assert.NotNull(await additiveStore.LoadAsync("configurationDocument", id));
            var route = additive.Target.Routes.Single();
            var queries = PostgreSqlPhysicalQueryRuntime.Create(
                additiveStore, additive.Manifest, route, additive.Target.Provider);
            Assert.Equal(4, await queries.CountAsync(new DocumentQuery(
                "configurationDocument",
                "find-by-category-priority",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", "7"))
                ],
                resultOperation: BoundedQueryResultOperation.Count)));
            Assert.Equal(
                PhysicalSchemaApplicationOutcome.NoChanges,
                (await PhysicalSchemaApplication.ApplyAsync(
                    additive.Target,
                    new PostgreSqlPhysicalSchemaExecutor(connectionString))).Outcome);

            await TamperPostgreSqlIdentityCollationAsync(
                connectionString,
                route.LinkedIndexStorage!.Name.Identifier,
                route.LinkedRelationship!.DocumentId.Identifier);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PhysicalSchemaApplication.ApplyAsync(
                    additive.Target,
                    new PostgreSqlPhysicalSchemaExecutor(connectionString)));
            Assert.Contains("collation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(database);
        }
    }

    protected override Task<RelationalPhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false) =>
        CreateFixtureAsync(RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            dedicatedWithoutLinked: dedicatedWithoutLinked,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames));

    protected override async Task<RelationalServerIdentityFixture> CreateIdentityAsync(
        PhysicalStorageForm form,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames,
            stringCasePolicy: stringCasePolicy);
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalServerIdentityFixture(
            store,
            PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            synchronizeAfterPrimaryLock: true,
            lookupKey => CorruptPrimaryLookupAsync(route, lookupKey),
            (retainedId, comparisonKey) => CorruptLinkedIdentityAsync(route, retainedId, comparisonKey),
            linked => ReadIdentitySchemaAsync(route, linked),
            linked => DropComparisonEvidenceAsync(route, linked),
            async () =>
            {
                await PhysicalSchemaApplication.ApplyAsync(
                    model.Target,
                    new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
            },
            column => column,
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalServerLinkedBackfillCollisionFixture> CreateLinkedBackfillCollisionAsync()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            initial.Target,
            new PostgreSqlPhysicalSchemaExecutor(connectionString));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        return new RelationalServerLinkedBackfillCollisionFixture(
            new PostgreSqlPhysicalDocumentStore(
                connectionString,
                initial.Manifest,
                initial.Target.Routes,
                DocumentStoreAccess.Global),
            route,
            (lookupKey, retainedId, comparisonKey) => SetLinkedIdentityAsync(
                route,
                lookupKey,
                retainedId,
                comparisonKey),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new PostgreSqlPhysicalSchemaExecutor(connectionString))).Outcome,
            () => ReadNullableInt32Async(
                route.LinkedIndexStorage!.Name.Identifier,
                priority.Column.Identifier,
                route.LinkedRelationship!.DocumentId.Identifier),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            PostgreSqlGroundworkCapabilities.Provider,
            includePriority: true,
            scoped: true,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        return new RelationalScopedPhysicalStorageFixture(
            access => new PostgreSqlPhysicalDocumentStore(container.GetConnectionString(), model.Manifest, model.Target.Routes, access),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalPhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            form, PostgreSqlGroundworkCapabilities.Provider, includePriority: false, instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            form, PostgreSqlGroundworkCapabilities.Provider, includePriority: true, instance: instance,
            normalizer: PostgreSqlGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var initialStore = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageEvolutionFixture(
            initialStore,
            () => ApplyAndCreateAsync(additive),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()))).Outcome,
            async cancellationToken => await new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString())
                .AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
            () => ValueTask.CompletedTask);
    }

    private async Task<RelationalPhysicalStorageFixture> ApplyAndCreateAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model)
    {
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        return await CreateFixtureAsync(model, apply: false);
    }

    private async Task<RelationalPhysicalStorageFixture> CreateFixtureAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        bool apply = true)
    {
        if (apply)
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageFixture(
            store,
            route.ProjectedColumns.Count == 0
                ? null
                : PostgreSqlPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            () => ExplainCategoryLookupAsync(store, model.Manifest, route, model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private async Task<string> ExplainCategoryLookupAsync(
        PostgreSqlPhysicalDocumentStore store,
        Groundwork.Core.Manifests.StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider)
    {
        var query = new DocumentQuery(
            "configurationDocument",
            "list-by-category",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
            resultOperation: BoundedQueryResultOperation.Count);
        var rendered = RelationalPhysicalQueryRuntime.BuildCountCommand(
            store, manifest, route, provider, "postgresql", query);
        await SeedPlanNoiseAsync(route);
        await AnalyzeRouteAsync(route);
        return await ExplainAsync(rendered);
    }

    private async Task AnalyzeRouteAsync(ExecutableStorageRoute route)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = route.LinkedIndexStorage is null
                ? $"ANALYZE {Q(route.PrimaryStorage.Name.Identifier)};"
                : $"ANALYZE {Q(route.PrimaryStorage.Name.Identifier)}; ANALYZE {Q(route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
    }

    private async Task<string> ExplainAsync(RelationalPhysicalQueryCommand rendered)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (FORMAT JSON) {rendered.CommandText}";
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
    }

    private async Task SeedPlanNoiseAsync(ExecutableStorageRoute route)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var id = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Id.Identifier
            : route.LinkedRelationship!.DocumentId.Identifier;
        var identity = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Identity
            : route.LinkedRelationship!.Identity;
        var identityColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            id,
            identity.ComparisonKey.Identifier,
            identity.LookupKey.Identifier
        };
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table AND is_generated = 'NEVER'
                ORDER BY ordinal_position;
                """;
            metadata.Parameters.AddWithValue("table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        await using var seed = connection.CreateCommand();
        seed.CommandText = $"""
            WITH source AS (
                SELECT * FROM {Q(table)} WHERE {Q(category.Column.Identifier)} = @category LIMIT 1
            )
            INSERT INTO {Q(table)} ({string.Join(", ", columns.Select(Q))})
            SELECT {string.Join(", ", columns.Select(column => identityColumns.Contains(column)
                ? $"s.{Q(column)} || '-noise-' || n::text"
                : column == category.Column.Identifier ? "'noise'" : $"s.{Q(column)}"))}
            FROM source s CROSS JOIN generate_series(1, 4096) AS n;
            """;
        seed.Parameters.AddWithValue("category", "tools");
        await seed.ExecuteNonQueryAsync();
    }

    private async Task CorruptPrimaryLookupAsync(ExecutableStorageRoute route, string lookupKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.PrimaryStorage.Name.Identifier)} SET " +
            $"{Q(route.Envelope.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("lookupKey", lookupKey);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CorruptLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{Q(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey;";
        command.Parameters.AddWithValue("retainedId", retainedId);
        command.Parameters.AddWithValue("comparisonKey", comparisonKey);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetLinkedIdentityAsync(
        ExecutableStorageRoute route,
        string lookupKey,
        string retainedId,
        string comparisonKey)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {Q(route.LinkedIndexStorage!.Name.Identifier)} SET " +
            $"{Q(route.LinkedRelationship!.DocumentId.Identifier)} = @retainedId, " +
            $"{Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = @comparisonKey " +
            $"WHERE {Q(route.LinkedRelationship.Identity.LookupKey.Identifier)} = @lookupKey;";
        command.Parameters.AddWithValue("retainedId", retainedId);
        command.Parameters.AddWithValue("comparisonKey", comparisonKey);
        command.Parameters.AddWithValue("lookupKey", lookupKey);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<IReadOnlyList<int?>> ReadNullableInt32Async(
        string table,
        string column,
        string orderBy)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Q(column)} FROM {Q(table)} ORDER BY {Q(orderBy)};";
        var values = new List<int?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        return values;
    }

    private async Task<RelationalIdentitySchemaEvidence> ReadIdentitySchemaAsync(
        ExecutableStorageRoute route,
        bool linked)
    {
        var table = linked
            ? route.LinkedIndexStorage!.Name.Identifier
            : route.PrimaryStorage.Name.Identifier;
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table;
                """;
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        var primaryKey = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_schema = tc.constraint_schema
                 AND kcu.constraint_name = tc.constraint_name
                WHERE tc.table_schema = current_schema()
                  AND tc.table_name = @table
                  AND tc.constraint_type = 'PRIMARY KEY'
                ORDER BY kcu.ordinal_position;
                """;
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                primaryKey.Add(reader.GetString(0));
        }
        return new RelationalIdentitySchemaEvidence(columns, primaryKey);
    }

    private async Task DropComparisonEvidenceAsync(ExecutableStorageRoute route, bool linked)
    {
        var table = linked
            ? route.LinkedIndexStorage!.Name.Identifier
            : route.PrimaryStorage.Name.Identifier;
        var comparison = linked
            ? route.LinkedRelationship!.Identity.ComparisonKey.Identifier
            : route.Envelope.Identity.ComparisonKey.Identifier;
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {Q(table)} DROP COLUMN {Q(comparison)};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task TerminateSessionAsync(long sessionId)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_terminate_backend(@sessionId);";
        command.Parameters.AddWithValue("sessionId", checked((int)sessionId));
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetFenceAsync(PhysicalSchemaTargetIdentity target, long fence)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE groundwork_physical_schema_locks SET fence = @fence WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("fence", fence);
        command.Parameters.AddWithValue("manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("providerName", target.ProviderName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<string> CreateIcuDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {Q(database)} TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und-u-ks-level1';";
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString;
    }

    private async Task DropDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {Q(database)} WITH (FORCE);";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task TamperPostgreSqlIdentityCollationAsync(
        string connectionString,
        string table,
        string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        string constraint;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT conname FROM pg_catalog.pg_constraint WHERE conrelid = @table::regclass AND contype = 'p';";
            read.Parameters.AddWithValue("table", table);
            constraint = (string)(await read.ExecuteScalarAsync())!;
        }
        await using var tamper = connection.CreateCommand();
        tamper.CommandText = $"ALTER TABLE {Q(table)} DROP CONSTRAINT {Q(constraint)}; " +
                             $"ALTER TABLE {Q(table)} ALTER COLUMN {Q(column)} TYPE text COLLATE gw_nondeterministic;";
        await tamper.ExecuteNonQueryAsync();
    }

    private Task<long> CountOperationEvidenceAsync(string operationId, string fingerprint) =>
        CountAsync(
            "SELECT COUNT(*) FROM groundwork_physical_schema_operations WHERE operation_id = @first AND operation_fingerprint = @second;",
            operationId,
            fingerprint);

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL;";
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> CountAppliedStateAsync(string manifestId, string providerName) =>
        CountAsync(
            "SELECT COUNT(*) FROM groundwork_physical_schema_state WHERE manifest_id = @first AND provider_name = @second;",
            manifestId,
            providerName);

    private async Task<long> CountProjectedValuesAsync(string table, string column)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Q(table)} WHERE {Q(column)} IS NOT NULL;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountFenceAsync(string connectionString, PhysicalSchemaTargetIdentity target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_locks WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("providerName", target.ProviderName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task TamperInfrastructureAsync(
        string connectionString,
        string schema,
        InfrastructureTamper tamper)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var sql = tamper switch
        {
            InfrastructureTamper.WrongObjectKind => """
                DROP TABLE groundwork_physical_schema_locks;
                CREATE VIEW groundwork_physical_schema_locks AS
                    SELECT NULL::text AS manifest_id, NULL::text AS provider_name WHERE false;
                """,
            InfrastructureTamper.ExtraOperationsColumn =>
                "ALTER TABLE groundwork_physical_schema_operations ADD COLUMN unexpected integer NULL;",
            InfrastructureTamper.MissingStateColumn =>
                "ALTER TABLE groundwork_physical_schema_state DROP COLUMN applied_state_json;",
            InfrastructureTamper.NullableLockOwner =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id DROP NOT NULL;",
            InfrastructureTamper.WrongLockOwnerType =>
                "ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN owner_id TYPE character varying(32);",
            InfrastructureTamper.WrongStateCollation => $"""
                CREATE COLLATION {Q(schema)}.gw_nondeterministic
                    (provider = icu, locale = 'und-u-ks-level1', deterministic = false);
                ALTER TABLE groundwork_physical_schema_state ALTER COLUMN target_fingerprint
                    TYPE text COLLATE {Q(schema)}.gw_nondeterministic;
                """,
            InfrastructureTamper.SameNameCShadowCollation => $"""
                CREATE COLLATION {Q(schema)}.{Q("C")}
                    (provider = icu, locale = 'und-u-ks-level1', deterministic = false);
                ALTER TABLE groundwork_physical_schema_locks DROP CONSTRAINT groundwork_physical_schema_locks_pkey;
                ALTER TABLE groundwork_physical_schema_locks ALTER COLUMN manifest_id
                    TYPE text COLLATE {Q(schema)}.{Q("C")};
                ALTER TABLE groundwork_physical_schema_locks ADD PRIMARY KEY (manifest_id, provider_name);
                """,
            InfrastructureTamper.MissingStatePrimaryKey =>
                "ALTER TABLE groundwork_physical_schema_state DROP CONSTRAINT groundwork_physical_schema_state_pkey;",
            InfrastructureTamper.ReorderedOperationsPrimaryKey => """
                ALTER TABLE groundwork_physical_schema_operations DROP CONSTRAINT groundwork_physical_schema_operations_pkey;
                ALTER TABLE groundwork_physical_schema_operations
                    ADD PRIMARY KEY (provider_name, manifest_id, operation_id);
                """,
            InfrastructureTamper.LegacyMutationLedgerPrimaryKey => """
                ALTER TABLE groundwork_document_mutation_operations
                    DROP CONSTRAINT groundwork_document_mutation_operations_pkey;
                ALTER TABLE groundwork_document_mutation_operations
                    DROP COLUMN manifest_key,
                    DROP COLUMN provider_key,
                    DROP COLUMN storage_unit_key,
                    DROP COLUMN storage_scope_key,
                    DROP COLUMN operation_key;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD PRIMARY KEY (manifest_id, provider_name, storage_unit, storage_scope, operation_id);
                """,
            InfrastructureTamper.WrongMutationLedgerHashExpression => """
                ALTER TABLE groundwork_document_mutation_operations
                    DROP CONSTRAINT groundwork_document_mutation_operations_pkey;
                ALTER TABLE groundwork_document_mutation_operations DROP COLUMN operation_key;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD COLUMN operation_key bytea
                    GENERATED ALWAYS AS (groundwork_utf8_sha256('wrong-operation')) STORED NOT NULL;
                ALTER TABLE groundwork_document_mutation_operations
                    ADD PRIMARY KEY (manifest_key, provider_key, storage_unit_key, storage_scope_key, operation_key);
                """,
            InfrastructureTamper.WrongMutationHashFunction => """
                CREATE OR REPLACE FUNCTION groundwork_utf8_sha256(value text) RETURNS bytea
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $function$
                    SELECT pg_catalog.sha256(pg_catalog.convert_to('wrong-value', 'UTF8'))
                $function$;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
        };
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql, string first, string second)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("second", second);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private RelationalLockContentionProbe LockContention() => new(
        ReadSessionIdAsync,
        WaitUntilBlockedAsync);

    private RelationalMutationServerHarness<PostgreSqlPhysicalDocumentStore> MutationHarness() => new(
        PostgreSqlGroundworkCapabilities.Provider,
        "postgresql",
        PostgreSqlGroundworkCapabilities.PhysicalNames,
        () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()),
        (manifest, routes, access) => new PostgreSqlPhysicalDocumentStore(
            container.GetConnectionString(), manifest, routes, access),
        PostgreSqlPhysicalMutationRuntime.Create,
        PostgreSqlPhysicalQueryRuntime.Create,
        () => new NpgsqlConnection(container.GetConnectionString()),
        () => new PostgreSqlPhysicalDocumentDialect(),
        LockContention());

    private static async ValueTask<int> ReadSessionIdAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_backend_pid();";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task WaitUntilBlockedAsync(
        int blockedSessionId,
        int blockerSessionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @blocker = ANY(pg_catalog.pg_blocking_pids(@blocked));";
        command.Parameters.AddWithValue("blocker", blockerSessionId);
        command.Parameters.AddWithValue("blocked", blockedSessionId);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
                return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException(
            $"PostgreSQL session {blockedSessionId} was not observed waiting on session {blockerSessionId}.");
    }

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public enum InfrastructureTamper
    {
        WrongObjectKind,
        ExtraOperationsColumn,
        MissingStateColumn,
        NullableLockOwner,
        WrongLockOwnerType,
        WrongStateCollation,
        SameNameCShadowCollation,
        MissingStatePrimaryKey,
        ReorderedOperationsPrimaryKey,
        LegacyMutationLedgerPrimaryKey,
        WrongMutationLedgerHashExpression,
        WrongMutationHashFunction
    }
}
