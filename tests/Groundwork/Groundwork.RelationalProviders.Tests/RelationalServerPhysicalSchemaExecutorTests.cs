using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Core.Manifests;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.SqlServer.PhysicalStorage;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalServerPhysicalSchemaExecutorTests
{
    private static readonly PhysicalSchemaTargetIdentity Target = new(
        new StorageManifestIdentity("relational-lock-cleanup-tests"),
        "sqlserver");

    [Fact]
    public async Task Failed_connection_open_preserves_primary_failure_when_disposal_fails()
    {
        var connection = new FailureConnection();
        var executor = CreateExecutor(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.AcquireApplicationLockAsync(Target, CancellationToken.None).AsTask());

        Assert.Equal("open failed", exception.Message);
        AssertCleanupFailures(exception, "dispose failed");
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Canceled_connection_open_normalizes_after_failing_disposal()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FailureConnection(beforeOpen: cancellation.Cancel);
        var executor = CreateExecutor(connection);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.AcquireApplicationLockAsync(Target, cancellation.Token).AsTask());

        var primaryFailure = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("open failed", primaryFailure.Message);
        AssertCleanupFailures(primaryFailure, "dispose failed");
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Failed_bootstrap_initialization_preserves_primary_when_release_and_disposal_fail()
    {
        var connection = new FailureConnection(initiallyOpen: true);
        var executor = CreateExecutor(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.AcquireApplicationLockAsync(Target, CancellationToken.None).AsTask());

        Assert.Equal("ensure failed", exception.Message);
        AssertCleanupFailures(exception, "release failed", "dispose failed");
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Canceled_bootstrap_initialization_normalizes_after_release_and_disposal_fail()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FailureConnection(initiallyOpen: true);
        var executor = CreateExecutor(connection, cancellation.Cancel);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.AcquireApplicationLockAsync(Target, cancellation.Token).AsTask());

        var primaryFailure = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("ensure failed", primaryFailure.Message);
        AssertCleanupFailures(primaryFailure, "release failed", "dispose failed");
        Assert.Equal(1, connection.DisposeCount);
    }

    private static RelationalServerPhysicalSchemaExecutor CreateExecutor(
        FailureConnection connection,
        Action? beforeEnsure = null) =>
        new(() => connection, new FailingBootstrapDialect(beforeEnsure));

    private static void AssertCleanupFailures(Exception exception, params string[] expectedMessages)
    {
        var cleanupFailures = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data["Groundwork.Relational.CleanupFailures"]);
        Assert.Equal(expectedMessages, cleanupFailures.Select(cleanup => cleanup.Message));
    }

    private sealed class FailingBootstrapDialect(Action? beforeEnsure) : SqlServerPhysicalSchemaDialect
    {
        public override Task AcquireApplicationLockAsync(
            DbConnection connection,
            string resource,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task EnsureInfrastructureAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            beforeEnsure?.Invoke();
            return Task.FromException(new InvalidOperationException("ensure failed"));
        }

        public override Task ReleaseApplicationLockAsync(
            DbConnection connection,
            string resource,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("release failed"));
    }

    private sealed class FailureConnection(
        bool initiallyOpen = false,
        Action? beforeOpen = null) : DbConnection
    {
        public int DisposeCount { get; private set; }
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => initiallyOpen ? ConnectionState.Open : ConnectionState.Closed;

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
