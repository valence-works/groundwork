using Groundwork.Core.Indexing;
using Groundwork.Core.Queries;

namespace Groundwork.Core.PhysicalStorage;

internal enum PhysicalQueryIdentityEvidenceDemand
{
    None,
    Exact,
    Ordered,
    Mixed
}

internal static class PhysicalQueryIdentityDemand
{
    public static PhysicalQueryIdentityEvidenceDemand Resolve(
        IReadOnlySet<PortableQueryOperation> operations)
    {
        var exact = operations.Any(IsExact);
        var ordered = operations.Any(IsOrdered);
        return (exact, ordered) switch
        {
            (true, true) => PhysicalQueryIdentityEvidenceDemand.Mixed,
            (true, false) => PhysicalQueryIdentityEvidenceDemand.Exact,
            (false, true) => PhysicalQueryIdentityEvidenceDemand.Ordered,
            _ => PhysicalQueryIdentityEvidenceDemand.None
        };
    }

    public static PhysicalQueryIdentityEvidenceDemand Resolve(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query)
    {
        var predicate = BoundedQueryPredicateResolver.Resolve(query, logicalIndex)
            .SingleOrDefault(field => field.Path == PhysicalDocumentFieldPaths.Id)?
            .Operations;
        if (predicate is not null)
            return Resolve(predicate);

        var ordersByIdentity = query.SortFields.Any(field => field.Path == PhysicalDocumentFieldPaths.Id) ||
                               (query.SortFields.Count == 0 &&
                                query.SortSupport != QuerySortSupport.None &&
                                logicalIndex.Fields.Any(field => field.Path == PhysicalDocumentFieldPaths.Id));
        return ordersByIdentity
            ? PhysicalQueryIdentityEvidenceDemand.Ordered
            : PhysicalQueryIdentityEvidenceDemand.None;
    }

    private static bool IsExact(PortableQueryOperation operation) => operation is
        PortableQueryOperation.Equal or
        PortableQueryOperation.In or
        PortableQueryOperation.NotEqual;

    private static bool IsOrdered(PortableQueryOperation operation) => operation is
        PortableQueryOperation.StartsWith or
        PortableQueryOperation.GreaterThan or
        PortableQueryOperation.GreaterThanOrEqual or
        PortableQueryOperation.LessThan or
        PortableQueryOperation.LessThanOrEqual;
}
