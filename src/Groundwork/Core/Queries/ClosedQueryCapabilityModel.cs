using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Queries;

/// <summary>
/// Projects a <see cref="StorageUnit"/>'s <see cref="PortableQueryDeclaration"/> entries into a flat,
/// provider-neutral, per-index description of which closed-query operators, ordering, paging, and
/// contract flags it natively supports. Detection is driven by the unit's declarations (not by raw
/// index capability), so an index that physically supports an operator the unit never declared is not
/// reported as native. Adapters read this to decide whether Groundwork can execute a given query shape
/// server-side or whether they must fall back to an in-memory evaluation.
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

    /// <summary>Builds the per-index closed-query support profile for a storage unit from its declarations.</summary>
    public static StorageUnitClosedQuerySupport Describe(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var singleFieldIndexes = unit.Indexes
            .Where(index => index.Fields.Count == 1)
            .ToDictionary(index => index.Identity, StringComparer.Ordinal);

        var supports = new Dictionary<string, ClosedQueryIndexSupport>(StringComparer.Ordinal);

        foreach (var declarations in unit.Queries.GroupBy(query => query.IndexIdentity, StringComparer.Ordinal))
        {
            // Declarations targeting a multi-field or unknown index are outside the closed surface.
            if (!singleFieldIndexes.TryGetValue(declarations.Key, out var index))
                continue;

            var operators = declarations
                .SelectMany(declaration => declaration.Operations)
                .Where(ClosedComparisonOperators.Contains)
                .ToHashSet();

            var canAscend = declarations.Any(d => d.SortSupport is QuerySortSupport.Ascending or QuerySortSupport.Both);
            var canDescend = declarations.Any(d => d.SortSupport is QuerySortSupport.Descending or QuerySortSupport.Both);
            var sortSupport = (canAscend, canDescend) switch
            {
                (true, true) => QuerySortSupport.Both,
                (true, false) => QuerySortSupport.Ascending,
                (false, true) => QuerySortSupport.Descending,
                _ => QuerySortSupport.None
            };

            var paging = declarations
                .Select(declaration => declaration.PagingSupport)
                .Aggregate(QueryPagingSupport.None, MaxPaging);

            supports[declarations.Key] = new ClosedQueryIndexSupport(
                declarations.Key,
                index.Fields[0].Path,
                operators,
                index.IsSortable,
                sortSupport,
                paging,
                declarations.Any(d => d.SupportsDisjunction),
                declarations.Any(d => d.SupportsTotalCount));
        }

        return new StorageUnitClosedQuerySupport(supports);
    }

    private static QueryPagingSupport MaxPaging(QueryPagingSupport current, QueryPagingSupport candidate) =>
        (QueryPagingSupport)Math.Max((int)current, (int)candidate);
}

/// <summary>Native closed-query support a single declared single-field index offers.</summary>
public sealed record ClosedQueryIndexSupport(
    string IndexIdentity,
    string FieldPath,
    IReadOnlySet<PortableQueryOperation> Operators,
    bool IsSortable,
    QuerySortSupport SortSupport,
    QueryPagingSupport Paging,
    bool SupportsDisjunction,
    bool SupportsTotalCount);

/// <summary>The per-index closed-query capabilities a storage unit natively supports.</summary>
public sealed record StorageUnitClosedQuerySupport(
    IReadOnlyDictionary<string, ClosedQueryIndexSupport> Indexes)
{
    /// <summary>Whether <paramref name="indexIdentity"/> natively supports <paramref name="operation"/>.</summary>
    public bool SupportsOperator(string indexIdentity, PortableQueryOperation operation) =>
        Indexes.TryGetValue(indexIdentity, out var support) && support.Operators.Contains(operation);

    /// <summary>Whether <paramref name="indexIdentity"/> can order results in the requested direction.</summary>
    public bool SupportsOrderBy(string indexIdentity, bool descending = false) =>
        Indexes.TryGetValue(indexIdentity, out var support)
        && support.IsSortable
        && SortCovers(support.SortSupport, descending);

    /// <summary>Whether <paramref name="indexIdentity"/> declares OR-composition (disjunction) support.</summary>
    public bool SupportsDisjunction(string indexIdentity) =>
        Indexes.TryGetValue(indexIdentity, out var support) && support.SupportsDisjunction;

    /// <summary>Whether <paramref name="indexIdentity"/> declares total-count support.</summary>
    public bool SupportsTotalCount(string indexIdentity) =>
        Indexes.TryGetValue(indexIdentity, out var support) && support.SupportsTotalCount;

    /// <summary>Whether <paramref name="indexIdentity"/> declares offset paging support.</summary>
    public bool SupportsOffsetPaging(string indexIdentity) =>
        Indexes.TryGetValue(indexIdentity, out var support)
        && support.Paging is QueryPagingSupport.Offset or QueryPagingSupport.Cursor;

    private static bool SortCovers(QuerySortSupport support, bool descending) =>
        descending
            ? support is QuerySortSupport.Descending or QuerySortSupport.Both
            : support is QuerySortSupport.Ascending or QuerySortSupport.Both;
}
