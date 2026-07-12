using Groundwork.Core.Indexing;

namespace Groundwork.Core.Queries;

/// <summary>
/// Declares the closed-query capability of a single declared single-field index: which comparison
/// <paramref name="Operations"/> it supports, and whether/in which direction it is sortable
/// (<paramref name="SortSupport"/>). <paramref name="PagingSupport"/>, <paramref name="SupportsDisjunction"/>,
/// and <paramref name="SupportsTotalCount"/> are advisory metadata only: in the closed-query contract OR is
/// a universal query shape, total count rides with offset paging, and offset paging is a base store
/// capability, so these do not gate native support (see <c>ClosedQueryCapabilityModel</c>). They are
/// retained so manifests can describe intent without changing the gating contract.
/// </summary>
[Obsolete(
    "Use BoundedQueryDeclaration. Convert existing declarations with LegacyPhysicalStorageBridge.",
    DiagnosticId = "GW0003")]
public sealed record PortableQueryDeclaration(
    string Identity,
    string IndexIdentity,
    IReadOnlySet<PortableQueryOperation> Operations,
    QuerySortSupport SortSupport,
    QueryPagingSupport PagingSupport,
    bool SupportsDisjunction = false,
    bool SupportsTotalCount = false);

public enum QuerySortSupport
{
    None,
    Ascending,
    Descending,
    Both
}

public enum QueryPagingSupport
{
    None,
    Offset,
    Cursor
}
