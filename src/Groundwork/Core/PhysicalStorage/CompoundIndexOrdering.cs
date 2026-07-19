using Groundwork.Core.Indexing;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Resolves the physical index position at which an ordered compound query can begin.
/// A range boundary may also lead the requested order when every preceding predicate is an equality.
/// </summary>
internal static class CompoundIndexOrdering
{
    public static bool TryResolveSortStart(
        IReadOnlyList<string> indexPaths,
        IReadOnlyList<string> predicatePaths,
        IReadOnlyList<BoundedQueryPredicateField> predicates,
        IReadOnlyList<string> sortPaths,
        out int sortStart,
        out int requiredEqualityPredicateCount)
    {
        if (indexPaths.Take(sortPaths.Count).SequenceEqual(sortPaths))
        {
            sortStart = 0;
            requiredEqualityPredicateCount = 0;
            return true;
        }

        if (indexPaths.Skip(predicatePaths.Count).Take(sortPaths.Count).SequenceEqual(sortPaths))
        {
            sortStart = predicatePaths.Count;
            requiredEqualityPredicateCount = predicates.Count;
            return true;
        }

        if (predicatePaths.Count == predicates.Count &&
            predicatePaths.Count > 0 &&
            sortPaths.Count > 0 &&
            predicatePaths[^1] == sortPaths[0] &&
            HasRangeBoundary(predicates[^1].Operations) &&
            indexPaths.Skip(predicatePaths.Count - 1).Take(sortPaths.Count).SequenceEqual(sortPaths))
        {
            sortStart = predicatePaths.Count - 1;
            requiredEqualityPredicateCount = predicates.Count - 1;
            return true;
        }

        sortStart = 0;
        requiredEqualityPredicateCount = 0;
        return false;
    }

    public static bool AreSingleValueEqualities(
        IReadOnlyList<BoundedQueryPredicateField> predicates,
        int count) =>
        predicates.Take(count).All(predicate =>
            predicate.Operations.Count == 1 &&
            predicate.Operations.Contains(PortableQueryOperation.Equal));

    private static bool HasRangeBoundary(IReadOnlySet<PortableQueryOperation> operations) =>
        operations.Any(operation => operation is
            PortableQueryOperation.GreaterThan or
            PortableQueryOperation.GreaterThanOrEqual or
            PortableQueryOperation.LessThan or
            PortableQueryOperation.LessThanOrEqual);
}
