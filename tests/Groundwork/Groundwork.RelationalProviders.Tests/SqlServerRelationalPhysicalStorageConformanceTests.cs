using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerPhysicalStorageContainer : IAsyncLifetime
{
    public MsSqlContainer Container { get; } =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public sealed class SqlServerRelationalPhysicalStorageConformanceTests(
    SqlServerPhysicalStorageContainer fixture)
    : RelationalPhysicalStorageConformance, IClassFixture<SqlServerPhysicalStorageContainer>
{
    private readonly MsSqlContainer container = fixture.Container;

    [Fact]
    public Task ConcurrentMaterializationAndAcknowledgementLossAreRestartSafe()
    {
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 1 }.ConnectionString;
        return RelationalPhysicalServerAssertions.ConcurrentMaterializationAndAcknowledgementLossAreRestartSafeAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(connectionString));
    }

    [Fact]
    public async Task Concurrent_distinct_targets_can_bootstrap_a_clean_database()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"groundwork_bootstrap_{suffix}";
        await ExecuteAdminAsync($"CREATE DATABASE {Q(database)};");
        var connectionString = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = database
        }.ConnectionString;
        try
        {
            var first = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.PhysicalEntityTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"first-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            var second = RelationalPhysicalStorageTestModels.Create(
                PhysicalStorageForm.DedicatedDocumentTable,
                SqlServerGroundworkCapabilities.Provider,
                includePriority: true,
                instance: $"second-{suffix}",
                normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
            var results = await Task.WhenAll(
                PhysicalSchemaApplication.ApplyAsync(first.Target, new SqlServerPhysicalSchemaExecutor(connectionString)),
                PhysicalSchemaApplication.ApplyAsync(second.Target, new SqlServerPhysicalSchemaExecutor(connectionString)));
            Assert.All(results, result => Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"ALTER DATABASE {Q(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Q(database)};");
        }
    }

    [Fact]
    public Task Application_lock_disposal_is_heartbeat_race_safe() =>
        RelationalPhysicalServerAssertions.ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));

    [Fact]
    public async Task Exhausted_fence_fails_without_poisoning_the_session_lock()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var executor = new SqlServerPhysicalSchemaExecutor(container.GetConnectionString());
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
    public Task Terminated_lock_session_cannot_publish_operation_evidence() =>
        RelationalPhysicalServerAssertions.LostOperationLockCannotPublishEvidenceAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                beforeOperation,
                beforeState),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountOperationEvidenceAsync,
            TableExistsAsync);

    [Fact]
    public Task Terminated_lock_session_cannot_publish_applied_state() =>
        RelationalPhysicalServerAssertions.LostStateLockCannotPublishAppliedStateAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            (beforeOperation, beforeState) => new SqlServerPhysicalSchemaExecutor(
                container.GetConnectionString(),
                new SqlServerPhysicalIdentityHash(),
                beforeOperation,
                beforeState),
            SqlServerPhysicalSchemaExecutor.LockSessionId,
            TerminateSessionAsync,
            CountAppliedStateAsync);

    [Fact]
    public Task DecimalLiveAndBackfillValuesUseTheSameNativeSemantics() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "sqlserver",
            PortablePhysicalType.Decimal,
            "12.3400",
            "12.34",
            "12.34",
            precision: 18,
            scale: 4);

    [Fact]
    public Task DateTimeLiveAndBackfillValuesPreserveEquivalentUtcTicks() =>
        RelationalPhysicalServerAssertions.TypedProjectionLiveAndBackfillValuesRemainEquivalentAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global),
            "sqlserver",
            PortablePhysicalType.DateTime,
            "\"2026-01-01T00:00:00.0000001+01:00\"",
            "\"2025-12-31T23:00:00.0000001Z\"",
            "2025-12-31T23:00:00.0000001Z");

    [Fact]
    public async Task ExistingIncompatibleSchemaIsRejectedInsteadOfAcceptedByName()
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await using (var connection = new SqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {Q(model.Target.Routes.Single().PrimaryStorage.Name.Identifier)} ([wrong] int NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())));
    }

    [Fact]
    public Task NullableUniqueProjectionUsesPortableNullDistinctSemantics() =>
        RelationalPhysicalServerAssertions.NullableUniqueProjectionUsesPortableNullDistinctSemanticsAsync(
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames,
            () => new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()),
            (manifest, routes) => new SqlServerPhysicalDocumentStore(
                container.GetConnectionString(), manifest, routes, DocumentStoreAccess.Global));

    [Fact]
    public async Task FourHundredFiftyCharacterUnicodeKindAndIdSupportCrudOccAndRestart()
    {
        var kind = string.Concat(Enumerable.Repeat("界", 450));
        var id = string.Concat(Enumerable.Repeat("é", 450));
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames,
            documentKind: kind);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(connectionString));

        var store = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        var saved = await store.SaveAsync(new SaveDocumentRequest(kind, id, "1", "{\"category\":\"before\"}", 0));
        Assert.Equal(DocumentStoreWriteStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Document!.Version);
        Assert.Equal(id, (await store.LoadAsync(kind, id))!.Id);

        var updated = await store.SaveAsync(new SaveDocumentRequest(kind, id, "1", "{\"category\":\"after\"}", 1));
        Assert.Equal(DocumentStoreWriteStatus.Saved, updated.Status);
        Assert.Equal(DocumentStoreWriteStatus.ConcurrencyConflict, (await store.SaveAsync(
            new SaveDocumentRequest(kind, id, "1", "{\"category\":\"stale\"}", 1))).Status);

        var restarted = new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        var loaded = await restarted.LoadAsync(kind, id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.Equal("{\"category\":\"after\"}", loaded.ContentJson);
        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await restarted.DeleteAsync(
            new DeleteDocumentRequest(kind, id, 2))).Status);
        Assert.Null(await new SqlServerPhysicalDocumentStore(
            connectionString, model.Manifest, model.Target.Routes, DocumentStoreAccess.Global).LoadAsync(kind, id));
    }

    [Fact]
    public async Task RetainedIdentityMismatchBehindTheSameHashThrowsDedicatedCollision()
    {
        var hash = new SqlServerPhysicalIdentityHash(_ =>
            "CONVERT(binary(32), 0x0000000000000000000000000000000000000000000000000000000000000000)");
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new SqlServerPhysicalSchemaExecutor(connectionString, hash));
        var store = new SqlServerPhysicalDocumentStore(
            connectionString,
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global,
            hash);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "first", "1", "{\"category\":\"tools\"}", 0))).Status);
        var collision = await Assert.ThrowsAsync<PhysicalIdentityHashCollisionException>(() => store.SaveAsync(
            new SaveDocumentRequest("configurationDocument", "second", "1", "{\"category\":\"tools\"}", 0)));

        Assert.Equal(model.Target.Routes.Single().PrimaryStorage.Name.Identifier, collision.Table);
        Assert.Equal(3, collision.IdentityColumns.Count);
        Assert.NotNull(await store.LoadAsync("configurationDocument", "first"));
        Assert.Null(await store.LoadAsync("configurationDocument", "second"));
    }

    [Fact]
    public async Task Same_target_restart_rejects_tampered_primary_and_linked_identity_hash_columns()
    {
        foreach (var linked in new[] { false, true })
        {
            foreach (var tamper in Enum.GetValues<IdentityHashTamper>())
            {
                var model = RelationalPhysicalStorageTestModels.Create(
                    linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable,
                    SqlServerGroundworkCapabilities.Provider,
                    includePriority: false,
                    normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
                await PhysicalSchemaApplication.ApplyAsync(
                    model.Target,
                    new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
                var route = model.Target.Routes.Single();
                var table = linked
                    ? route.LinkedIndexStorage!.Name.Identifier
                    : route.PrimaryStorage.Name.Identifier;
                var retainedId = linked
                    ? route.LinkedRelationship!.DocumentId.Identifier
                    : route.Envelope.Id.Identifier;
                await TamperIdentityHashColumnAsync(table, retainedId, tamper);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PhysicalSchemaApplication.ApplyAsync(
                        model.Target,
                        new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())));

                Assert.Contains(SqlServerPhysicalIdentity.HiddenColumn(retainedId), exception.Message, StringComparison.Ordinal);
                Assert.Contains("computed", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Linked_backfill_hash_collision_rolls_back_all_batches_and_can_retry_after_correction()
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            dedicatedWithoutLinked: true,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.DedicatedDocumentTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var connectionString = container.GetConnectionString();
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(connectionString));
        var initialStore = new SqlServerPhysicalDocumentStore(
            connectionString, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        for (var index = 0; index < 256; index++)
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", $"{index:D4}", "1", "{\"category\":\"tools\"}", 0))).Status);
        }
        foreach (var id in new[] { "zz-collision-a", "zz-collision-b" })
        {
            Assert.Equal(DocumentStoreWriteStatus.Saved, (await initialStore.SaveAsync(new SaveDocumentRequest(
                "configurationDocument", id, "1", "{\"category\":\"tools\"}", 0))).Status);
        }

        var route = additive.Target.Routes.Single();
        var linkedId = route.LinkedRelationship!.DocumentId.Identifier;
        var linkedIdExpression = Q(linkedId);
        var hash = new SqlServerPhysicalIdentityHash(value =>
            string.Equals(value, linkedIdExpression, StringComparison.Ordinal) ||
            string.Equals(value, "@collisionId", StringComparison.Ordinal) ||
            string.Equals(value, "@v2", StringComparison.Ordinal)
                ? $"CASE WHEN CONVERT(nvarchar(max), {value}) LIKE N'zz-collision-%' " +
                  "THEN CONVERT(binary(32), 0x0000000000000000000000000000000000000000000000000000000000000000) " +
                  $"ELSE CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(900), {value}))) END"
                : $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(900), {value})))");
        var executor = new SqlServerPhysicalSchemaExecutor(connectionString, hash);
        PhysicalSchemaDiffPlan plan;
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(additive.Target.Identity, CancellationToken.None))
        {
            var history = await executor.ReadHistoryAsync(additive.Target.Identity, applicationLock, CancellationToken.None);
            plan = PhysicalSchemaDiffPlanner.Plan(additive.Target, history, DateTimeOffset.UtcNow);
        }
        var backfills = plan.Operations.OfType<BackfillCanonicalJsonOperation>()
            .Where(operation => operation.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
            .ToArray();
        Assert.NotEmpty(backfills);

        var collision = await Assert.ThrowsAsync<PhysicalIdentityHashCollisionException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));
        Assert.Equal(route.LinkedIndexStorage!.Name.Identifier, collision.Table);
        Assert.Equal(0, await CountRowsAsync(route.LinkedIndexStorage.Name.Identifier));
        foreach (var backfill in backfills)
            Assert.Equal(0, await CountOperationEvidenceAsync(backfill.Identity, backfill.Fingerprint));

        Assert.Equal(DocumentStoreWriteStatus.Deleted, (await initialStore.DeleteAsync(
            new DeleteDocumentRequest("configurationDocument", "zz-collision-b", 1))).Status);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new SqlServerPhysicalSchemaExecutor(connectionString, hash))).Outcome);
        Assert.Equal(257, await CountRowsAsync(route.LinkedIndexStorage.Name.Identifier));
    }

    protected override Task<RelationalPhysicalStorageFixture> CreateAsync(
        PhysicalStorageForm form,
        bool dedicatedWithoutLinked = false) =>
        CreateFixtureAsync(RelationalPhysicalStorageTestModels.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            dedicatedWithoutLinked: dedicatedWithoutLinked,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames));

    protected override async Task<RelationalScopedPhysicalStorageFixture> CreateScopedAsync(PhysicalStorageForm form)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            form,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: true,
            scoped: true,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        return new RelationalScopedPhysicalStorageFixture(
            access => new SqlServerPhysicalDocumentStore(container.GetConnectionString(), model.Manifest, model.Target.Routes, access),
            () => ValueTask.CompletedTask);
    }

    protected override async Task<RelationalPhysicalStorageEvolutionFixture> CreateEvolutionAsync(PhysicalStorageForm form)
    {
        var instance = Guid.NewGuid().ToString("N")[..8];
        var initial = RelationalPhysicalStorageTestModels.Create(
            form, SqlServerGroundworkCapabilities.Provider, includePriority: false, instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        var additive = RelationalPhysicalStorageTestModels.Create(
            form, SqlServerGroundworkCapabilities.Provider, includePriority: true, instance: instance,
            normalizer: SqlServerGroundworkCapabilities.PhysicalNames);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var initialStore = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageEvolutionFixture(
            initialStore,
            () => ApplyAndCreateAsync(additive),
            async () => (await PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()))).Outcome,
            async cancellationToken => await new SqlServerPhysicalSchemaExecutor(container.GetConnectionString())
                .AcquireApplicationLockAsync(additive.Target.Identity, cancellationToken),
            () => ValueTask.CompletedTask);
    }

    private async Task<RelationalPhysicalStorageFixture> ApplyAndCreateAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model)
    {
        await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        return await CreateFixtureAsync(model, apply: false);
    }

    private async Task<RelationalPhysicalStorageFixture> CreateFixtureAsync(
        (Groundwork.Core.Manifests.StorageManifest Manifest, PhysicalSchemaTarget Target) model,
        bool apply = true)
    {
        if (apply)
            await PhysicalSchemaApplication.ApplyAsync(model.Target, new SqlServerPhysicalSchemaExecutor(container.GetConnectionString()));
        var route = model.Target.Routes.Single();
        var store = new SqlServerPhysicalDocumentStore(
            container.GetConnectionString(), model.Manifest, model.Target.Routes, DocumentStoreAccess.Global);
        return new RelationalPhysicalStorageFixture(
            store,
            route.ProjectedColumns.Count == 0
                ? null
                : SqlServerPhysicalQueryRuntime.Create(store, model.Manifest, route, model.Target.Provider),
            route,
            () => ExplainCategoryLookupAsync(store, model.Manifest, route, model.Target.Provider),
            () => ValueTask.CompletedTask);
    }

    private async Task<string> ExplainCategoryLookupAsync(
        SqlServerPhysicalDocumentStore store,
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
            store, manifest, route, provider, "sqlserver", query);
        await SeedPlanNoiseAsync(route);
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = route.LinkedIndexStorage is null
                ? $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)};"
                : $"UPDATE STATISTICS {Q(route.PrimaryStorage.Name.Identifier)}; UPDATE STATISTICS {Q(route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {rendered.CommandText} SET STATISTICS XML OFF;";
        foreach (var (name, value) in rendered.Parameters)
            command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    if (reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) ||
                        reader.GetFieldType(ordinal) == typeof(System.Data.SqlTypes.SqlXml))
                    {
                        plans.Add(reader.GetValue(ordinal).ToString() ?? string.Empty);
                    }
        } while (await reader.NextResultAsync());
        return Assert.Single(plans);
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
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND is_computed = 0 ORDER BY column_id;";
            metadata.Parameters.AddWithValue("@table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }
        await using var seed = connection.CreateCommand();
        seed.CommandText = $"""
            WITH source AS (
                SELECT TOP (1) * FROM {Q(table)} WHERE {Q(category.Column.Identifier)} = @category
            ), numbers AS (
                SELECT TOP (4096) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO {Q(table)} ({string.Join(", ", columns.Select(Q))})
            SELECT {string.Join(", ", columns.Select(column => column == id
                ? $"CONCAT(s.{Q(column)}, N'-noise-', n.n)"
                : column == category.Column.Identifier ? "N'noise'" : $"s.{Q(column)}"))}
            FROM source s CROSS JOIN numbers n;
            """;
        seed.Parameters.AddWithValue("@category", "tools");
        await seed.ExecuteNonQueryAsync();
    }

    private async Task TerminateSessionAsync(long sessionId)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"KILL {sessionId};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetFenceAsync(PhysicalSchemaTargetIdentity target, long fence)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE groundwork_physical_schema_locks SET fence = @fence WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("@fence", fence);
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.ProviderName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task TamperIdentityHashColumnAsync(string table, string retainedColumn, IdentityHashTamper tamper)
    {
        var hidden = SqlServerPhysicalIdentity.HiddenColumn(retainedColumn);
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var readKey = connection.CreateCommand())
        {
            readKey.CommandText = "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(@table) AND type = 'PK';";
            readKey.Parameters.AddWithValue("@table", table);
            var constraint = (string)(await readKey.ExecuteScalarAsync())!;
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE {Q(table)} DROP CONSTRAINT {Q(constraint)}; ALTER TABLE {Q(table)} DROP COLUMN {Q(hidden)};";
            await drop.ExecuteNonQueryAsync();
        }
        var definition = tamper switch
        {
            IdentityHashTamper.PlainBinary => $"{Q(hidden)} binary(32) NOT NULL",
            IdentityHashTamper.NonPersisted =>
                $"{Q(hidden)} AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {Q(retainedColumn)})))",
            IdentityHashTamper.WrongExpression =>
                $"{Q(hidden)} AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), N'wrong'))) PERSISTED NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null)
        };
        await using var add = connection.CreateCommand();
        add.CommandText = $"ALTER TABLE {Q(table)} ADD {definition};";
        await add.ExecuteNonQueryAsync();
    }

    private Task<long> CountOperationEvidenceAsync(string operationId, string fingerprint) =>
        CountAsync(
            "SELECT COUNT_BIG(*) FROM groundwork_physical_schema_operations WHERE operation_id = @first AND operation_fingerprint = @second;",
            operationId,
            fingerprint);

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@table", table);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private Task<long> CountAppliedStateAsync(string manifestId, string providerName) =>
        CountAsync(
            "SELECT COUNT_BIG(*) FROM groundwork_physical_schema_state WHERE manifest_id = @first AND provider_name = @second;",
            manifestId,
            providerName);

    private Task<long> CountRowsAsync(string table) =>
        CountScalarAsync($"SELECT COUNT_BIG(*) FROM {Q(table)};");

    private async Task<long> CountAsync(string sql, string first, string second)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@first", first);
        command.Parameters.AddWithValue("@second", second);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string Q(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private enum IdentityHashTamper
    {
        PlainBinary,
        NonPersisted,
        WrongExpression
    }
}
