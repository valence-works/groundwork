using Groundwork.Core.PhysicalStorage;
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
        var index = route.Indexes.Single(candidate => candidate.Identity == "by-category").Name.Identifier;
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var enable = connection.CreateCommand())
        {
            enable.CommandText = "SET STATISTICS XML ON;";
            await enable.ExecuteNonQueryAsync();
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {Q(table)} WITH (INDEX({Q(index)})) WHERE {Q(scope)} = @scope AND {Q(category.Column.Identifier)} = @category;";
            command.Parameters.AddWithValue("@scope", "__groundwork_global__");
            command.Parameters.AddWithValue("@category", "tools");
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                        lines.Add(reader.GetValue(ordinal).ToString() ?? string.Empty);
                }
            } while (await reader.NextResultAsync());
            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            await using var disable = connection.CreateCommand();
            disable.CommandText = "SET STATISTICS XML OFF;";
            await disable.ExecuteNonQueryAsync();
        }
    }

    private static string Q(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
