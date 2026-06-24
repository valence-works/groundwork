using Groundwork.Core.Capabilities;

namespace Groundwork.Core.Migrations;

public sealed class GroundworkMigrationRunner(
    IGroundworkMigrationExecutor executor,
    TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<GroundworkMigrationResult> RunAsync(
        IEnumerable<IGroundworkMigration> migrations,
        GroundworkMigrationExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GroundworkMigrationExecutionOptions();
        if (!options.DryRun)
            await executor.EnsureLedgerAsync(cancellationToken);

        var diagnostics = new List<string>();
        var orderedMigrations = migrations
            .OrderBy(migration => migration.Version)
            .ThenBy(migration => migration.Identity, StringComparer.Ordinal)
            .ToList();
        ValidateUniqueVersions(orderedMigrations, diagnostics);

        var appliedKeys = await executor.ReadAppliedKeysAsync(cancellationToken);
        var pending = orderedMigrations
            .Where(migration => !appliedKeys.Contains(MigrationKey(migration.Identity, migration.Version, executor.Provider)))
            .ToList();

        foreach (var migration in pending)
        {
            var destructiveOperations = migration.Operations.Where(operation => operation.IsDestructive).ToList();
            if (destructiveOperations.Count != 0 && !options.AllowDestructive)
            {
                diagnostics.Add(
                    $"Migration '{migration.Identity}' version {migration.Version} contains destructive operations: {string.Join(", ", destructiveOperations.Select(operation => operation.Identity))}.");
            }
        }

        if (diagnostics.Count != 0 || options.DryRun)
        {
            return new GroundworkMigrationResult(
                [],
                pending,
                diagnostics);
        }

        var applied = new List<GroundworkMigrationRecord>();
        foreach (var migration in pending)
        {
            var appliedUtc = clock.GetUtcNow();
            if (!await executor.ExecuteAsync(migration, appliedUtc, cancellationToken))
                continue;

            applied.Add(new GroundworkMigrationRecord(
                migration.Identity,
                migration.Version,
                executor.Provider,
                appliedUtc,
                migration.Description));
        }

        return new GroundworkMigrationResult(
            applied,
            [],
            diagnostics);
    }

    public static string MigrationKey(string identity, long version, ProviderIdentity provider) =>
        $"{provider.Name}\u001F{provider.Version}\u001F{identity}\u001F{version}";

    private static void ValidateUniqueVersions(
        IReadOnlyList<IGroundworkMigration> migrations,
        List<string> diagnostics)
    {
        foreach (var duplicate in migrations.GroupBy(migration => (migration.Identity, migration.Version)).Where(group => group.Count() > 1))
            diagnostics.Add($"Migration '{duplicate.Key.Identity}' version {duplicate.Key.Version} is registered more than once.");
    }
}
