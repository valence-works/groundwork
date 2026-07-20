using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Relational;
using Groundwork.PostgreSql.DiagnosticRecords;
using Groundwork.SqlServer.DiagnosticRecords;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[Collection(PostgreSqlDiagnosticRecordCollection.Name)]
public sealed class PostgreSqlDiagnosticRecordRuntimeAdmissionTests(PostgreSqlDiagnosticContainer container)
{
    [Fact]
    public void Unusable_normalized_index_and_primary_key_states_are_drift()
    {
        var table = new RelationalDiagnosticTableSnapshot(
            "records",
            [new("id", "int8", false, null)],
            ["id"],
            "table",
            "btree",
            true);
        var index = new RelationalDiagnosticIndexSnapshot(
            "ix_records",
            "records",
            [new("id", false)],
            false,
            null,
            [],
            "btree",
            true);

        var disabledIndex = RelationalDiagnosticRecordDeploymentInspector.ClassifyPhysical(
            "postgresql",
            Deployment(),
            [table],
            [index],
            [table],
            [index with { IsUsable = false }]);
        var unusablePrimaryKey = RelationalDiagnosticRecordDeploymentInspector.ClassifyPhysical(
            "postgresql",
            Deployment(),
            [table],
            [index],
            [table with { IsPrimaryKeyUsable = false }],
            [index]);

        Assert.Equal(DiagnosticRecordDeploymentAdmissionStatus.Drifted, disabledIndex.Status);
        Assert.Equal(DiagnosticRecordDeploymentAdmissionStatus.Drifted, unusablePrimaryKey.Status);
    }

    [Fact]
    public async Task Missing_schema_is_rejected_without_materializing_it()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename = 'groundwork_diagnostic_definitions';";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task Drift_is_rejected_without_repairing_the_persisted_fingerprint()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = $"UPDATE {RelationalDiagnosticRecordSchema.DefinitionsTable} SET definition_fingerprint = 'drifted' WHERE stream_id = @stream;";
                drift.Parameters.AddWithValue("stream", Definition().Stream.Value);
                await drift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
            read.Parameters.AddWithValue("stream", Definition().Stream.Value);
            Assert.Equal("drifted", await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task Algorithm_state_drift_is_rejected_without_repair()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = $"UPDATE {RelationalDiagnosticRecordSchema.DefinitionsTable} SET algorithm_manifest_fingerprint = 'drifted' WHERE stream_id = @stream;";
                drift.Parameters.AddWithValue("stream", Definition().Stream.Value);
                await drift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
            read.Parameters.AddWithValue("stream", Definition().Stream.Value);
            Assert.Equal("drifted", await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task Wrong_index_shape_is_rejected_without_repair()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor; CREATE INDEX ix_groundwork_diagnostic_records_scope_cursor ON groundwork_diagnostic_records (stream_id, scope_id, tenant_id, cursor);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() AND indexname = 'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.Contains("(stream_id, scope_id, tenant_id, cursor)", Assert.IsType<string>(await read.ExecuteScalarAsync()), StringComparison.Ordinal);
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task Descending_index_key_is_rejected_without_repair()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor; CREATE INDEX ix_groundwork_diagnostic_records_scope_cursor ON groundwork_diagnostic_records (tenant_id, scope_id, stream_id, cursor DESC);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task Ready_deployment_opens_a_session()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var session = await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                .OpenAsync(Deployment(), Scope);

            Assert.Equal(Scope, session.Scope);
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    [Fact]
    public async Task First_store_open_reinspects_and_does_not_recreate_schema_removed_after_session_admission()
    {
        var connectionString = await container.CreateSchemaAsync();
        try
        {
            await PostgreSqlDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var session = await PostgreSqlDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                .OpenAsync(Deployment(), Scope);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP INDEX ix_groundwork_diagnostic_records_scope_cursor;";
                await drop.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await session.OpenStoreAsync(Definition().Stream));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname = 'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.Equal(0L, await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropSchemaAsync(connectionString);
        }
    }

    private static readonly DiagnosticStorageScope Scope = new("tenant", "shell");

    private static DiagnosticRecordDeploymentManifest Deployment() =>
        new(RelationalTestManifests.MetadataManifest(), [Definition()]);

    private static DiagnosticRecordStreamDefinition Definition() => new(
        new("runtime-admission"),
        1,
        "runtime_admission",
        [new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
            new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal }, MaxStringBytes: 128)],
        new(MaxRecordIdBytes: SqlServerDiagnosticRecordValidator.MaxRecordIdBytes),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(10));
}

[Collection(SqlServerDiagnosticRecordCollection.Name)]
public sealed class SqlServerDiagnosticRecordRuntimeAdmissionTests(SqlServerDiagnosticContainer container)
{
    [Fact]
    public async Task Missing_schema_is_rejected_without_materializing_it()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = N'groundwork_diagnostic_definitions';";
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Drift_is_rejected_without_repairing_the_persisted_fingerprint()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = $"UPDATE {RelationalDiagnosticRecordSchema.DefinitionsTable} SET definition_fingerprint = 'drifted' WHERE stream_id = @stream;";
                drift.Parameters.AddWithValue("stream", Definition().Stream.Value);
                await drift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT definition_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
            read.Parameters.AddWithValue("stream", Definition().Stream.Value);
            Assert.Equal("drifted", await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Algorithm_state_drift_is_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = $"UPDATE {RelationalDiagnosticRecordSchema.DefinitionsTable} SET algorithm_manifest_fingerprint = 'drifted' WHERE stream_id = @stream;";
                drift.Parameters.AddWithValue("stream", Definition().Stream.Value);
                await drift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = $"SELECT algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
            read.Parameters.AddWithValue("stream", Definition().Stream.Value);
            Assert.Equal("drifted", await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Wrong_index_shape_is_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "DROP INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records; CREATE INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records ([stream_id], [scope_id], [tenant_id], [cursor]);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT STRING_AGG(column_ref.name, ',') WITHIN GROUP (ORDER BY index_column.key_ordinal) FROM sys.indexes AS index_ref JOIN sys.index_columns AS index_column ON index_column.object_id = index_ref.object_id AND index_column.index_id = index_ref.index_id JOIN sys.columns AS column_ref ON column_ref.object_id = index_column.object_id AND column_ref.column_id = index_column.column_id WHERE index_ref.name = N'ix_groundwork_diagnostic_records_scope_cursor' AND index_column.key_ordinal > 0;";
            Assert.Equal("stream_id,scope_id,tenant_id,cursor", await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Included_columns_on_a_same_name_index_are_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "DROP INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records; CREATE INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records ([tenant_id], [scope_id], [stream_id], [cursor]) INCLUDE ([payload_json]);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Disabled_secondary_index_is_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "ALTER INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records DISABLE;";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT is_disabled FROM sys.indexes WHERE object_id = OBJECT_ID(N'groundwork_diagnostic_records') AND name = N'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.True(Assert.IsType<bool>(await read.ExecuteScalarAsync()));
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Clustered_primary_key_is_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "ALTER TABLE groundwork_diagnostic_records DROP CONSTRAINT [pk_groundwork_diagnostic_records]; ALTER TABLE groundwork_diagnostic_records ADD CONSTRAINT [pk_groundwork_diagnostic_records] PRIMARY KEY CLUSTERED ([tenant_id], [scope_id], [stream_id], [cursor]);";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Disabled_primary_key_backing_index_is_rejected_without_repair()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "ALTER INDEX [pk_groundwork_diagnostic_records] ON groundwork_diagnostic_records DISABLE;";
                await corrupt.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT is_disabled FROM sys.indexes WHERE object_id = OBJECT_ID(N'groundwork_diagnostic_records') AND name = N'pk_groundwork_diagnostic_records';";
            Assert.True(Assert.IsType<bool>(await read.ExecuteScalarAsync()));
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task An_unrelated_table_may_reuse_a_diagnostic_index_name()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var unrelated = connection.CreateCommand())
            {
                unrelated.CommandText = "CREATE TABLE unrelated_diagnostic_index_name (id INT NOT NULL); CREATE INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON unrelated_diagnostic_index_name ([id]);";
                await unrelated.ExecuteNonQueryAsync();
            }

            await using var session = await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                .OpenAsync(Deployment(), Scope);
            Assert.Equal(Scope, session.Scope);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Ready_deployment_opens_a_session()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var session = await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                .OpenAsync(Deployment(), Scope);

            Assert.Equal(Scope, session.Scope);
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task First_store_open_reinspects_and_does_not_recreate_schema_removed_after_session_admission()
    {
        var connectionString = await container.CreateDatabaseAsync();
        try
        {
            await SqlServerDiagnosticRecordMaterializer.MaterializeAsync(connectionString, Definition());
            await using var session = await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                .OpenAsync(Deployment(), Scope);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP INDEX [ix_groundwork_diagnostic_records_scope_cursor] ON groundwork_diagnostic_records;";
                await drop.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await session.OpenStoreAsync(Definition().Stream));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.Missing, exception.Code);
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = N'ix_groundwork_diagnostic_records_scope_cursor';";
            Assert.Equal(0, await read.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Missing_topology_is_surfaced_without_materializing_the_schema()
    {
        var connectionString = await container.CreateDatabaseAsync(enableReadCommittedSnapshot: false);
        try
        {
            var exception = await Assert.ThrowsAsync<DiagnosticRecordDeploymentAdmissionException>(async () =>
                await SqlServerDiagnosticRecordStoreFactory.CreateSessionFactory(connectionString)
                    .OpenAsync(Deployment(), Scope));

            Assert.Equal(DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed, exception.Code);
            Assert.Contains(
                "READ_COMMITTED_SNAPSHOT ON",
                Assert.IsType<InvalidOperationException>(exception.InnerException).Message,
                StringComparison.Ordinal);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = N'groundwork_diagnostic_definitions';";
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }
        finally
        {
            await container.DropDatabaseAsync(connectionString);
        }
    }

    private static readonly DiagnosticStorageScope Scope = new("tenant", "shell");

    private static DiagnosticRecordDeploymentManifest Deployment() =>
        new(RelationalTestManifests.MetadataManifest(), [Definition()]);

    private static DiagnosticRecordStreamDefinition Definition() => new(
        new("runtime-admission"),
        1,
        "runtime_admission",
        [new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
            new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Equal }, MaxStringBytes: 128)],
        new(MaxRecordIdBytes: SqlServerDiagnosticRecordValidator.MaxRecordIdBytes),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(10));
}
