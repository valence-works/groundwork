namespace Groundwork.Operational.Leases;

public sealed record AcquireLeaseRequest(
    string Unit,
    string ResourceKey,
    string OwnerId,
    TimeSpan LeaseDuration);

public sealed record RenewLeaseRequest(
    string Unit,
    string ResourceKey,
    string OwnerId,
    long FencingToken,
    TimeSpan LeaseDuration);

public sealed record ReleaseLeaseRequest(
    string Unit,
    string ResourceKey,
    string OwnerId,
    long FencingToken);

/// <summary>Outcome of an acquire/renew attempt: either granted with a fencing token, or denied.</summary>
public abstract record LeaseAcquisition
{
    private LeaseAcquisition()
    {
    }

    public sealed record Acquired(long FencingToken, DateTimeOffset ExpiresAt) : LeaseAcquisition;

    public sealed record Denied(string CurrentOwner, DateTimeOffset ExpiresAt) : LeaseAcquisition;
}

public sealed record LeaseState(
    string ResourceKey,
    string OwnerId,
    long FencingToken,
    DateTimeOffset ExpiresAt);
