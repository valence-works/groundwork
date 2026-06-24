using Groundwork.Core.Capabilities;

namespace Groundwork.Core.Migrations;

public interface IGroundworkMigrationExecutor
{
    ProviderIdentity Provider { get; }

    Task EnsureLedgerAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> ReadAppliedKeysAsync(CancellationToken cancellationToken = default);

    Task<bool> ExecuteAsync(IGroundworkMigration migration, DateTimeOffset appliedUtc, CancellationToken cancellationToken = default);
}
