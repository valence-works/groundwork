using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
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
            () => ExplainCategoryLookupAsync(route),
            () => ValueTask.CompletedTask);
    }

    private async Task<string> ExplainCategoryLookupAsync(ExecutableStorageRoute route)
    {
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var table = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        var scope = category.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.ScopeKey.Column.Identifier
            : route.LinkedRelationship!.StorageScope.Identifier;
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var enable = connection.CreateCommand())
        {
            enable.CommandText = "SET enable_seqscan = off;";
            await enable.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN SELECT * FROM {Q(table)} WHERE {Q(scope)} = @scope AND {Q(category.Column.Identifier)} = @category;";
        command.Parameters.AddWithValue("scope", "__groundwork_global__");
        command.Parameters.AddWithValue("category", "tools");
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
