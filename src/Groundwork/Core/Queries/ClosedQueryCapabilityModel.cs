using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Queries;

/// <summary>
/// Projects a <see cref="StorageUnit"/>'s declared indexes and portable-query declarations into a
/// flat, provider-neutral description of which closed-query operators it natively supports. Adapters
/// read this to decide whether Groundwork can execute a given query shape server-side or whether they
/// must fall back to an in-memory evaluation.
/// </summary>
public static class ClosedQueryCapabilityModel
{
    /// <summary>The comparison operators that form the closed query contract.</summary>
    public static IReadOnlySet<PortableQueryOperation> ClosedComparisonOperators { get; } =
        new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In,
            PortableQueryOperation.Contains
        };

    /// <summary>Builds the closed-query support profile for a storage unit.</summary>
    public static StorageUnitClosedQuerySupport Describe(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var indexes = unit.Indexes
            .Where(index => index.Fields.Count == 1)
            .ToDictionary(
                index => index.Identity,
                index => new ClosedQueryIndexSupport(
                    index.Identity,
                    index.Fields[0].Path,
                    index.SupportedOperations
                        .Where(ClosedComparisonOperators.Contains)
                        .ToHashSet(),
                    index.IsSortable),
                StringComparer.Ordinal);

        var supportsDisjunction = unit.Queries.Any(query => query.SupportsDisjunction);
        var supportsTotalCount = unit.Queries.Any(query => query.SupportsTotalCount);
        var paging = unit.Queries
            .Select(query => query.PagingSupport)
            .Aggregate(QueryPagingSupport.None, MaxPaging);

        return new StorageUnitClosedQuerySupport(indexes, supportsDisjunction, supportsTotalCount, paging);
    }

    private static QueryPagingSupport MaxPaging(QueryPagingSupport current, QueryPagingSupport candidate) =>
        (QueryPagingSupport)Math.Max((int)current, (int)candidate);
}

/// <summary>Native closed-query support a single-field index offers.</summary>
public sealed record ClosedQueryIndexSupport(
    string IndexIdentity,
    string FieldPath,
    IReadOnlySet<PortableQueryOperation> Operators,
    bool IsSortable);

/// <summary>The closed-query capabilities a storage unit natively supports.</summary>
public sealed record StorageUnitClosedQuerySupport(
    IReadOnlyDictionary<string, ClosedQueryIndexSupport> Indexes,
    bool SupportsDisjunction,
    bool SupportsTotalCount,
    QueryPagingSupport Paging)
{
    /// <summary>Whether <paramref name="indexIdentity"/> natively supports <paramref name="operation"/>.</summary>
    public bool SupportsOperator(string indexIdentity, PortableQueryOperation operation) =>
        Indexes.TryGetValue(indexIdentity, out var support) && support.Operators.Contains(operation);

    /// <summary>Whether <paramref name="indexIdentity"/> can be used to order results.</summary>
    public bool SupportsOrderBy(string indexIdentity) =>
        Indexes.TryGetValue(indexIdentity, out var support) && support.IsSortable;

    /// <summary>Whether offset paging is natively available.</summary>
    public bool SupportsOffsetPaging => Paging == QueryPagingSupport.Offset || Paging == QueryPagingSupport.Cursor;
}
