using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;

namespace Groundwork.Documents.Store;

/// <summary>
/// Resolves and validates the single-field declared indexes a <see cref="PortableDocumentQuery"/> addresses,
/// shared across providers so capability checks behave identically.
/// </summary>
public static class ClosedQueryIndexResolver
{
    /// <summary>Resolves a single-field index that declares support for the given operator.</summary>
    public static IndexDeclaration ResolveComparisonIndex(StorageUnit unit, string indexName, QueryComparisonOperator @operator)
    {
        var index = ResolveSingleFieldIndex(unit, indexName);
        if (!index.SupportedOperations.Contains(ToPortableOperation(@operator)))
            throw new UndeclaredDocumentIndexException(unit.Identity.Value, indexName);

        return index;
    }

    /// <summary>Resolves a single-field, sortable index used for ordering.</summary>
    public static IndexDeclaration ResolveOrderIndex(StorageUnit unit, string indexName)
    {
        var index = ResolveSingleFieldIndex(unit, indexName);
        if (!index.IsSortable)
            throw new UndeclaredDocumentIndexException(unit.Identity.Value, indexName);

        return index;
    }

    private static IndexDeclaration ResolveSingleFieldIndex(StorageUnit unit, string indexName)
    {
        var index = unit.Indexes.SingleOrDefault(index => index.Identity == indexName)
            ?? throw new UndeclaredDocumentIndexException(unit.Identity.Value, indexName);

        if (index.Fields.Count != 1)
            throw new UndeclaredDocumentIndexException(unit.Identity.Value, indexName);

        return index;
    }

    private static PortableQueryOperation ToPortableOperation(QueryComparisonOperator @operator) =>
        @operator switch
        {
            QueryComparisonOperator.Equal => PortableQueryOperation.Equal,
            QueryComparisonOperator.In => PortableQueryOperation.In,
            QueryComparisonOperator.Contains => PortableQueryOperation.Contains,
            QueryComparisonOperator.NotContains => PortableQueryOperation.NotContains,
            _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported comparison operator.")
        };
}
