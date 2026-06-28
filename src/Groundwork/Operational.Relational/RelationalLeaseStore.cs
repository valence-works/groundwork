using Groundwork.Core.Identity;
using Groundwork.Provider.Relational;
using System.Data.Common;
using Groundwork.Operational.Leases;

namespace Groundwork.Operational.Relational;

internal sealed class RelationalLeaseStore(
    RelationalExecutor executor,
    IOperationalClock clock,
    IGroundworkIdentityGenerator identityGenerator)
    : RelationalOperationalStoreBase(executor, clock, identityGenerator), ILeaseStore
{
    public Task<LeaseAcquisition> TryAcquireAsync(AcquireLeaseRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync<LeaseAcquisition>(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var expiresAt = now + request.LeaseDuration;
            var current = await ReadRowAsync(connection, transaction, request.Unit, request.ResourceKey, ct);

            if (current is null)
            {
                await InsertAsync(connection, transaction, request.Unit, request.ResourceKey, request.OwnerId, 1, expiresAt, ct);
                return new LeaseAcquisition.Acquired(1, expiresAt);
            }

            var heldByCaller = current.Value.OwnerId == request.OwnerId;
            var expired = current.Value.ExpiresAt <= now;
            if (!heldByCaller && !expired)
                return new LeaseAcquisition.Denied(current.Value.OwnerId, current.Value.ExpiresAt);

            var fencingToken = current.Value.FencingToken + 1;
            await UpdateAsync(connection, transaction, request.Unit, request.ResourceKey, request.OwnerId, fencingToken, expiresAt, ct);
            return new LeaseAcquisition.Acquired(fencingToken, expiresAt);
        }, cancellationToken);

    public Task<LeaseAcquisition> RenewAsync(RenewLeaseRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync<LeaseAcquisition>(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var current = await ReadRowAsync(connection, transaction, request.Unit, request.ResourceKey, ct);
            if (current is null ||
                current.Value.OwnerId != request.OwnerId ||
                current.Value.FencingToken != request.FencingToken ||
                current.Value.ExpiresAt <= now)
            {
                return current is null
                    ? new LeaseAcquisition.Denied(string.Empty, now)
                    : new LeaseAcquisition.Denied(current.Value.OwnerId, current.Value.ExpiresAt);
            }

            var expiresAt = now + request.LeaseDuration;
            await UpdateAsync(connection, transaction, request.Unit, request.ResourceKey, request.OwnerId, request.FencingToken, expiresAt, ct);
            return new LeaseAcquisition.Acquired(request.FencingToken, expiresAt);
        }, cancellationToken);

    public Task<bool> ReleaseAsync(ReleaseLeaseRequest request, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            // Retain the row (owner cleared, expired) so the fencing token stays monotonic across
            // re-acquisitions after release.
            await using var command = CreateCommand(connection, transaction, """
                UPDATE groundwork_leases
                SET owner_id = '', expires_utc = @expired
                WHERE unit = @unit AND resource_key = @resourceKey
                  AND owner_id = @ownerId AND fencing_token = @fencingToken;
                """);
            AddParameter(command, "expired", Format(Clock.UtcNow));
            AddParameter(command, "unit", request.Unit);
            AddParameter(command, "resourceKey", request.ResourceKey);
            AddParameter(command, "ownerId", request.OwnerId);
            AddParameter(command, "fencingToken", request.FencingToken);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, cancellationToken);

    public Task<LeaseState?> ReadAsync(string unit, string resourceKey, CancellationToken cancellationToken = default) =>
        Executor.ExecuteAsync(async (connection, transaction, ct) =>
        {
            var now = Clock.UtcNow;
            var current = await ReadRowAsync(connection, transaction, unit, resourceKey, ct);
            if (current is null || current.Value.OwnerId.Length == 0 || current.Value.ExpiresAt <= now)
                return (LeaseState?)null;

            return new LeaseState(resourceKey, current.Value.OwnerId, current.Value.FencingToken, current.Value.ExpiresAt);
        }, cancellationToken);

    private static async Task<(string OwnerId, long FencingToken, DateTimeOffset ExpiresAt)?> ReadRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string resourceKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT owner_id, fencing_token, expires_utc FROM groundwork_leases
            WHERE unit = @unit AND resource_key = @resourceKey;
            """);
        AddParameter(command, "unit", unit);
        AddParameter(command, "resourceKey", resourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (reader.GetString(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)));
    }

    private static async Task InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string resourceKey,
        string ownerId,
        long fencingToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO groundwork_leases (unit, resource_key, owner_id, fencing_token, expires_utc)
            VALUES (@unit, @resourceKey, @ownerId, @fencingToken, @expires);
            """);
        AddParameter(command, "unit", unit);
        AddParameter(command, "resourceKey", resourceKey);
        AddParameter(command, "ownerId", ownerId);
        AddParameter(command, "fencingToken", fencingToken);
        AddParameter(command, "expires", Format(expiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string resourceKey,
        string ownerId,
        long fencingToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            UPDATE groundwork_leases
            SET owner_id = @ownerId, fencing_token = @fencingToken, expires_utc = @expires
            WHERE unit = @unit AND resource_key = @resourceKey;
            """);
        AddParameter(command, "ownerId", ownerId);
        AddParameter(command, "fencingToken", fencingToken);
        AddParameter(command, "expires", Format(expiresAt));
        AddParameter(command, "unit", unit);
        AddParameter(command, "resourceKey", resourceKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
