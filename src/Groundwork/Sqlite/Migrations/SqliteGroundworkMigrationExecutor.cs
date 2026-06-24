using System.Data;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Migrations;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Migrations;

public sealed class SqliteGroundworkMigrationExecutor(
    SqliteConnection connection,
    ProviderIdentity provider) : IGroundworkMigrationExecutor
{
    public ProviderIdentity Provider => provider;

    public async Task EnsureLedgerAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS groundwork_migration_history (
                migration_identity TEXT NOT NULL,
                migration_version INTEGER NOT NULL,
                provider_name TEXT NOT NULL,
                provider_version TEXT NOT NULL,
                description TEXT NOT NULL,
                applied_utc TEXT NOT NULL,
                PRIMARY KEY (migration_identity, migration_version, provider_name, provider_version)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ReadAppliedKeysAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        if (!await LedgerExistsAsync(cancellationToken))
            return new HashSet<string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT migration_identity, migration_version, provider_name, provider_version
            FROM groundwork_migration_history
            WHERE provider_name = $providerName AND provider_version = $providerVersion;
            """;
        command.Parameters.AddWithValue("$providerName", provider.Name);
        command.Parameters.AddWithValue("$providerVersion", provider.Version);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appliedProvider = new ProviderIdentity(reader.GetString(2), reader.GetString(3));
            applied.Add(GroundworkMigrationRunner.MigrationKey(reader.GetString(0), reader.GetInt64(1), appliedProvider));
        }

        return applied;
    }

    private async Task<bool> LedgerExistsAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'groundwork_migration_history';
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<bool> ExecuteAsync(
        IGroundworkMigration migration,
        DateTimeOffset appliedUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await TryClaimMigrationAsync(migration, appliedUtc, transaction, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            foreach (var operation in migration.Operations)
                await ExecuteOperationAsync(operation, transaction, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task ExecuteOperationAsync(
        GroundworkMigrationOperation operation,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (operation.Kind is not GroundworkMigrationOperationKind.ProviderSql and not GroundworkMigrationOperationKind.ProviderDestructive)
        {
            throw new NotSupportedException(
                $"SQLite migration operation '{operation.Kind}' is not supported by '{nameof(SqliteGroundworkMigrationExecutor)}'.");
        }

        if (!operation.Metadata.TryGetValue("sql", out var sql) || string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException($"SQLite migration operation '{operation.Identity}' is missing required metadata value 'sql'.");

        var command = (SqliteCommand)transaction.Connection!.CreateCommand();
        await using (command)
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> TryClaimMigrationAsync(
        IGroundworkMigration migration,
        DateTimeOffset appliedUtc,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command)
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO groundwork_migration_history
                (migration_identity, migration_version, provider_name, provider_version, description, applied_utc)
                VALUES ($identity, $version, $providerName, $providerVersion, $description, $appliedUtc);
                """;
            command.Parameters.AddWithValue("$identity", migration.Identity);
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$providerName", provider.Name);
            command.Parameters.AddWithValue("$providerVersion", provider.Version);
            command.Parameters.AddWithValue("$description", migration.Description);
            command.Parameters.AddWithValue("$appliedUtc", appliedUtc.ToString("O"));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                return true;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return false;
            }
        }
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
    }
}
