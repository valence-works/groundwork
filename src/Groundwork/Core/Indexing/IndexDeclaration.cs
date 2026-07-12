namespace Groundwork.Core.Indexing;

[Obsolete(
    "Use LogicalIndexDeclaration for logical lookup intent and PhysicalIndexDefinition for physical structure.",
    DiagnosticId = "GW0002")]
public sealed record IndexDeclaration(
    string Identity,
    IReadOnlyList<IndexField> Fields,
    IndexValueKind ValueKind,
    bool IsUnique,
    bool IsSortable,
    MissingValueBehavior MissingValueBehavior,
    IReadOnlySet<PortableQueryOperation> SupportedOperations,
    IndexPhysicalizationPolicy Physicalization = IndexPhysicalizationPolicy.Default);

public sealed record IndexField(string Path);

public enum IndexValueKind
{
    String,
    Number,
    Boolean,
    DateTime,
    Keyword
}

public enum MissingValueBehavior
{
    Excluded,
    IncludedAsNull
}

public enum PortableQueryOperation
{
    Equal,
    NotEqual,
    StartsWith,
    Contains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In
}

[Obsolete(
    "Physical placement belongs to PhysicalTableDefinition. Convert existing declarations with LegacyPhysicalStorageBridge.",
    DiagnosticId = "GW0002")]
public enum IndexPhysicalizationPolicy
{
    Default,
    Portable,
    Optimized
}
