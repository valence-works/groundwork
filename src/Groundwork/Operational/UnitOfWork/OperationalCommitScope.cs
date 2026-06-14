namespace Groundwork.Operational.UnitOfWork;

/// <summary>
/// The set of operational storage-unit identities that must commit as one logical transaction.
/// </summary>
public sealed record OperationalCommitScope(IReadOnlyList<string> Units)
{
    public static OperationalCommitScope Of(params string[] units) => new(units);
}

/// <summary>
/// Describes how far a provider can extend a single atomic commit boundary.
/// </summary>
public enum TransactionBoundary
{
    /// <summary>Each operation commits independently; cross-unit atomic commit is not available.</summary>
    PerOperation,

    /// <summary>Multiple operational units can commit atomically within one transaction.</summary>
    CrossUnitAtomic
}
