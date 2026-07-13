using Groundwork.Core.Capabilities;
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
    : RelationalPhysicalStorageConformance, IClassFixture<PostgreSqlPhysicalStorageContainer>
{
    private readonly PostgreSqlContainer container = fixture.Container;

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

    [Fact]
    public Task Application_lock_disposal_is_heartbeat_race_safe() =>
        RelationalPhysicalServerAssertions.ApplicationLockDisposalIsHeartbeatRaceSafeAsync(
            PostgreSqlGroundworkCapabilities.Provider,
            PostgreSqlGroundworkCapabilities.PhysicalNames,
            () => new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString()));

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
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = route.LinkedIndexStorage is null
                ? $"ANALYZE {Q(route.PrimaryStorage.Name.Identifier)};"
                : $"ANALYZE {Q(route.PrimaryStorage.Name.Identifier)}; ANALYZE {Q(route.LinkedIndexStorage.Name.Identifier)};";
            await statistics.ExecuteNonQueryAsync();
        }
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
            SELECT {string.Join(", ", columns.Select(column => column == id
                ? $"s.{Q(column)} || '-noise-' || n::text"
                : column == category.Column.Identifier ? "'noise'" : $"s.{Q(column)}"))}
            FROM source s CROSS JOIN generate_series(1, 4096) AS n;
            """;
        seed.Parameters.AddWithValue("category", "tools");
        await seed.ExecuteNonQueryAsync();
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

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
