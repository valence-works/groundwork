using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.PhysicalStorage;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalPhysicalProviderHardeningTests
{
    private static readonly PhysicalSchemaTargetIdentity Target = new(
        new StorageManifestIdentity("relational-lock-cleanup-tests"),
        "sqlserver");

    [Theory]
    [InlineData(RelationalEnvelopeColumnKind.DocumentKind)]
    [InlineData(RelationalEnvelopeColumnKind.StorageScope)]
    [InlineData(RelationalEnvelopeColumnKind.Id)]
    [InlineData(RelationalEnvelopeColumnKind.SchemaVersion)]
    [InlineData(RelationalEnvelopeColumnKind.Timestamp)]
    public void Postgre_sql_text_envelope_columns_use_explicit_ordinal_collation(
        RelationalEnvelopeColumnKind kind)
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();

        Assert.Equal("C", dialect.EnvelopeCollation(kind));
        Assert.Contains("COLLATE \"pg_catalog\".\"C\"", dialect.EnvelopeColumn("value", kind), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgre_sql_default_string_projection_uses_explicit_ordinal_collation()
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();
        var projected = new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String);

        Assert.Equal("C", dialect.ProjectedCollation(projected));
    }

    [Fact]
    public void Postgre_sql_only_accepts_pg_catalog_C_as_the_ordinal_collation_identity()
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();

        Assert.Equal("C", dialect.NormalizeCollationIdentity("pg_catalog:C"));
        Assert.NotEqual(
            dialect.NormalizeCollationIdentity("pg_catalog:C"),
            dialect.NormalizeCollationIdentity("tenant_schema:C"));
    }

    [Fact]
    public void Sql_server_identity_hash_column_must_be_persisted_computed_sha256()
    {
        var dialect = new SqlServer.PhysicalStorage.SqlServerPhysicalSchemaDialect();
        var expected = dialect.IdentityLayout(
            [new RelationalPhysicalIdentityColumn("document_kind", RelationalEnvelopeColumnKind.DocumentKind)],
            ["document_kind"]).ProviderColumns.Single();
        var plainBinary = new RelationalPhysicalColumnMetadata(
            expected.Name,
            expected.Type,
            false,
            null,
            null,
            1,
            IsComputed: false,
            IsPersisted: false,
            ComputedDefinition: null);

        Assert.False(dialect.IsProviderOwnedColumnCompatible(expected, plainBinary));
    }

    [Fact]
    public async Task Failed_lock_acquisition_preserves_primary_failure_when_connection_disposal_fails()
    {
        var connection = new FailingOpenAndDisposeConnection();
        var executor = new RelationalServerPhysicalSchemaExecutor(
            () => connection,
            new SqlServer.PhysicalStorage.SqlServerPhysicalSchemaDialect());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.AcquireApplicationLockAsync(Target, CancellationToken.None).AsTask());

        Assert.Equal("open failed", exception.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(cleanupFailures, cleanup => Assert.Equal("dispose failed", cleanup.Message));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Canceled_lock_acquisition_normalizes_after_failing_connection_disposal()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FailingOpenAndDisposeConnection(cancellation.Cancel);
        var executor = new RelationalServerPhysicalSchemaExecutor(
            () => connection,
            new SqlServer.PhysicalStorage.SqlServerPhysicalSchemaDialect());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.AcquireApplicationLockAsync(Target, cancellation.Token).AsTask());

        var primaryFailure = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("open failed", primaryFailure.Message);
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            primaryFailure.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Collection(cleanupFailures, cleanup => Assert.Equal("dispose failed", cleanup.Message));
        Assert.Equal(1, connection.DisposeCount);
    }

    private sealed class FailingOpenAndDisposeConnection(Action? beforeOpen = null) : DbConnection
    {
        public int DisposeCount { get; private set; }
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("open failed");

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            beforeOpen?.Invoke();
            return Task.FromException(new InvalidOperationException("open failed"));
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(new InvalidOperationException("dispose failed"));
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
