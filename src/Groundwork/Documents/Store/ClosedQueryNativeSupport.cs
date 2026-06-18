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
            if (clause.Comparisons.Count > 1 && !profile.SupportsDisjunction)
                reasons.Add($"Index '{clause.Comparisons[0].IndexName}' clause uses OR composition but the unit does not declare disjunction support.");

            foreach (var comparison in clause.Comparisons)
            {
                var operation = ToPortableOperation(comparison.Operator);
                if (!profile.SupportsOperator(comparison.IndexName, operation))
                    reasons.Add($"Index '{comparison.IndexName}' does not natively support operator '{operation}'.");
            }
        }

        if (query.Order is not null && !profile.SupportsOrderBy(query.Order.IndexName))
            reasons.Add($"Index '{query.Order.IndexName}' is not sortable, so ORDER BY is not natively supported.");

        var usesOffsetPaging = (query.Skip ?? 0) > 0 || query.Take is not null;
        if (usesOffsetPaging && !profile.SupportsOffsetPaging)
            reasons.Add("Offset paging is requested but the unit does not declare offset paging support.");

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
