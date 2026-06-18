using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;

namespace Groundwork.Documents.Store;

/// <summary>
/// Determines whether a <see cref="PortableDocumentQuery"/> can be executed natively (server-side)
/// against a <see cref="StorageUnit"/>, or whether the caller must fall back to in-memory evaluation.
/// Adapters call <see cref="Evaluate"/> before dispatching a closed query so they can cleanly skip the
/// fallback when Groundwork supports the shape.
/// </summary>
public static class ClosedQueryNativeSupport
{
    /// <summary>Evaluates whether <paramref name="query"/> is natively supported by <paramref name="unit"/>.</summary>
    public static ClosedQuerySupportResult Evaluate(StorageUnit unit, PortableDocumentQuery query)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(query);

        var profile = ClosedQueryCapabilityModel.Describe(unit);
        var reasons = new List<string>();

        foreach (var clause in query.Clauses)
        {
            var isDisjunction = clause.Comparisons.Count > 1;

            foreach (var comparison in clause.Comparisons)
            {
                var operation = ToPortableOperation(comparison.Operator);
                if (!profile.SupportsOperator(comparison.IndexName, operation))
                    reasons.Add($"Index '{comparison.IndexName}' does not natively support operator '{operation}'.");

                if (isDisjunction && !profile.SupportsDisjunction(comparison.IndexName))
                    reasons.Add($"Index '{comparison.IndexName}' participates in an OR composition but does not declare disjunction support.");
            }
        }

        if (query.Order is not null && !profile.SupportsOrderBy(query.Order.IndexName, query.Order.Descending))
            reasons.Add($"Index '{query.Order.IndexName}' does not natively support the requested ORDER BY (index not sortable or direction not declared).");

        var usesOffsetPaging = (query.Skip ?? 0) > 0 || query.Take is not null;
        if (usesOffsetPaging)
        {
            // Total count rides with offset paging, so a paged query needs both capabilities on every
            // index it references. A match-all paged query references no index and uses the store's
            // base offset paging, so it is not gated here.
            var referencedIndexes = query.Clauses
                .SelectMany(clause => clause.Comparisons.Select(comparison => comparison.IndexName))
                .Concat(query.Order is null ? Array.Empty<string>() : new[] { query.Order.IndexName })
                .Distinct(StringComparer.Ordinal);

            foreach (var indexName in referencedIndexes)
            {
                if (!profile.SupportsOffsetPaging(indexName))
                    reasons.Add($"Offset paging is requested but index '{indexName}' does not declare offset paging support.");

                if (!profile.SupportsTotalCount(indexName))
                    reasons.Add($"Offset paging requires a total count, but index '{indexName}' does not declare total-count support.");
            }
        }

        return reasons.Count == 0
            ? ClosedQuerySupportResult.Native
            : new ClosedQuerySupportResult(false, reasons);
    }

    private static PortableQueryOperation ToPortableOperation(QueryComparisonOperator @operator) =>
        @operator switch
        {
            QueryComparisonOperator.Equal => PortableQueryOperation.Equal,
            QueryComparisonOperator.In => PortableQueryOperation.In,
            QueryComparisonOperator.Contains => PortableQueryOperation.Contains,
            _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported comparison operator.")
        };
}

/// <summary>The outcome of a native-support check: whether the query is supported and, if not, why.</summary>
public sealed record ClosedQuerySupportResult(bool IsNativelySupported, IReadOnlyList<string> Reasons)
{
    public static ClosedQuerySupportResult Native { get; } = new(true, Array.Empty<string>());
}
