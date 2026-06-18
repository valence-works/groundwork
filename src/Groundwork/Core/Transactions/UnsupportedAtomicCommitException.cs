namespace Groundwork.Core.Transactions;

/// <summary>
/// Thrown when a provider is asked to commit a multi-unit or multi-document scope it cannot honor
/// atomically — a provider whose boundary is <see cref="TransactionBoundary.PerOperation"/> (for
/// example a standalone MongoDB deployment with no multi-document transactions). Providers fail
/// loudly here rather than silently losing atomicity, so callers can fall back to a compensation
/// strategy deliberately.
/// </summary>
public sealed class UnsupportedAtomicCommitException : InvalidOperationException
{
    public UnsupportedAtomicCommitException(IReadOnlyList<string> units, string? reason = null)
        : base(reason is null
            ? $"The provider cannot atomically commit the requested scope across units: {string.Join(", ", units)}."
            : $"The provider cannot atomically commit the requested scope across units: {string.Join(", ", units)}. {reason}")
    {
        Units = units;
        Reason = reason;
    }

    /// <summary>The unit or document-kind identities that were requested to commit atomically.</summary>
    public IReadOnlyList<string> Units { get; }

    /// <summary>Optional provider-specific explanation (for example the deployment topology).</summary>
    public string? Reason { get; }
}
