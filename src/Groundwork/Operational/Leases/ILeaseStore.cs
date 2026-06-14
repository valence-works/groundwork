namespace Groundwork.Operational.Leases;

/// <summary>
/// Ownership lease store providing distributed-lock-style coordination with monotonic fencing
/// tokens and TTL/expiry. Maps to storage requirements <c>LeaseRecovery</c> and
/// <c>FencedOwnership</c>. The fencing token strictly increases per resource across acquisitions so
/// a stale owner whose lease has expired can be detected and fenced out by downstream writers.
/// </summary>
public interface ILeaseStore
{
    /// <summary>
    /// Attempts to acquire (or re-acquire when already owned, or steal when expired) the lease for a
    /// resource. On success a strictly larger fencing token than any previously issued for the
    /// resource is returned.
    /// </summary>
    Task<LeaseAcquisition> TryAcquireAsync(AcquireLeaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends an owned lease. Requires the caller's owner id and fencing token to match the current
    /// holder; otherwise the request is denied.
    /// </summary>
    Task<LeaseAcquisition> RenewAsync(RenewLeaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an owned lease. Requires a matching owner id and fencing token. Returns
    /// <c>false</c> when the caller is not the current holder.
    /// </summary>
    Task<bool> ReleaseAsync(ReleaseLeaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads the current lease state for a resource, or <c>null</c> when unheld.</summary>
    Task<LeaseState?> ReadAsync(string unit, string resourceKey, CancellationToken cancellationToken = default);
}
