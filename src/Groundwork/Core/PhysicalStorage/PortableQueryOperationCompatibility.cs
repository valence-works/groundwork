using Groundwork.Core.Indexing;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Provider-neutral operation/type compatibility for executable physical queries. Providers may
/// support fewer combinations, but may not certify combinations excluded by this closed surface.
/// </summary>
public static class PortableQueryOperationCompatibility
{
    /// <summary>
    /// Returns whether a provider-neutral projected type preserves the declared logical index
    /// value semantics. This is deliberately narrower than storage convertibility: an integer can
    /// be rendered as text, for example, but doing so would change numeric ordering into lexical
    /// ordering.
    /// </summary>
    public static bool Supports(IndexValueKind valueKind, PortablePhysicalType physicalType) => valueKind switch
    {
        IndexValueKind.String => physicalType == PortablePhysicalType.String,
        IndexValueKind.Keyword => physicalType is
            PortablePhysicalType.String or
            PortablePhysicalType.Guid or
            PortablePhysicalType.Json or
            PortablePhysicalType.Binary,
        IndexValueKind.Number => physicalType is
            PortablePhysicalType.Int32 or
            PortablePhysicalType.Int64 or
            PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => physicalType == PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => physicalType == PortablePhysicalType.DateTime,
        _ => false
    };

    public static bool Supports(IndexValueKind valueKind, PortableQueryOperation operation) => operation switch
    {
        PortableQueryOperation.Equal or
        PortableQueryOperation.NotEqual or
        PortableQueryOperation.In => true,
        PortableQueryOperation.Contains or
        PortableQueryOperation.StartsWith => valueKind is IndexValueKind.String or IndexValueKind.Keyword,
        PortableQueryOperation.GreaterThan or
        PortableQueryOperation.GreaterThanOrEqual or
        PortableQueryOperation.LessThan or
        PortableQueryOperation.LessThanOrEqual => valueKind is
            IndexValueKind.String or
            IndexValueKind.Keyword or
            IndexValueKind.Number or
            IndexValueKind.DateTime,
        _ => false
    };

    public static bool Supports(PortablePhysicalType physicalType, PortableQueryOperation operation) => physicalType switch
    {
        PortablePhysicalType.String => Supports(IndexValueKind.String, operation),
        PortablePhysicalType.Int32 or
        PortablePhysicalType.Int64 or
        PortablePhysicalType.Decimal => Supports(IndexValueKind.Number, operation),
        PortablePhysicalType.Boolean => Supports(IndexValueKind.Boolean, operation),
        PortablePhysicalType.DateTime => Supports(IndexValueKind.DateTime, operation),
        PortablePhysicalType.Guid or
        PortablePhysicalType.Json or
        PortablePhysicalType.Binary => operation is
            PortableQueryOperation.Equal or
            PortableQueryOperation.NotEqual or
            PortableQueryOperation.In,
        _ => false
    };
}
