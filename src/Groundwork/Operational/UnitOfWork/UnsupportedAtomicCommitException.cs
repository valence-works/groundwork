namespace Groundwork.Operational.UnitOfWork;

/// <summary>
/// Thrown when a provider is asked to commit a cross-unit scope it cannot honor atomically (for
/// example a document-only provider whose transaction boundary is <see cref="TransactionBoundary.PerOperation"/>).
/// Providers fail loudly here rather than silently losing atomicity.
/// </summary>
public sealed class UnsupportedAtomicCommitException : InvalidOperationException
{
    public UnsupportedAtomicCommitException(OperationalCommitScope scope)
        : base($"The provider cannot atomically commit the requested operational scope across units: {string.Join(", ", scope.Units)}.")
    {
        Scope = scope;
    }

    public OperationalCommitScope Scope { get; }
}
