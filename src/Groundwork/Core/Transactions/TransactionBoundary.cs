namespace Groundwork.Core.Transactions;

/// <summary>
/// Describes how far a provider can extend a single atomic commit boundary. Shared by the operational
/// cross-unit unit of work and the document cross-document unit of work.
/// </summary>
public enum TransactionBoundary
{
    /// <summary>Each operation commits independently; cross-unit/cross-document atomic commit is not available.</summary>
    PerOperation,

    /// <summary>Multiple units or documents can commit atomically within one transaction.</summary>
    CrossUnitAtomic
}
