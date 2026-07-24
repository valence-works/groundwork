using System.Diagnostics;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Groundwork.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqlitePhysicalSchemaExecutorTests
{
    private const string CrossProcessDatabaseEnvironment = "GROUNDWORK_SCHEMA_LOCK_DATABASE";

    [Fact]
    public async Task Physical_factory_is_inspect_only_by_default()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var model = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);

        await Assert.ThrowsAsync<GroundworkRuntimeSchemaAdmissionException>(() =>
            SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connection,
                model.Manifest,
                model.Target.Provider,
                DocumentStoreAccess.Global));

        Assert.False(await TableExistsAsync(connection, "groundwork_physical_schema_state"));
    }

    [Fact]
    public async Task Physical_factory_auto_applies_safe_schema_when_enabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var model = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);

        var store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
            connection,
            model.Manifest,
            model.Target.Provider,
            DocumentStoreAccess.Global,
            options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.NotNull(store);
        Assert.True(await TableExistsAsync(connection, "groundwork_physical_schema_state"));
    }

    [Fact]
    public async Task File_backed_physical_factory_persists_safe_auto_apply_before_returning()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"groundwork-startup-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var model = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        try
        {
            var store = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connectionString,
                model.Manifest,
                model.Target.Provider,
                DocumentStoreAccess.Global,
                options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });
            await using var inspection = new SqliteConnection(connectionString);
            await inspection.OpenAsync();

            Assert.NotNull(store);
            Assert.True(await TableExistsAsync(inspection, "groundwork_physical_schema_state"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Physical_factory_rejects_invalid_manifest_before_auto_apply_mutates_schema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var model = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        var invalidManifest = model.Manifest with
        {
            StorageUnits =
            [
                model.Manifest.StorageUnits.Single() with { Lifecycle = null! }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connection,
                invalidManifest,
                model.Target.Provider,
                DocumentStoreAccess.Global,
                options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true }));

        Assert.Contains("GW-UNIT-006", exception.Message, StringComparison.Ordinal);
        await connection.OpenAsync();
        Assert.False(await TableExistsAsync(connection, "groundwork_physical_schema_state"));
    }

    [Fact]
    public async Task Named_private_in_memory_factory_reuses_retained_database_for_restart_admission()
    {
        var dataSource = Path.Combine(Path.GetTempPath(), $"groundwork-memory-{Guid.NewGuid():N}");
        var connectionString = $"Data Source={dataSource};Mode=Memory;Cache=Private";
        await using var connection = new SqliteConnection(connectionString);
        var model = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        try
        {
            await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connection,
                model.Manifest,
                model.Target.Provider,
                DocumentStoreAccess.Global,
                options: new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

            var restart = await SqliteDocumentStoreFactory.OpenPhysicalAsync(
                connection,
                model.Manifest,
                model.Target.Provider,
                DocumentStoreAccess.Global);

            Assert.NotNull(restart);
            Assert.True(await TableExistsAsync(connection, "groundwork_physical_schema_state"));
        }
        finally
        {
            foreach (var lockFile in Directory.GetFiles(
                         Path.GetDirectoryName(dataSource)!,
                         $"{Path.GetFileName(dataSource)}.groundwork-*.schema.lock"))
            {
                File.Delete(lockFile);
            }
            File.Delete(dataSource);
        }
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task MaterializesCompiledRoutesAndPersistsRestartSafeAppliedState(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(form, includePriority: false).Target;
        var executor = new SqlitePhysicalSchemaExecutor(connection);

        var first = await PhysicalSchemaApplication.ApplyAsync(target, executor);
        var restart = await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, first.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        Assert.Equal(target.Fingerprint, restart.AppliedState!.TargetFingerprint);
        foreach (var route in target.Routes)
        {
            Assert.True(await TableExistsAsync(connection, route.PrimaryStorage.Name.Identifier));
            if (route.LinkedIndexStorage is not null)
                Assert.True(await TableExistsAsync(connection, route.LinkedIndexStorage.Name.Identifier));
        }
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SameVersionAdditiveColumnsAndIndexesAreAppliedAndRecorded(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        var initial = CreateModel(form, includePriority: false).Target;
        var additive = CreateModel(form, includePriority: true).Target;

        await PhysicalSchemaApplication.ApplyAsync(initial, executor);
        var applied = await PhysicalSchemaApplication.ApplyAsync(additive, executor);
        var restart = await PhysicalSchemaApplication.ApplyAsync(additive, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        var route = additive.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Contains(priority.Column.Identifier, await ColumnNamesAsync(connection, table));
        Assert.True(await IndexExistsAsync(connection, route.Indexes.Single(index => index.Identity == "by-priority").Name.Identifier));
        Assert.Equal(additive.Fingerprint, applied.AppliedState!.TargetFingerprint);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task SameVersionAdditiveProjectionBackfillsPreexistingCanonicalDocuments(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(form, includePriority: true);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        var store = new SqlitePhysicalDocumentStore(
            connection,
            initial.Manifest,
            initial.Target.Routes,
            DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "preexisting", "1", """{"category":"tools","priority":42}""", 0));

        await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);

        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{priority.Column.Identifier}\" FROM \"{table}\";";
        Assert.Equal(42L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task Additive_linked_string_projection_length_is_validated_before_sqlite_backfill(
        int valueLength,
        bool rejects)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(PhysicalStorageForm.SharedDocuments, includePriority: false);
        var additive = CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true,
            priorityType: PortablePhysicalType.String,
            priorityLength: 128);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        var documents = new SqlitePhysicalDocumentStore(
            connection,
            initial.Manifest,
            initial.Target.Routes,
            DocumentStoreAccess.Global);
        var value = new string('a', valueLength);
        await documents.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "preexisting", "1", $"{{\"category\":\"tools\",\"priority\":\"{value}\"}}", 0));

        if (!rejects)
        {
            await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
            var projection = additive.Target.Routes.Single().ProjectedColumns.Single(column =>
                column.Definition.LogicalName == "priority");
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"{projection.Column.Identifier}\" FROM \"{additive.Target.Routes.Single().LinkedIndexStorage!.Name.Identifier}\";";
            Assert.Equal(value, await command.ExecuteScalarAsync());
            return;
        }

        var exception = await Assert.ThrowsAsync<PhysicalProjectionValueValidationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));
        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
        Assert.Contains("priority", exception.Diagnostic.Target);
        Assert.Contains(value, (await documents.LoadAsync("configurationDocument", "preexisting"))!.ContentJson);
        var inspection = await executor.InspectHistoryAsync(additive.Target, CancellationToken.None);
        Assert.Equal(initial.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
        Assert.NotEqual(additive.Target.Fingerprint, inspection.History.AppliedState?.TargetFingerprint);
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task String_projection_default_length_is_validated_before_sqlite_target_admission(
        int valueLength,
        bool rejects)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(
            PhysicalStorageForm.SharedDocuments,
            includePriority: true,
            priorityType: PortablePhysicalType.String,
            priorityLength: 128,
            priorityDefault: new string('a', valueLength)).Target;
        var executor = new SqlitePhysicalSchemaExecutor(connection);

        if (!rejects)
        {
            await PhysicalSchemaApplication.ApplyAsync(target, executor);
            Assert.Equal(target.Fingerprint, (await executor.InspectHistoryAsync(target, CancellationToken.None))
                .History.AppliedState?.TargetFingerprint);
            return;
        }

        var exception = await Assert.ThrowsAsync<PhysicalProjectionValueValidationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, executor));
        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
        Assert.Null((await executor.InspectHistoryAsync(target, CancellationToken.None)).History.AppliedState);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task AdditiveRequiredProjectionStagesBackfillsAndFinalizesExistingRows(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(form, includePriority: true, priorityNullable: false);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "preexisting", "1", """{"category":"tools","priority":42}""", 0));

        var applied = await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
        var restart = await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);

        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        Assert.Equal(1L, await ColumnNotNullAsync(connection, table, priority.Column.Identifier));
        await using var value = connection.CreateCommand();
        value.CommandText = $"SELECT \"{priority.Column.Identifier}\" FROM \"{table}\";";
        Assert.Equal(42L, Convert.ToInt64(await value.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task NullableProjectionMayShareItsResolvedIdentifierWithItsTable(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var normalizer = SameTableAndPriorityName(form);
        var target = CreateModel(
            form,
            includePriority: true,
            normalizer: normalizer,
            priorityCollation: "nocase").Target;

        var result = await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        var route = target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal(table, priority.Column.Identifier);
        Assert.Equal(0L, await ColumnNotNullAsync(connection, table, priority.Column.Identifier));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task RequiredProjectionMayShareItsResolvedIdentifierWithItsTableDuringFinalization(
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var normalizer = SameTableAndPriorityName(form);
        var initial = CreateModel(form, includePriority: false, normalizer: normalizer);
        var additive = CreateModel(
            form,
            includePriority: true,
            priorityNullable: false,
            normalizer: normalizer,
            priorityCollation: "nocase");
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "preexisting", "1", """{"category":"tools","priority":42}""", 0));

        var result = await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;
        Assert.Equal(table, priority.Column.Identifier);
        Assert.Equal(1L, await ColumnNotNullAsync(connection, table, priority.Column.Identifier));
        foreach (var index in route.Indexes)
            Assert.True(await IndexExistsAsync(connection, index.Name.Identifier));
        await using var value = connection.CreateCommand();
        value.CommandText = $"SELECT \"{priority.Column.Identifier}\" FROM \"{table}\";";
        Assert.Equal(42L, Convert.ToInt64(await value.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData("double")]
    [InlineData("bracket")]
    [InlineData("backtick")]
    [InlineData("unquoted")]
    public async Task Compatible_preexisting_ddl_can_be_finalized_without_losing_complex_indexes(
        string quoting)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var tableName = quoting == "double" ? "preexisting\"entities" : $"preexisting_{quoting}_entities";
        var normalizer = new DelegateProviderPhysicalNameNormalizer(context =>
            context.ObjectKind == PhysicalObjectKind.PrimaryStorage
                ? tableName
                : context.LogicalName);
        var initial = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: false,
            normalizer: normalizer,
            categoryNullable: true);
        var additive = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityNullable: false,
            normalizer: normalizer,
            categoryNullable: true);
        var route = initial.Target.Routes.Single();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = CompatiblePreexistingTableSql(route, quoting);
            await create.ExecuteNonQueryAsync();
        }
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor)).Outcome);
        const string customIndex = "custom \"nested,partial\" index";
        await using (var index = connection.CreateCommand())
        {
            index.CommandText = $"""
                CREATE INDEX {Quote(customIndex)} ON {Quote(tableName)}
                (json_extract({Quote(route.Envelope.CanonicalJson.Identifier)}, '$."a,b"[0]'))
                WHERE json_valid({Quote(route.Envelope.CanonicalJson.Identifier)});
                """;
            await index.ExecuteNonQueryAsync();
        }

        var result = await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        var priority = additive.Target.Routes.Single().ProjectedColumns
            .Single(column => column.Definition.LogicalName == "priority");
        Assert.Equal(1L, await ColumnNotNullAsync(connection, tableName, priority.Column.Identifier));
        Assert.True(await IndexExistsAsync(connection, customIndex));
        foreach (var index in additive.Target.Routes.Single().Indexes)
            Assert.True(await IndexExistsAsync(connection, index.Name.Identifier));
    }

    [Fact]
    public async Task RequiredProjectionWithoutCanonicalValueFailsBeforeFinalization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        var additive = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityNullable: false);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest("configurationDocument", "missing", "1", """{"category":"tools"}""", 0));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));

        Assert.Contains("priority", exception.Message);
        // The single-transaction batch rolls the staged column back with the rest of the plan.
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        Assert.DoesNotContain(
            priority.Column.Identifier,
            await ColumnNamesAsync(connection, route.PrimaryStorage.Name.Identifier));
    }

    [Fact]
    public async Task RequiredProjectionUsesValidatedPortableDefaultDuringBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        var additive = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityNullable: false,
            priorityDefault: "7");
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest("configurationDocument", "defaulted", "1", """{"category":"tools"}""", 0));

        await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);

        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        await using var value = connection.CreateCommand();
        value.CommandText = $"SELECT \"{priority.Column.Identifier}\" FROM \"{route.PrimaryStorage.Name.Identifier}\";";
        Assert.Equal(7L, Convert.ToInt64(await value.ExecuteScalarAsync()));
        Assert.Equal(1L, await ColumnNotNullAsync(connection, route.PrimaryStorage.Name.Identifier, priority.Column.Identifier));
    }

    [Fact]
    public async Task ConcurrentApplicantsSerializeAcrossIndependentFileConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"groundwork-physical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "groundwork.db")}";
            await using var firstConnection = new SqliteConnection(connectionString);
            await using var secondConnection = new SqliteConnection(connectionString);
            await firstConnection.OpenAsync();
            await secondConnection.OpenAsync();
            var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;

            var results = await Task.WhenAll(
                PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(firstConnection)),
                PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(secondConnection)));

            Assert.Contains(results, result => result.Outcome == PhysicalSchemaApplicationOutcome.Applied);
            Assert.Contains(results, result => result.Outcome == PhysicalSchemaApplicationOutcome.NoChanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApplicationLeaseExcludesACompetingProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"groundwork-physical-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "groundwork.db");
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target.Identity;
            var executor = new SqlitePhysicalSchemaExecutor(connection);
            await using var applicationLock = await executor.AcquireApplicationLockAsync(target, CancellationToken.None);
            var root = RepositoryRootLocator.FindRepositoryRoot();
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("test");
            start.ArgumentList.Add("tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("Release");
            start.ArgumentList.Add("--no-build");
            start.ArgumentList.Add("--no-restore");
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~CrossProcessSchemaLockContender");
            start.Environment[CrossProcessDatabaseEnvironment] = database;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the schema-lock contender process.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                process.ExitCode == 0,
                $"Schema-lock contender failed.{Environment.NewLine}{await standardOutput}{Environment.NewLine}{await standardError}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CrossProcessSchemaLockContender()
    {
        var database = Environment.GetEnvironmentVariable(CrossProcessDatabaseEnvironment);
        if (database is null)
            return;
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target.Identity;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SqlitePhysicalSchemaExecutor(connection).AcquireApplicationLockAsync(target, cancellation.Token));
    }

    [Fact]
    public async Task OneExecutorKeepsConcurrentTargetOperationLedgersIsolated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var first = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        var second = new PhysicalSchemaTarget(
            new StorageManifestIdentity("other-manifest"),
            first.ManifestVersion,
            first.Provider,
            first.Routes);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        var firstOperation = PhysicalSchemaDiffPlanner.Plan(first, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations.First(operation => operation is not RecordPhysicalSchemaAppliedStateOperation);
        var secondOperation = PhysicalSchemaDiffPlanner.Plan(second, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations.First(operation => operation is not RecordPhysicalSchemaAppliedStateOperation);

        await using var firstLock = await executor.AcquireApplicationLockAsync(first.Identity, CancellationToken.None);
        await using var secondLock = await executor.AcquireApplicationLockAsync(second.Identity, CancellationToken.None);
        await executor.ApplyOperationAsync(first.Identity, firstOperation, firstLock, CancellationToken.None);
        await executor.ApplyOperationAsync(second.Identity, secondOperation, secondLock, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT manifest_id) FROM groundwork_physical_schema_operations;";
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RestartReconcilesADurableOperationWhoseAcknowledgementWasLost()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        var operation = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations.First(candidate => candidate is not RecordPhysicalSchemaAppliedStateOperation);
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(target.Identity, CancellationToken.None))
            _ = await executor.ApplyOperationAsync(
                target.Identity,
                operation,
                applicationLock,
                CancellationToken.None);

        var restarted = await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, restarted.Outcome);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_operations WHERE operation_id = @identity;";
        command.Parameters.AddWithValue("@identity", operation.Identity);
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task UnpublishedBackfillAcknowledgementLossReplaysInterleavedWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        var additive = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        var oldRouteStore = new SqlitePhysicalDocumentStore(
            connection,
            initial.Manifest,
            initial.Target.Routes,
            DocumentStoreAccess.Global);
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("before-loss", 41))).Status);

        var acknowledgementLosing = new BackfillAcknowledgementLosingExecutor(executor);
        await Assert.ThrowsAsync<SimulatedBackfillAcknowledgementLossException>(() =>
            PhysicalSchemaApplication.ApplyAsync(
                additive.Target,
                acknowledgementLosing));
        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("between-attempts", 42))).Status);

        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor)).Outcome);
        var currentStore = new SqlitePhysicalDocumentStore(
            connection,
            additive.Manifest,
            additive.Target.Routes,
            DocumentStoreAccess.Global);
        var queries = SqlitePhysicalQueryRuntime.Create(
            currentStore,
            additive.Manifest,
            additive.Target.Routes.Single(),
            additive.Target.Provider);
        Assert.Equal(1, await CountPriorityAsync(queries, "41"));
        Assert.Equal(1, await CountPriorityAsync(queries, "42"));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.NoChanges,
            (await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor)).Outcome);

        Assert.Equal(DocumentStoreWriteStatus.Saved, (await oldRouteStore.SaveAsync(
            Save("after-publication", 43))).Status);
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(
                         additive.Target.Identity,
                         CancellationToken.None))
        {
            var acknowledgement = await executor.ApplyOperationAsync(
                additive.Target.Identity,
                acknowledgementLosing.Backfill!,
                applicationLock,
                CancellationToken.None);
            Assert.Equal(acknowledgementLosing.Acknowledgement, acknowledgement);
        }
        Assert.Equal(0, await CountPriorityAsync(queries, "43"));

        static SaveDocumentRequest Save(string id, int priority) =>
            new("configurationDocument", id, "1", $"{{\"category\":\"tools\",\"priority\":{priority}}}", 0);

        static Task<long> CountPriorityAsync(IBoundedDocumentStore queries, string priority) =>
            queries.CountAsync(new DocumentQuery(
                "configurationDocument",
                "find-by-category-priority",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", priority))
                ],
                resultOperation: BoundedQueryResultOperation.Count));
    }

    [Fact]
    public async Task IncompatiblePreexistingPrimarySchemaIsRejectedWithoutAcknowledgement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE TABLE \"{target.Routes.Single().PrimaryStorage.Name.Identifier}\" (\"id\" TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_operations;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task IncompatiblePreexistingProjectedTypeNullabilityAndCollationAreRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        var route = target.Routes.Single();
        var envelope = route.Envelope;
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var primaryKey = string.Join(", ", route.PrimaryKey.Columns.Select(column => $"\"{column.Identifier}\""));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TABLE "{route.PrimaryStorage.Name.Identifier}" (
                    "{envelope.DocumentKind.Identifier}" TEXT NOT NULL,
                    "{envelope.StorageScope.Identifier}" TEXT NOT NULL,
                    "{envelope.Identity.OriginalId.Identifier}" TEXT NOT NULL,
                    "{envelope.Identity.ComparisonKey.Identifier}" TEXT NOT NULL,
                    "{envelope.Identity.LookupKey.Identifier}" TEXT NOT NULL,
                    "{envelope.SchemaVersion.Identifier}" TEXT NOT NULL,
                    "{envelope.Version.Identifier}" INTEGER NOT NULL,
                    "{envelope.CanonicalJson.Identifier}" TEXT NOT NULL,
                    "created_utc" TEXT NOT NULL,
                    "updated_utc" TEXT NOT NULL,
                    "{category.Column.Identifier}" INTEGER NULL COLLATE NOCASE,
                    PRIMARY KEY ({primaryKey})
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        Assert.Contains("incompatible", exception.Message);
        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_state;";
        Assert.Equal(0L, Convert.ToInt64(await state.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task IncompatiblePreexistingIndexIsRejectedInsteadOfRecorded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
        var route = target.Routes.Single();
        var index = route.Indexes.Single(candidate => candidate.Identity == "by-category");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                DELETE FROM groundwork_physical_schema_state;
                DELETE FROM groundwork_physical_schema_operations;
                DROP INDEX "{index.Name.Identifier}";
                CREATE INDEX "{index.Name.Identifier}" ON "{route.PrimaryStorage.Name.Identifier}" ("{route.Envelope.Id.Identifier}");
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_state;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task BatchApplyRollsBackEveryOperationWhenTrailingValidationFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            categoryNullable: true).Target;
        var route = target.Routes.Single();
        var byCategory = route.Indexes.Single(index => index.Identity == "by-category");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                {CompatiblePreexistingTableSql(route, "double")}
                CREATE INDEX {Quote(byCategory.Name.Identifier)} ON {Quote(route.PrimaryStorage.Name.Identifier)}
                ({Quote(route.Envelope.Id.Identifier)});
                """;
            await command.ExecuteNonQueryAsync();
        }
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var byPriority = route.Indexes.Single(index => index.Identity == "by-priority");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        Assert.DoesNotContain(
            priority.Column.Identifier,
            await ColumnNamesAsync(connection, route.PrimaryStorage.Name.Identifier));
        Assert.False(await IndexExistsAsync(connection, byPriority.Name.Identifier));
        await using var operations = connection.CreateCommand();
        operations.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_operations;";
        Assert.Equal(0L, Convert.ToInt64(await operations.ExecuteScalarAsync()));
        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_state;";
        Assert.Equal(0L, Convert.ToInt64(await state.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task BatchApplyReplaysPriorDurableAcknowledgementsWithoutReapplying()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var target = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: true).Target;
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        var operations = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations
            .Where(operation => operation is not RecordPhysicalSchemaAppliedStateOperation)
            .ToArray();

        IReadOnlyList<PhysicalSchemaOperationAcknowledgement> first;
        IReadOnlyList<PhysicalSchemaOperationAcknowledgement> replay;
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(target.Identity, CancellationToken.None))
            first = await executor.ApplyOperationBatchAsync(target.Identity, operations, applicationLock, CancellationToken.None);
        await using (var applicationLock = await executor.AcquireApplicationLockAsync(target.Identity, CancellationToken.None))
            replay = await executor.ApplyOperationBatchAsync(target.Identity, operations, applicationLock, CancellationToken.None);

        Assert.Equal(operations.Select(operation => operation.Identity), first.Select(item => item.Identity));
        Assert.Equal(first, replay);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM groundwork_physical_schema_operations;";
        Assert.Equal((long)operations.Length, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ProviderNamesCannotCollideWithReservedRelationalEnvelopeColumns()
    {
        var normalizer = new DelegateProviderPhysicalNameNormalizer(context =>
            context.ObjectKind == PhysicalObjectKind.ProjectedField && context.LogicalName == "priority"
                ? "created_utc"
                : context.LogicalName);
        var target = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            normalizer: normalizer).Target;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        Assert.Contains("reserved", exception.Message);
    }

    [Fact]
    public async Task SqliteRejectsDecimalPrecisionThatCannotUseItsExactIntegerEncoding()
    {
        var target = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityType: PortablePhysicalType.Decimal,
            priorityPrecision: 19,
            priorityScale: 4).Target;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));

        Assert.Contains("precision 1..18", exception.Message);
    }

    public static IEnumerable<object[]> SupportedSqliteDecimalPrecisions =>
        Enumerable.Range(1, 18).Select(precision => new object[] { precision });

    [Theory]
    [MemberData(nameof(SupportedSqliteDecimalPrecisions))]
    public async Task SqliteAcceptsEveryExactIntegerBackedDecimalPrecision(int precision)
    {
        var target = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityType: PortablePhysicalType.Decimal,
            priorityPrecision: precision,
            priorityScale: Math.Min(precision, 4)).Target;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var result = await PhysicalSchemaApplication.ApplyAsync(
            target,
            new SqlitePhysicalSchemaExecutor(connection));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
    }

    [Theory]
    [InlineData(PortablePhysicalType.Decimal, "1e-29", 18, 4)]
    [InlineData(PortablePhysicalType.DateTime, "2026-01-01T00:00:00.00000015Z", null, null)]
    public async Task LossyPortableDefaultsAreRejectedBeforeTheyCanBeApplied(
        PortablePhysicalType type,
        string defaultValue,
        int? precision,
        int? scale)
    {
        var target = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityType: type,
            priorityPrecision: precision,
            priorityScale: scale,
            priorityDefault: defaultValue).Target;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection)));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DecimalBackfillRejectsLexicalValuesThatWouldRoundIntoTheDeclaredShape(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(
            form,
            includePriority: true,
            priorityType: PortablePhysicalType.Decimal,
            priorityPrecision: 18,
            priorityScale: 4);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "lossy", "1",
                """{"category":"tools","priority":99999999999999.99990000000000001}""", 0));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DateTimeBackfillRejectsSubTickCanonicalValues(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(form, includePriority: true, priorityType: PortablePhysicalType.DateTime);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "sub-tick", "1",
                """{"category":"tools","priority":"2026-01-01T00:00:00.00000015Z"}""", 0));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task LinkedBackfillRejectsLookupCollisionEvidenceAndRollsBackProjectedRows(
        PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(form, includePriority: true);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        var store = new SqlitePhysicalDocumentStore(
            connection,
            initial.Manifest,
            initial.Target.Routes,
            DocumentStoreAccess.Global);
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "a-valid", "1", """{"category":"tools","priority":1}""", 0));
        await store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "z-collision", "1", """{"category":"tools","priority":2}""", 0));
        var route = initial.Target.Routes.Single();
        await using (var corruptEvidence = connection.CreateCommand())
        {
            corruptEvidence.CommandText =
                $"UPDATE {Q(route.LinkedIndexStorage!.Name.Identifier)} SET " +
                $"{Q(route.LinkedRelationship!.DocumentId.Identifier)} = 'Retained-Collision-Id', " +
                $"{Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)} = 'different-comparison' " +
                $"WHERE {Q(route.LinkedRelationship.DocumentId.Identifier)} = 'z-collision';";
            await corruptEvidence.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DocumentIdentityLookupCollisionException>(() =>
            PhysicalSchemaApplication.ApplyAsync(additive.Target, executor));

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("z-collision", exception.RequestedId);
        Assert.Equal("Retained-Collision-Id", exception.RetainedId);
        Assert.Equal(route.LinkedRelationship.Identity.Project("z-collision").LookupKey, exception.LookupKey);
        // The single-transaction batch rolls the whole additive plan back, so the linked rows and
        // schema return to their pre-apply shape.
        var priority = additive.Target.Routes.Single().ProjectedColumns
            .Single(column => column.Definition.LogicalName == "priority");
        Assert.DoesNotContain(
            priority.Column.Identifier,
            await ColumnNamesAsync(connection, route.LinkedIndexStorage.Name.Identifier));
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {Q(route.LinkedIndexStorage.Name.Identifier)};";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DecimalLiveAndBackfilledValuesUseTheSameExactScaledInteger(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(
            form,
            includePriority: true,
            priorityType: PortablePhysicalType.Decimal,
            priorityPrecision: 18,
            priorityScale: 4);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "backfilled", "1",
                """{"category":"tools","priority":99999999999999.9998}""", 0));
        await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "live", "1",
                """{"category":"tools","priority":99999999999999.9999}""", 0));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT typeof(\"{priority.Column.Identifier}\"), \"{priority.Column.Identifier}\" FROM \"{table}\" ORDER BY \"{priority.Column.Identifier}\";";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("integer", reader.GetString(0));
        Assert.Equal(999999999999999998L, reader.GetInt64(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("integer", reader.GetString(0));
        Assert.Equal(999999999999999999L, reader.GetInt64(1));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public async Task DateTimeLiveAndBackfilledValuesPreserveAdjacentUtcTicks(PhysicalStorageForm form)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(form, includePriority: false);
        var additive = CreateModel(form, includePriority: true, priorityType: PortablePhysicalType.DateTime);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "backfilled", "1",
                """{"category":"tools","priority":"2026-01-01T00:00:00.0000000Z"}""", 0));
        await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
        await new SqlitePhysicalDocumentStore(connection, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global)
            .SaveAsync(new SaveDocumentRequest(
                "configurationDocument", "live", "1",
                """{"category":"tools","priority":"2026-01-01T00:00:00.0000001Z"}""", 0));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");
        var table = priority.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name.Identifier
            : route.LinkedIndexStorage!.Name.Identifier;

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT typeof(\"{priority.Column.Identifier}\"), \"{priority.Column.Identifier}\" FROM \"{table}\" ORDER BY \"{priority.Column.Identifier}\";";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("integer", reader.GetString(0));
        var first = reader.GetInt64(1);
        Assert.True(await reader.ReadAsync());
        Assert.Equal("integer", reader.GetString(0));
        Assert.Equal(first + 1, reader.GetInt64(1));
    }

    [Fact]
    public async Task LiveAndBackfilledBooleanProjectionsUseTheSameSqliteTypeAndValue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var initial = CreateModel(PhysicalStorageForm.PhysicalEntityTable, includePriority: false);
        var additive = CreateModel(
            PhysicalStorageForm.PhysicalEntityTable,
            includePriority: true,
            priorityType: PortablePhysicalType.Boolean);
        var executor = new SqlitePhysicalSchemaExecutor(connection);
        await PhysicalSchemaApplication.ApplyAsync(initial.Target, executor);
        var initialStore = new SqlitePhysicalDocumentStore(connection, initial.Manifest, initial.Target.Routes, DocumentStoreAccess.Global);
        await initialStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "backfilled", "1", """{"category":"tools","priority":true}""", 0));
        await PhysicalSchemaApplication.ApplyAsync(additive.Target, executor);
        var additiveStore = new SqlitePhysicalDocumentStore(connection, additive.Manifest, additive.Target.Routes, DocumentStoreAccess.Global);
        await additiveStore.SaveAsync(new SaveDocumentRequest(
            "configurationDocument", "live", "1", """{"category":"tools","priority":false}""", 0));
        var route = additive.Target.Routes.Single();
        var priority = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "priority");

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{route.Envelope.Id.Identifier}\", typeof(\"{priority.Column.Identifier}\"), \"{priority.Column.Identifier}\" FROM \"{route.PrimaryStorage.Name.Identifier}\" ORDER BY \"{route.Envelope.Id.Identifier}\";";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("backfilled", reader.GetString(0));
        Assert.Equal("integer", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("live", reader.GetString(0));
        Assert.Equal("integer", reader.GetString(1));
        Assert.Equal(0L, reader.GetInt64(2));

        await reader.DisposeAsync();
        var queries = SqlitePhysicalQueryRuntime.Create(additiveStore, additive.Manifest, route, additive.Target.Provider);
        var trueCount = await queries.CountAsync(new DocumentQuery(
            "configurationDocument",
            "find-by-category-priority",
            [
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools")),
                DocumentQueryClause.Of(DocumentQueryComparison.Equal("priority", "true"))
            ],
            resultOperation: BoundedQueryResultOperation.Count));
        Assert.Equal(1, trueCount);
    }

    private static string Q(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateModel(
        PhysicalStorageForm form,
        bool includePriority,
        bool scoped = false,
        bool categoryUnique = false,
        IReadOnlySet<PortableQueryOperation>? categoryOperations = null,
        PortablePhysicalType priorityType = PortablePhysicalType.Int32,
        int? priorityPrecision = null,
        int? priorityScale = null,
        int? priorityLength = null,
        bool priorityNullable = true,
        string? priorityDefault = null,
        IProviderPhysicalNameNormalizer? normalizer = null,
        string? priorityCollation = null,
        bool categoryNullable = false,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.Ordinal,
        QueryPagingSupport categoryPaging = QueryPagingSupport.Offset,
        QueryPagingSupport compoundPaging = QueryPagingSupport.None,
        bool includeLatestPerCategory = false)
    {
        var template = SqliteTestManifests.MetadataManifest();
        var columns = new List<ProjectedColumnDefinition>
        {
            new("category", "category", PortablePhysicalType.String, Length: 200, IsNullable: categoryNullable)
        };
        var categoryIndexColumns = new List<PhysicalIndexColumnDefinition>();
        if (scoped)
            categoryIndexColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
        categoryIndexColumns.Add(new PhysicalIndexColumnDefinition("category", categoryIndexColumns.Count));
        if (categoryPaging == QueryPagingSupport.Cursor)
        {
            categoryIndexColumns.Add(new PhysicalIndexColumnDefinition(
                new DocumentEnvelopeDefinition().IdLookupKeyColumn,
                categoryIndexColumns.Count));
        }
        var indexes = new List<PhysicalIndexDefinition>
        {
            new("by-category", categoryIndexColumns, isUnique: categoryUnique)
        };
        if (includePriority)
        {
            columns.Add(new ProjectedColumnDefinition(
                "priority",
                "priority",
                priorityType,
                Length: priorityLength,
                Precision: priorityPrecision,
                Scale: priorityScale,
                IsNullable: priorityNullable,
                DefaultValue: priorityDefault,
                Collation: priorityCollation));
            if (includeLatestPerCategory)
                columns.Add(new ProjectedColumnDefinition("visible", "visible", PortablePhysicalType.Boolean));
            var priorityIndexColumns = new List<PhysicalIndexColumnDefinition>();
            if (scoped)
                priorityIndexColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
            priorityIndexColumns.Add(new PhysicalIndexColumnDefinition("priority", priorityIndexColumns.Count));
            indexes.Add(new PhysicalIndexDefinition("by-priority", priorityIndexColumns));
            var compoundColumns = new List<PhysicalIndexColumnDefinition>();
            if (scoped)
                compoundColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
            compoundColumns.Add(new PhysicalIndexColumnDefinition("category", compoundColumns.Count));
            compoundColumns.Add(new PhysicalIndexColumnDefinition(
                "priority",
                compoundColumns.Count,
                compoundPaging == QueryPagingSupport.Cursor
                    ? PhysicalSortDirection.Descending
                    : PhysicalSortDirection.Ascending));
            if (compoundPaging == QueryPagingSupport.Cursor)
            {
                compoundColumns.Add(new PhysicalIndexColumnDefinition(
                    new DocumentEnvelopeDefinition().IdLookupKeyColumn,
                    compoundColumns.Count));
            }
            indexes.Add(new PhysicalIndexDefinition("by-category-priority", compoundColumns));
        }

        var binding = new SharedStorageBinding("runtime-documents");
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.String,
            categoryUnique,
            MissingValueBehavior.Excluded);
        var boundedQuery = new BoundedQueryDeclaration(
            "list-by-category",
            logicalIndex.Identity,
            categoryOperations ?? new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            categoryPaging,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count,
                BoundedQueryResultOperation.Any,
                BoundedQueryResultOperation.First
            });
        var logicalIndexes = new List<LogicalIndexDeclaration> { logicalIndex };
        var boundedQueries = new List<BoundedQueryDeclaration> { boundedQuery };
        if (includePriority)
        {
            var compound = new LogicalIndexDeclaration(
                "by-category-priority",
                [new IndexField("category"), new IndexField("priority", ToIndexValueKind(priorityType))],
                IndexValueKind.String,
                false,
                MissingValueBehavior.Excluded);
            logicalIndexes.Add(compound);
            boundedQueries.Add(compoundPaging == QueryPagingSupport.Cursor
                ? new BoundedQueryDeclaration(
                    "find-by-category-priority",
                    compound.Identity,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Descending,
                    QueryPagingSupport.Cursor,
                    BoundedQueryExecutionClass.ScaleBearing,
                    supportsTotalCount: true,
                    sortFields:
                    [
                        new BoundedQuerySortField("priority", PhysicalSortDirection.Descending)
                    ],
                    predicateFields:
                    [
                        new BoundedQueryPredicateField(
                            "category",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                    ],
                    resultOperations: new HashSet<BoundedQueryResultOperation>
                    {
                        BoundedQueryResultOperation.Documents,
                        BoundedQueryResultOperation.Count
                    })
                : new BoundedQueryDeclaration(
                    "find-by-category-priority",
                    compound.Identity,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.None,
                    BoundedQueryExecutionClass.ScaleBearing,
                    supportsTotalCount: true,
                    predicateFields:
                    [
                        new BoundedQueryPredicateField("category", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                        new BoundedQueryPredicateField("priority", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                    ],
                    resultOperations: new HashSet<BoundedQueryResultOperation>
                    {
                        BoundedQueryResultOperation.Documents,
                        BoundedQueryResultOperation.Count
                    }));
            if (includeLatestPerCategory)
            {
                boundedQueries.Add(new BoundedQueryDeclaration(
                    "latest-by-category",
                    compound.Identity,
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal
                    },
                    QuerySortSupport.Both,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing,
                    supportsTotalCount: true,
                    sortFields:
                    [
                        new BoundedQuerySortField("category", PhysicalSortDirection.Ascending),
                        new BoundedQuerySortField("priority", PhysicalSortDirection.Ascending)
                    ],
                    predicateFields:
                    [
                        new BoundedQueryPredicateField(
                            "category",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                    ],
                    resultOperations: new HashSet<BoundedQueryResultOperation>
                    {
                        BoundedQueryResultOperation.Documents,
                        BoundedQueryResultOperation.Count,
                        BoundedQueryResultOperation.Any,
                        BoundedQueryResultOperation.First
                    },
                    latestPerKeyPath: "category",
                    residualPredicateFields:
                    [
                        new BoundedQueryResidualPredicateField(
                            "visible",
                            IndexValueKind.Boolean,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            isRequired: true)
                    ]));
            }
        }
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding, columns, indexes, linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "configuration_documents", indexes: indexes, linkedProjectedColumns: columns,
                linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "configuration_entities", columns, indexes: indexes),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: stringCasePolicy),
                    Tenancy = scoped ? TenancyPolicy.Scoped : TenancyPolicy.Global,
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition),
                        logicalIndexes,
                        boundedQueries)
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            normalizer ?? ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, SqliteTestManifests.Provider, compilation.Routes));
    }

    private static string CompatiblePreexistingTableSql(ExecutableStorageRoute route, string quoting)
    {
        var envelope = route.Envelope;
        var category = route.ProjectedColumns.Single(column => column.Definition.LogicalName == "category");
        var primaryKey = string.Join(", ", route.PrimaryKey.Columns.Select(column => QuoteTable(column.Identifier, quoting)));
        return $"""
            CREATE TABLE {QuoteTable(route.PrimaryStorage.Name.Identifier, quoting)} (
              {QuoteTable(envelope.DocumentKind.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.StorageScope.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.Identity.OriginalId.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.Identity.ComparisonKey.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.Identity.LookupKey.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.SchemaVersion.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(envelope.Version.Identifier, quoting)} INTEGER NOT NULL,
              {QuoteTable(envelope.CanonicalJson.Identifier, quoting)} TEXT NOT NULL,
              {QuoteTable(RelationalPhysicalStorageColumns.CreatedUtc, quoting)} TEXT NOT NULL,
              {QuoteTable(RelationalPhysicalStorageColumns.UpdatedUtc, quoting)} TEXT NOT NULL,
              {QuoteTable(category.Column.Identifier, quoting)} TEXT NULL, -- ignore top-level tokens: ),(
              {Quote("auxiliary,column")} TEXT DEFAULT ('value,(') /* ignore top-level tokens: ),( */,
              CONSTRAINT {Quote("check \"nested,expression\"")} CHECK (
                json_valid({QuoteTable(envelope.CanonicalJson.Identifier, quoting)}) AND
                (instr(json_extract({QuoteTable(envelope.CanonicalJson.Identifier, quoting)}, '$."category"'), ',') >= 0 OR
                 length({QuoteTable(envelope.CanonicalJson.Identifier, quoting)}) >= 0)),
              PRIMARY KEY ({primaryKey})
            );
            """;
    }

    private static string QuoteTable(string value, string quoting) => quoting switch
    {
        "double" => Quote(value),
        "bracket" => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]",
        "backtick" => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`",
        "unquoted" => value,
        _ => throw new ArgumentOutOfRangeException(nameof(quoting), quoting, null)
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static IndexValueKind ToIndexValueKind(PortablePhysicalType type) => type switch
    {
        PortablePhysicalType.String => IndexValueKind.String,
        PortablePhysicalType.Int32 or PortablePhysicalType.Int64 or PortablePhysicalType.Decimal => IndexValueKind.Number,
        PortablePhysicalType.Boolean => IndexValueKind.Boolean,
        PortablePhysicalType.DateTime => IndexValueKind.DateTime,
        PortablePhysicalType.Guid or PortablePhysicalType.Json or PortablePhysicalType.Binary => IndexValueKind.Keyword,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static IProviderPhysicalNameNormalizer SameTableAndPriorityName(PhysicalStorageForm form)
    {
        var storageKind = form == PhysicalStorageForm.PhysicalEntityTable
            ? PhysicalObjectKind.PrimaryStorage
            : PhysicalObjectKind.LinkedIndexStorage;
        var projectionKind = form == PhysicalStorageForm.PhysicalEntityTable
            ? PhysicalObjectKind.ProjectedField
            : PhysicalObjectKind.LinkedProjectedField;
        return new DelegateProviderPhysicalNameNormalizer(context =>
            context.ObjectKind == storageKind ||
            context.ObjectKind == projectionKind && context.LogicalName == "priority"
                ? "table_and_priority"
                : context.LogicalName);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;";
        command.Parameters.AddWithValue("@name", index);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<IReadOnlySet<string>> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<long> ColumnNotNullAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == column)
                return reader.GetInt64(3);
        }
        throw new InvalidOperationException($"Column '{table}.{column}' is missing.");
    }
}
