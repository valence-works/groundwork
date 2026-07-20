using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.Core.Manifests;
using Groundwork.Provider.Relational;
using Groundwork.Sqlite.DiagnosticRecords;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteDiagnosticRecordTestCollection
{
    public const string Name = "SQLite diagnostic records";
}

[Collection(SqliteDiagnosticRecordTestCollection.Name)]
public sealed class SqliteDiagnosticRecordStoreTests : RelationalDiagnosticRecordStoreConformanceTests
{
    protected override IRelationalDiagnosticRecordStoreConformanceFixture CreateRelationalFixture() =>
        new SqliteDiagnosticRecordStoreFixture();
}

[Collection(SqliteDiagnosticRecordTestCollection.Name)]
public sealed class SqliteDiagnosticRecordMaterializerTests
{
    [Fact]
    public async Task Session_factory_rejects_a_missing_file_without_creating_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var deployment = Deployment(SqliteDiagnosticRecordStoreFixture.Definition);
        var factory = SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(ConnectionString(path));

        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await factory.OpenAsync(deployment, new("tenant-a", "shell-a")));

            Assert.Equal("GW-DIAG-DEPLOY-001", exception.Code);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_definition_drift_without_repairing_the_persisted_definition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var persisted = SqliteDiagnosticRecordStoreFixture.Definition;
        var changed = persisted with { SchemaVersion = persisted.SchemaVersion + 1 };
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, persisted);
        var before = await ReadDefinitionFingerprintAsync(connectionString, persisted.Stream);

        try
        {
            var factory = SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString);
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await factory.OpenAsync(Deployment(changed), new("tenant-a", "shell-a")));

            Assert.Equal("GW-DIAG-DEPLOY-002", exception.Code);
            Assert.Equal(before, await ReadDefinitionFingerprintAsync(connectionString, persisted.Stream));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_algorithm_state_drift_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var drift = connection.CreateCommand();
                drift.CommandText = $"UPDATE {RelationalDiagnosticRecordSchema.DefinitionsTable} SET algorithm_manifest_fingerprint = 'drifted' WHERE stream_id = @stream;";
                drift.Parameters.AddWithValue("@stream", definition.Stream.Value);
                await drift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            Assert.Equal("drifted", await ReadDefinitionStateAsync(connectionString, definition.Stream, "algorithm_manifest_fingerprint"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_wrong_index_shape_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor; CREATE INDEX ix_groundwork_diagnostic_records_scope_cursor ON groundwork_diagnostic_records (stream_id, scope_id, tenant_id, cursor);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync();
            await using var read = verify.CreateCommand();
            read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.Contains("(stream_id, scope_id, tenant_id, cursor)", Assert.IsType<string>(await read.ExecuteScalarAsync()), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_rejects_a_same_name_descending_index_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor; CREATE INDEX ix_groundwork_diagnostic_records_scope_cursor ON groundwork_diagnostic_records (tenant_id, scope_id, stream_id, cursor DESC);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_classifies_a_same_name_view_as_drift_not_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText = $"DROP TABLE {RelationalDiagnosticRecordSchema.DefinitionsTable}; CREATE VIEW {RelationalDiagnosticRecordSchema.DefinitionsTable} AS SELECT 'stream' AS stream_id;";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(definition), new("tenant-a", "shell-a")));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Session_factory_opens_a_ready_deployment_and_concurrent_callers_only_inspect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);
        var factory = SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString);

        try
        {
            var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                await factory.OpenAsync(Deployment(definition), new("tenant-a", "shell-a"))));
            foreach (var session in sessions)
            {
                Assert.Equal(new DiagnosticStorageScope("tenant-a", "shell-a"), session.Scope);
                await session.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task First_store_open_reinspects_and_does_not_recreate_schema_removed_after_session_admission()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-runtime-admission-{Guid.NewGuid():N}.db");
        var connectionString = ConnectionString(path);
        var definition = SqliteDiagnosticRecordStoreFixture.Definition;
        await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition);
        var factory = SqliteDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString);

        try
        {
            await using var session = await factory.OpenAsync(
                Deployment(definition),
                new("tenant-a", "shell-a"));
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var drop = connection.CreateCommand();
                drop.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor;";
                await drop.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await session.OpenStoreAsync(definition.Stream));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync();
            await using var read = verify.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.Equal(0L, await read.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_admission_runs_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var admission = new RelationalDiagnosticRecordAdmission(async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });

        var callers = Enumerable.Range(0, 8)
            .Select(_ => admission.EnsureAsync(CancellationToken.None).AsTask())
            .ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();

        await Task.WhenAll(callers);
        await admission.EnsureAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failed_admission_is_retryable()
    {
        var calls = 0;
        var admission = new RelationalDiagnosticRecordAdmission(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admission.EnsureAsync(CancellationToken.None).AsTask());
        await admission.EnsureAsync(CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Canceled_admission_is_retryable_by_another_caller()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new RelationalDiagnosticRecordAdmission(async cancellationToken =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });
        using var canceled = new CancellationTokenSource();
        var first = admission.EnsureAsync(canceled.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await admission.EnsureAsync(CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Store_uses_the_shared_diagnostic_record_instrumentation()
    {
        var store = new SqliteDiagnosticRecordStore(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"groundwork-wiring-{Guid.NewGuid():N}.db") }.ToString(),
            SqliteDiagnosticRecordStoreFixture.Definition);

        await DiagnosticRecordInstrumentationAssertions.AssertProviderRoutesAsync(
            store,
            "sqlite",
            new(
                request => store.AppendAsync(request),
                request => store.QueryAsync(request),
                request => store.InspectAsync(request),
                request => store.TrimAsync(request)));
    }

    [Fact]
    public void Portable_occurrence_order_uses_native_ticks_without_a_text_cast()
    {
        var store = new SqliteDiagnosticRecordStore(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"groundwork-sql-{Guid.NewGuid():N}.db") }.ToString(),
            SqliteDiagnosticRecordStoreFixture.Definition);
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            SqliteDiagnosticRecordStoreFixture.Definition.Stream,
            10,
            new(DiagnosticRecordFieldNames.OccurredAt));

        var sql = store.Inner.BuildQueryCommand(query, 10).CommandText;

        Assert.DoesNotContain("CAST", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r.occurred_at_ticks AS order_key", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Latest_per_key_sql_uses_the_bounded_latest_index_and_keeps_the_snapshot_sargable()
    {
        var store = new SqliteDiagnosticRecordStore(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"groundwork-sql-{Guid.NewGuid():N}.db") }.ToString(),
            SqliteDiagnosticRecordStoreFixture.Definition);
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "shell-a"),
            SqliteDiagnosticRecordStoreFixture.Definition.Stream,
            10,
            LatestPerKeyField: "service");

        var sql = store.Inner.BuildQueryCommand(query, 10).CommandText;

        Assert.DoesNotContain("CROSS JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INDEXED BY ix_groundwork_diagnostic_fields_scope_latest", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cursor + 0", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lfield.cursor <= @snapshot", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("groundwork_diagnostic_records lr", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(42L)]
    public void Relational_count_conversion_accepts_provider_int32_and_int64(object providerValue)
    {
        Assert.Equal(42, RelationalDiagnosticValueConversions.ToInt64(providerValue));
    }

    [Fact]
    public async Task Independent_public_stores_racing_the_same_append_commit_once_and_replay_once()
    {
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBeforeBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBeginReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowSecondBegin = new ManualResetEventSlim();
        var (path, first, second) = await CreateIndependentStoresAsync(async (point, cancellationToken) =>
        {
            if (point != RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit)
                return;
            firstStaged.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        }, new(
            () =>
            {
                secondBeforeBegin.TrySetResult();
                allowSecondBegin.Wait();
            },
            () => secondBeginReturned.TrySetResult()));
        try
        {
            var now = TimeProvider.System.GetUtcNow();
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var operationId = new DiagnosticOperationId(now, "public-same-fingerprint-race");
            var batch = DiagnosticRecordBatch.Create(
                scope,
                SqliteDiagnosticRecordStoreFixture.Definition.Stream,
                operationId,
                [new("record-1", now, "{}")]);
            var firstAppend = Task.Run(async () => await first.AppendAsync(batch));
            await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondAppend = Task.Run(async () => await second.AppendAsync(batch));

            await secondBeforeBegin.Task.WaitAsync(TimeSpan.FromSeconds(5));
            allowSecondBegin.Set();
            await AssertBeginBlockedAsync(secondBeginReturned.Task);
            releaseFirst.TrySetResult();
            var committed = await firstAppend;
            await secondBeginReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replayed = await secondAppend;
            var page = await second.QueryAsync(new(scope, batch.Stream, 10));

            Assert.Equal(DiagnosticAppendStatus.Committed, committed.Status);
            Assert.Equal(DiagnosticAppendStatus.Replayed, replayed.Status);
            Assert.Equal("1", Assert.Single(committed.Records).Cursor.Value);
            Assert.Equal("1", Assert.Single(replayed.Records).Cursor.Value);
            Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
        }
        finally
        {
            releaseFirst.TrySetResult();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Independent_public_stores_racing_different_fingerprints_commit_once_and_conflict_once()
    {
        var firstStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBeforeBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBeginReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowSecondBegin = new ManualResetEventSlim();
        var (path, first, second) = await CreateIndependentStoresAsync(async (point, cancellationToken) =>
        {
            if (point != RelationalDiagnosticRecordExecutionPoint.AppendAfterRecordStagedBeforeCommit)
                return;
            firstStaged.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        }, new(
            () =>
            {
                secondBeforeBegin.TrySetResult();
                allowSecondBegin.Wait();
            },
            () => secondBeginReturned.TrySetResult()));
        try
        {
            var now = TimeProvider.System.GetUtcNow();
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var operationId = new DiagnosticOperationId(now, "public-different-fingerprint-race");
            var firstBatch = DiagnosticRecordBatch.Create(scope, SqliteDiagnosticRecordStoreFixture.Definition.Stream, operationId, [new("record-a", now, "{}")]);
            var secondBatch = DiagnosticRecordBatch.Create(scope, SqliteDiagnosticRecordStoreFixture.Definition.Stream, operationId, [new("record-b", now, "{}")]);
            var firstAppend = Task.Run(async () => await first.AppendAsync(firstBatch));
            await firstStaged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondAppend = Task.Run(async () => await second.AppendAsync(secondBatch));

            await secondBeforeBegin.Task.WaitAsync(TimeSpan.FromSeconds(5));
            allowSecondBegin.Set();
            await AssertBeginBlockedAsync(secondBeginReturned.Task);
            releaseFirst.TrySetResult();
            var committed = await firstAppend;
            await secondBeginReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<DiagnosticOperationConflictException>(async () => await secondAppend);
            var page = await second.QueryAsync(new(scope, firstBatch.Stream, 10));

            Assert.Equal(DiagnosticAppendStatus.Committed, committed.Status);
            Assert.Equal("1", Assert.Single(committed.Records).Cursor.Value);
            Assert.Equal("1", Assert.Single(page.Records).Cursor.Value);
            Assert.Equal("record-a", Assert.Single(page.Records).RecordId);
        }
        finally
        {
            releaseFirst.TrySetResult();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_public_factories_materialize_one_file_idempotently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        try
        {
            var stores = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                SqliteDiagnosticRecordStoreFactory.CreateAsync(connectionString, SqliteDiagnosticRecordStoreFixture.Definition)));

            Assert.Equal(8, stores.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Materializer_creates_durable_records_fields_stream_and_operation_ledgers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        try
        {
            await SqliteDiagnosticRecordStoreFactory.CreateAsync(connectionString, SqliteDiagnosticRecordStoreFixture.Definition);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'groundwork_diagnostic_%' ORDER BY name;";
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));

            Assert.Equal(
                [
                    "groundwork_diagnostic_append_operations",
                    "groundwork_diagnostic_definitions",
                    "groundwork_diagnostic_fields",
                    "groundwork_diagnostic_provider_state",
                    "groundwork_diagnostic_records",
                    "groundwork_diagnostic_streams",
                    "groundwork_diagnostic_trim_operations"
                ],
                names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Materializer_declares_comparison_keys_with_binary_text_semantics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        try
        {
            await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'groundwork_diagnostic_fields';";

            var ddl = Assert.IsType<string>(await command.ExecuteScalarAsync());

            Assert.Contains("comparison_key TEXT COLLATE BINARY NOT NULL", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("comparison_key_prefix TEXT COLLATE BINARY NOT NULL", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("comparison_key_hash TEXT COLLATE BINARY NOT NULL", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("search_key TEXT COLLATE BINARY NOT NULL", ddl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Portable_schema_indexes_only_bounded_string_comparison_keys()
    {
        var fieldIndexes = RelationalDiagnosticRecordSchema.Standard.Indexes
            .Where(index => index.Table == RelationalDiagnosticRecordSchema.FieldsTable)
            .ToArray();

        Assert.NotEmpty(fieldIndexes);
        Assert.All(fieldIndexes, index =>
        {
            Assert.DoesNotContain("comparison_key", index.Columns);
            Assert.DoesNotContain("search_key", index.Columns);
        });
        Assert.Contains(fieldIndexes, index => index.Columns.Contains("comparison_key_prefix"));
        Assert.Contains(fieldIndexes, index => index.Columns.Contains("comparison_key_hash"));
    }

    [Fact]
    public async Task Factory_persists_definition_and_algorithm_state_and_rejects_drift()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        try
        {
            var definition = SqliteDiagnosticRecordStoreFixture.Definition with
            {
                Fields =
                [
                    SqliteDiagnosticRecordStoreFixture.Definition.Fields[0] with
                    {
                        CasePolicy = DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase,
                        MaxStringBytes = 8_192
                    }
                ],
                Limits = SqliteDiagnosticRecordStoreFixture.Definition.Limits with { MaxPredicateValues = 16 }
            };
            await SqliteDiagnosticRecordStoreFactory.CreateAsync(connectionString, definition);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT definition_fingerprint, algorithm_manifest, algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
            command.Parameters.AddWithValue("@stream", definition.Stream.Value);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(64, reader.GetString(0).Length);
            Assert.Contains(DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId, reader.GetString(1), StringComparison.Ordinal);
            Assert.Equal(64, reader.GetString(2).Length);
            await reader.DisposeAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteDiagnosticRecordStoreFactory.CreateAsync(connectionString, definition with { SchemaVersion = definition.SchemaVersion + 1 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Every_direct_public_store_operation_rejects_incompatible_persisted_state()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        try
        {
            var persisted = SqliteDiagnosticRecordStoreFixture.Definition;
            await SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, persisted);
            var incompatible = persisted with { SchemaVersion = persisted.SchemaVersion + 1 };
            var scope = new DiagnosticStorageScope("tenant-a", "shell-a");
            var now = TimeProvider.System.GetUtcNow();
            var append = DiagnosticRecordBatch.Create(
                scope,
                incompatible.Stream,
                new(now, "drift-append"),
                [new("record-1", now, "{}")]);
            var trim = DiagnosticTrimRequest.Create(
                scope,
                incompatible.Stream,
                new(now, "drift-trim"),
                0);
            Func<SqliteDiagnosticRecordStore, Task>[] operations =
            [
                async store => _ = await store.AppendAsync(append),
                async store => _ = await store.QueryAsync(new(scope, incompatible.Stream, 10)),
                async store => _ = await store.InspectAsync(new(scope, incompatible.Stream)),
                async store => _ = await store.TrimAsync(trim)
            ];

            foreach (var operation in operations)
            {
                var store = new SqliteDiagnosticRecordStore(connectionString, incompatible);
                await Assert.ThrowsAsync<InvalidOperationException>(() => operation(store));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Provider_string_bound_is_rejected_before_sqlite_file_io()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var oversized = SqliteDiagnosticRecordStoreFixture.Definition with
        {
            Fields =
            [
                SqliteDiagnosticRecordStoreFixture.Definition.Fields[0] with
                {
                    MaxStringBytes = 65_537
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<DiagnosticRecordValidationException>(() =>
            SqliteDiagnosticRecordMaterializer.MaterializeAsync(
                new SqliteConnectionStringBuilder { DataSource = path }.ToString(),
                oversized));

        Assert.Contains(exception.Errors, error => error.Code == "provider.sqlite.string_bound.too_large");
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    public async Task Stateless_factory_rejects_connection_scoped_in_memory_databases(string connectionString)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDiagnosticRecordStoreFactory.CreateAsync(connectionString, SqliteDiagnosticRecordStoreFixture.Definition));
    }

    [Fact]
    public async Task Materializer_rejects_unsafe_portable_schema_identifiers_before_generating_ddl()
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var schema = RelationalDiagnosticRecordSchema.Standard with
        {
            Tables =
            [
                new(
                    "unsafe; DROP TABLE groundwork_diagnostic_records;--",
                    [new("id", RelationalDiagnosticColumnType.Int64)],
                    ["id"])
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SqliteDiagnosticRecordMaterializer.MaterializeAsync(
                new SqliteConnectionStringBuilder { DataSource = path }.ToString(),
                schema));

        Assert.False(File.Exists(path));
    }

    private static DiagnosticRecordDeploymentManifest Deployment(DiagnosticRecordStreamDefinition definition) => new(
        new StorageManifest(
            new("diagnostic-runtime-admission-tests"),
            new("tests"),
            new("1"),
            [],
            new HashSet<string>(),
            []),
        [definition]);

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    private static async Task<string?> ReadDefinitionFingerprintAsync(
        string connectionString,
        DiagnosticStreamId stream)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        command.Parameters.AddWithValue("@stream", stream.Value);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> ReadDefinitionStateAsync(
        string connectionString,
        DiagnosticStreamId stream,
        string column)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        command.Parameters.AddWithValue("@stream", stream.Value);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<(string Path, SqliteDiagnosticRecordStore First, SqliteDiagnosticRecordStore Second)> CreateIndependentStoresAsync(
        Func<RelationalDiagnosticRecordExecutionPoint, CancellationToken, ValueTask>? firstInterceptor = null,
        SqliteImmediateTransactionObserver? secondTransactionObserver = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        var first = await SqliteDiagnosticRecordStoreFactory.CreateAsync(
            connectionString,
            SqliteDiagnosticRecordStoreFixture.Definition,
            TimeProvider.System,
            firstInterceptor);
        var second = await SqliteDiagnosticRecordStoreFactory.CreateAsync(
            connectionString,
            SqliteDiagnosticRecordStoreFixture.Definition,
            TimeProvider.System,
            null,
            secondTransactionObserver);
        return (path, first, second);
    }

    private static async Task AssertBeginBlockedAsync(Task beginReturned)
    {
        var timeout = Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.Same(timeout, await Task.WhenAny(beginReturned, timeout));
    }
}

internal sealed class SqliteDiagnosticRecordStoreFixture : IRelationalDiagnosticRecordStoreConformanceFixture
{
    internal static DiagnosticRecordStreamDefinition Definition { get; } = new(
        new("logs"),
        1,
        "diagnostic_logs",
        [
            new(
                "service",
                DiagnosticFieldType.String,
                DiagnosticFieldCardinality.Scalar,
                new HashSet<DiagnosticPredicateOperator>
                {
                    DiagnosticPredicateOperator.Equal,
                    DiagnosticPredicateOperator.In,
                    DiagnosticPredicateOperator.Contains
                },
                IsOrderable: true,
                SupportsLatestPerKey: true,
                CasePolicy: DiagnosticStringCasePolicy.AsciiIgnoreCase,
                MaxStringBytes: 64)
        ],
        new(),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(1));

    private readonly string path = Path.Combine(Path.GetTempPath(), $"groundwork-diagnostics-{Guid.NewGuid():N}.db");
    private readonly string connectionString;
    private readonly RelationalSessionFactory readSessions;
    private readonly RelationalSessionFactory writeSessions;
    private readonly ManualSqliteTimeProvider timeProvider = new(TimeProvider.System.GetUtcNow());
    private readonly Dictionary<DiagnosticExecutionPoint, Queue<Func<CancellationToken, ValueTask>>> interceptors = [];

    public SqliteDiagnosticRecordStoreFixture()
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        readSessions = SqliteRelationalSessions.CreateSerializedDeferred(connectionString);
        writeSessions = SqliteRelationalSessions.CreateSerializedImmediate(connectionString);
        SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString).GetAwaiter().GetResult();
    }

    public IDiagnosticRecordStore OpenStore(DiagnosticRecordStreamDefinition definition)
    {
        SqliteDiagnosticRecordMaterializer.MaterializeAsync(connectionString, definition).GetAwaiter().GetResult();
        return new SqliteDiagnosticRecordStore(readSessions, writeSessions, definition, timeProvider, InterceptAsync);
    }

    public IDiagnosticRecordStore OpenIndependentStore(DiagnosticRecordStreamDefinition definition)
        => new SqliteDiagnosticRecordStore(connectionString, definition, timeProvider);

    public string FieldsPrimaryAccessPath => "sqlite_autoindex_groundwork_diagnostic_fields_1";

    public void InterceptNext(DiagnosticExecutionPoint point, Func<CancellationToken, ValueTask> interceptor)
    {
        lock (interceptors)
        {
            if (!interceptors.TryGetValue(point, out var queue))
                interceptors[point] = queue = [];
            queue.Enqueue(interceptor);
        }
    }

    public DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();
    public void AdvanceTime(TimeSpan duration) => timeProvider.Advance(duration);
    public void SetWallClock(DateTimeOffset utcNow) => timeProvider.Set(utcNow);

    public ValueTask<IReadOnlyList<string>> ExplainQueryAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        SqliteDiagnosticRecordStoreFactory.ExplainQueryAsync(connectionString, definition, query, cancellationToken);

    public ValueTask<IReadOnlyList<string>> ExplainTrimAsync(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        SqliteDiagnosticRecordStoreFactory.ExplainTrimAsync(connectionString, definition, request, cancellationToken);

    public bool UsesSeek(
        IReadOnlyList<string> plan,
        string accessPath,
        IReadOnlyList<string> constrainedColumns) =>
        plan.Any(line =>
            line.Contains("SEARCH", StringComparison.Ordinal) &&
            line.Contains(accessPath, StringComparison.Ordinal) &&
            constrainedColumns.All(column =>
                line.Contains($"{column}=?", StringComparison.Ordinal) ||
                line.Contains($"{column}<?", StringComparison.Ordinal) ||
                line.Contains($"{column}>?", StringComparison.Ordinal)));

    public ValueTask<IReadOnlyList<string>> ReadComparisonKeysAsync(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        string field,
        CancellationToken cancellationToken = default) =>
        SqliteDiagnosticRecordStoreFactory.ReadComparisonKeysAsync(connectionString, scope, stream, field, cancellationToken);

    public ValueTask<long> CountOperationRowsAsync(
        DiagnosticOperationKind kind,
        CancellationToken cancellationToken = default) =>
        SqliteDiagnosticRecordStoreFactory.CountOperationRowsAsync(connectionString, kind, cancellationToken);

    private ValueTask InterceptAsync(RelationalDiagnosticRecordExecutionPoint point, CancellationToken cancellationToken)
    {
        var conformancePoint = Enum.Parse<DiagnosticExecutionPoint>(point.ToString());
        Func<CancellationToken, ValueTask>? interceptor = null;
        lock (interceptors)
        {
            if (interceptors.TryGetValue(conformancePoint, out var queue) && queue.Count > 0)
                interceptor = queue.Dequeue();
        }

        return interceptor?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    private sealed class ManualSqliteTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
        public void Set(DateTimeOffset value) => current = value;
    }
}
