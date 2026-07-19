using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Queries;

/// <summary>
/// Projects a <see cref="StorageUnit"/>'s <see cref="PortableQueryDeclaration"/> entries into a flat,
/// provider-neutral, per-index description of the closed-query capabilities that <em>vary per index</em>:
/// which comparison operators each declared single-field index supports, and whether (and in which
/// direction) it can be ordered. Detection is driven by the unit's declarations (not by raw index
/// capability), so an index that physically supports an operator the unit never declared is not reported
/// as native.
/// <para>
/// OR-composition, total-count, and offset paging are deliberately <em>not</em> modelled here: per the
/// closed-query contract they are universal query shapes/features (OR is a query shape, total count rides
/// with offset paging, and offset paging is a base store capability), so they do not vary per index and
/// are not gated. Adapters read this surface to decide whether Groundwork can execute a given query shape
/// server-side or whether they must fall back to an in-memory evaluation.
/// </para>
/// </summary>
public static class ClosedQueryCapabilityModel
{
    /// <summary>The comparison operators that form the closed query contract.</summary>
    public static IReadOnlySet<PortableQueryOperation> ClosedComparisonOperators { get; } =
        new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In,
            PortableQueryOperation.Contains,
            PortableQueryOperation.NotContains
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

            supports[declarations.Key] = new ClosedQueryIndexSupport(
                declarations.Key,
                index.Fields[0].Path,
                operators,
                index.IsSortable,
                sortSupport);
        }

        return new StorageUnitClosedQuerySupport(supports);
    }
}

/// <summary>Native closed-query support a single declared single-field index offers.</summary>
public sealed record ClosedQueryIndexSupport(
    string IndexIdentity,
    string FieldPath,
    IReadOnlySet<PortableQueryOperation> Operators,
    bool IsSortable,
    QuerySortSupport SortSupport);

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

    private static bool SortCovers(QuerySortSupport support, bool descending) =>
        descending
            ? support is QuerySortSupport.Descending or QuerySortSupport.Both
            : support is QuerySortSupport.Ascending or QuerySortSupport.Both;
}
