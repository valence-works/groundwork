using Groundwork.Core.Capabilities;
using Groundwork.Core.Migrations;
using Xunit;

namespace Groundwork.Tests;

public sealed class GroundworkMigrationRunnerTests
{
    [Fact]
    public async Task RunAppliesPendingMigrationsInVersionOrder()
    {
        var executor = new RecordingMigrationExecutor();
        var runner = new GroundworkMigrationRunner(executor, new FixedClock());
        var migrations = new[]
        {
            Migration("second", 2),
            Migration("first", 1)
        };

        var result = await runner.RunAsync(migrations);

        Assert.False(result.HasErrors);
        Assert.Equal(["first", "second"], executor.Executed);
        Assert.Equal(["first", "second"], result.Applied.Select(record => record.Identity));
    }

    [Fact]
    public async Task RunSkipsAlreadyAppliedMigrations()
    {
        var executor = new RecordingMigrationExecutor();
        await executor.MarkAppliedAsync(Migration("first", 1));
        var runner = new GroundworkMigrationRunner(executor);

        var result = await runner.RunAsync([Migration("first", 1), Migration("second", 2)]);

        Assert.False(result.HasErrors);
        Assert.Equal(["second"], executor.Executed);
        Assert.Equal(["second"], result.Applied.Select(record => record.Identity));
    }

    [Fact]
    public async Task DryRunReportsPendingWithoutExecuting()
    {
        var executor = new RecordingMigrationExecutor();
        var runner = new GroundworkMigrationRunner(executor);

        var result = await runner.RunAsync(
            [Migration("first", 1)],
            new GroundworkMigrationExecutionOptions(DryRun: true));

        Assert.False(result.HasErrors);
        Assert.Empty(executor.Executed);
        Assert.Equal(["first"], result.Pending.Select(migration => migration.Identity));
    }

    [Fact]
    public async Task DestructiveMigrationsAreBlockedByDefault()
    {
        var executor = new RecordingMigrationExecutor();
        var runner = new GroundworkMigrationRunner(executor);

        var result = await runner.RunAsync(
            [
                new GroundworkMigration(
                    "drop-old-index",
                    1,
                    "Drop old projection",
                    [new GroundworkMigrationOperation("drop", GroundworkMigrationOperationKind.DropIndex, "old-index", new Dictionary<string, string>())])
            ]);

        Assert.True(result.HasErrors);
        Assert.Empty(executor.Executed);
        Assert.Contains("destructive", result.Diagnostics.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunDoesNotReportMigrationAppliedWhenExecutorSkipsStalePendingMigration()
    {
        var executor = new RecordingMigrationExecutor { SkipExecution = true };
        var runner = new GroundworkMigrationRunner(executor);

        var result = await runner.RunAsync([Migration("first", 1)]);

        Assert.False(result.HasErrors);
        Assert.Empty(executor.Executed);
        Assert.Empty(result.Applied);
    }

    private static GroundworkMigration Migration(string identity, long version) =>
        new(
            identity,
            version,
            identity,
            [GroundworkMigrationOperation.ProviderSql($"{identity}-sql", "SELECT 1;")]);

    private sealed class RecordingMigrationExecutor : IGroundworkMigrationExecutor
    {
        private readonly HashSet<string> applied = [];

        public ProviderIdentity Provider { get; } = new("recording-provider", "1.0.0");

        public List<string> Executed { get; } = [];

        public bool SkipExecution { get; init; }

        public Task EnsureLedgerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlySet<string>> ReadAppliedKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(applied);

        public Task<bool> ExecuteAsync(IGroundworkMigration migration, DateTimeOffset appliedUtc, CancellationToken cancellationToken = default)
        {
            if (SkipExecution)
                return Task.FromResult(false);

            if (!MarkApplied(migration))
                return Task.FromResult(false);

            Executed.Add(migration.Identity);
            return Task.FromResult(true);
        }

        public Task MarkAppliedAsync(IGroundworkMigration migration) =>
            Task.FromResult(MarkApplied(migration));

        private bool MarkApplied(IGroundworkMigration migration) =>
            applied.Add(GroundworkMigrationRunner.MigrationKey(migration.Identity, migration.Version, Provider));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-24T11:00:00Z");
    }
}
