using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using System.Collections.Frozen;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>Identifies whether a storage unit is statically declared or created dynamically.</summary>
public enum StorageUnitProvisioningMode
{
    Declared,
    Dynamic
}

/// <summary>The three provider-neutral physical forms accepted by ADR 0003.</summary>
public enum PhysicalStorageForm
{
    SharedDocuments,
    DedicatedDocumentTable,
    PhysicalEntityTable
}

/// <summary>Identifies one manifest-owned shared document store.</summary>
public sealed record SharedStorageBinding(string Value);

/// <summary>
/// Declares a shared primary document store once at manifest/composition scope.
/// </summary>
public sealed record SharedDocumentStorageDefinition(
    SharedStorageBinding Binding,
    string FeatureDefaultLogicalName,
    DocumentEnvelopeDefinition Envelope,
    int SchemaVersion = 1,
    PhysicalEvolutionMetadata? Evolution = null);

/// <summary>Chooses default resolution or supplies an explicit physical definition.</summary>
public abstract record PhysicalStoragePolicy
{
    private PhysicalStoragePolicy()
    {
    }

    public static PhysicalStoragePolicy Default(SharedStorageBinding? sharedStorage = null) =>
        new DefaultPolicy(sharedStorage);

    public static PhysicalStoragePolicy Explicit(PhysicalTableDefinition definition) =>
        new ExplicitPolicy(definition ?? throw new ArgumentNullException(nameof(definition)));

    public sealed record DefaultPolicy(SharedStorageBinding? SharedStorage) : PhysicalStoragePolicy;

    public sealed record ExplicitPolicy(PhysicalTableDefinition Definition) : PhysicalStoragePolicy;
}

/// <summary>
/// Additive physical-storage declaration attached to a <see cref="StorageUnit"/>. It keeps the
/// legacy positional constructor intact while the bridge release supports both models.
/// </summary>
public sealed record StorageUnitPhysicalStorage
{
    public StorageUnitPhysicalStorage(
        StorageUnitProvisioningMode provisioningMode,
        PhysicalStoragePolicy policy,
        IReadOnlyList<LogicalIndexDeclaration>? logicalIndexes = null,
        IReadOnlyList<BoundedQueryDeclaration>? boundedQueries = null,
        IReadOnlyList<PhysicalObjectNameOverride>? nameOverrides = null)
    {
        ProvisioningMode = provisioningMode;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        LogicalIndexes = logicalIndexes?
            .OrderBy(x => x.Identity, StringComparer.Ordinal)
            .ToArray() ?? [];
        BoundedQueries = boundedQueries?
            .OrderBy(x => x.Identity, StringComparer.Ordinal)
            .ToArray() ?? [];
        NameOverrides = nameOverrides?
            .OrderBy(x => x.ObjectKind)
            .ThenBy(x => x.FeatureDefaultLogicalName, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    public StorageUnitProvisioningMode ProvisioningMode { get; }

    public PhysicalStoragePolicy Policy { get; }

    public IReadOnlyList<LogicalIndexDeclaration> LogicalIndexes { get; }

    public IReadOnlyList<BoundedQueryDeclaration> BoundedQueries { get; }

    public IReadOnlyList<PhysicalObjectNameOverride> NameOverrides { get; }

    public bool Equals(StorageUnitPhysicalStorage? other) =>
        other is not null &&
        ProvisioningMode == other.ProvisioningMode &&
        Policy == other.Policy &&
        LogicalIndexes.SequenceEqual(other.LogicalIndexes) &&
        BoundedQueries.SequenceEqual(other.BoundedQueries) &&
        NameOverrides.SequenceEqual(other.NameOverrides);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProvisioningMode);
        hash.Add(Policy);
        foreach (var index in LogicalIndexes)
            hash.Add(index);
        foreach (var query in BoundedQueries)
            hash.Add(query);
        foreach (var nameOverride in NameOverrides)
            hash.Add(nameOverride);
        return hash.ToHashCode();
    }
}

/// <summary>A provider-neutral logical index whose fields are stable serialized paths.</summary>
public sealed class LogicalIndexDeclaration : IEquatable<LogicalIndexDeclaration>
{
    public LogicalIndexDeclaration(
        string identity,
        IReadOnlyList<IndexField> fields,
        IndexValueKind valueKind,
        bool isUnique,
        MissingValueBehavior missingValueBehavior)
    {
        Identity = identity;
        Fields = fields?.ToArray() ?? throw new ArgumentNullException(nameof(fields));
        ValueKind = valueKind;
        IsUnique = isUnique;
        MissingValueBehavior = missingValueBehavior;
    }

    public string Identity { get; }

    public IReadOnlyList<IndexField> Fields { get; }

    public IndexValueKind ValueKind { get; }

    public bool IsUnique { get; }

    public MissingValueBehavior MissingValueBehavior { get; }

    public bool Equals(LogicalIndexDeclaration? other) =>
        other is not null &&
        Identity == other.Identity &&
        Fields.SequenceEqual(other.Fields) &&
        ValueKind == other.ValueKind &&
        IsUnique == other.IsUnique &&
        MissingValueBehavior == other.MissingValueBehavior;

    public override bool Equals(object? obj) => Equals(obj as LogicalIndexDeclaration);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Identity, StringComparer.Ordinal);
        foreach (var field in Fields)
            hash.Add(field);
        hash.Add(ValueKind);
        hash.Add(IsUnique);
        hash.Add(MissingValueBehavior);
        return hash.ToHashCode();
    }
}

/// <summary>States whether a bounded query is ordinary or part of the scale-bearing contract.</summary>
public enum BoundedQueryExecutionClass
{
    Ordinary,
    ScaleBearing
}

/// <summary>Declares the required direction for one stable path in compound query ordering.</summary>
public sealed record BoundedQuerySortField(
    string Path,
    PhysicalSortDirection Direction);

/// <summary>Declares the operators allowed for one stable predicate path.</summary>
public sealed class BoundedQueryPredicateField : IEquatable<BoundedQueryPredicateField>
{
    public BoundedQueryPredicateField(string path, IReadOnlySet<PortableQueryOperation> operations)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A stable predicate path is required.", nameof(path));

        Path = path;
        Operations = (operations ?? throw new ArgumentNullException(nameof(operations))).ToFrozenSet();
    }

    public string Path { get; }

    public IReadOnlySet<PortableQueryOperation> Operations { get; }

    public bool Equals(BoundedQueryPredicateField? other) =>
        other is not null && Path == other.Path && Operations.SetEquals(other.Operations);

    public override bool Equals(object? obj) => Equals(obj as BoundedQueryPredicateField);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Path, StringComparer.Ordinal);
        foreach (var operation in Operations.Order())
            hash.Add(operation);
        return hash.ToHashCode();
    }
}

/// <summary>The closed terminal operations a bounded document query may expose.</summary>
public enum BoundedQueryResultOperation
{
    Documents,
    Count,
    Any,
    First
}

/// <summary>
/// Declares a bounded query and references the logical index from which stable path demand is
/// resolved. Runtime query planning remains outside this declaration-only slice.
/// </summary>
public sealed class BoundedQueryDeclaration : IEquatable<BoundedQueryDeclaration>
{
    public BoundedQueryDeclaration(
        string identity,
        string indexIdentity,
        IReadOnlySet<PortableQueryOperation> operations,
        QuerySortSupport sortSupport,
        QueryPagingSupport pagingSupport,
        BoundedQueryExecutionClass executionClass = BoundedQueryExecutionClass.Ordinary,
        bool supportsDisjunction = false,
        bool supportsTotalCount = false,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null,
        IReadOnlyList<BoundedQueryPredicateField>? predicateFields = null,
        IReadOnlySet<BoundedQueryResultOperation>? resultOperations = null,
        string? latestPerKeyPath = null)
    {
        Identity = identity;
        IndexIdentity = indexIdentity;
        Operations = operations?.ToFrozenSet() ?? throw new ArgumentNullException(nameof(operations));
        SortSupport = sortSupport;
        PagingSupport = pagingSupport;
        ExecutionClass = executionClass;
        SupportsDisjunction = supportsDisjunction;
        SupportsTotalCount = supportsTotalCount;
        SortFields = Array.AsReadOnly(sortFields?.ToArray() ?? []);
        PredicateFields = Array.AsReadOnly(predicateFields?.ToArray() ?? []);
        ResultOperations = (resultOperations ?? DefaultResultOperations(supportsTotalCount)).ToFrozenSet();
        LatestPerKeyPath = latestPerKeyPath;
    }

    public string Identity { get; }

    public string IndexIdentity { get; }

    public IReadOnlySet<PortableQueryOperation> Operations { get; }

    public QuerySortSupport SortSupport { get; }

    public QueryPagingSupport PagingSupport { get; }

    public BoundedQueryExecutionClass ExecutionClass { get; }

    public bool SupportsDisjunction { get; }

    public bool SupportsTotalCount { get; }

    public IReadOnlyList<BoundedQuerySortField> SortFields { get; }

    /// <summary>
    /// Stable predicate paths and their allowed operations. An empty collection preserves the
    /// compatibility convention that the first field of the referenced logical index is filtered.
    /// </summary>
    public IReadOnlyList<BoundedQueryPredicateField> PredicateFields { get; }

    public IReadOnlySet<BoundedQueryResultOperation> ResultOperations { get; }

    public string? LatestPerKeyPath { get; }

    public bool Equals(BoundedQueryDeclaration? other) =>
        other is not null &&
        Identity == other.Identity &&
        IndexIdentity == other.IndexIdentity &&
        Operations.SetEquals(other.Operations) &&
        SortSupport == other.SortSupport &&
        PagingSupport == other.PagingSupport &&
        ExecutionClass == other.ExecutionClass &&
        SupportsDisjunction == other.SupportsDisjunction &&
        SupportsTotalCount == other.SupportsTotalCount &&
        SortFields.SequenceEqual(other.SortFields) &&
        PredicateFields.SequenceEqual(other.PredicateFields) &&
        ResultOperations.SetEquals(other.ResultOperations) &&
        LatestPerKeyPath == other.LatestPerKeyPath;

    public override bool Equals(object? obj) => Equals(obj as BoundedQueryDeclaration);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Identity, StringComparer.Ordinal);
        hash.Add(IndexIdentity, StringComparer.Ordinal);
        foreach (var operation in Operations.Order())
            hash.Add(operation);
        hash.Add(SortSupport);
        hash.Add(PagingSupport);
        hash.Add(ExecutionClass);
        hash.Add(SupportsDisjunction);
        hash.Add(SupportsTotalCount);
        foreach (var sortField in SortFields)
            hash.Add(sortField);
        foreach (var predicateField in PredicateFields)
            hash.Add(predicateField);
        foreach (var operation in ResultOperations.Order())
            hash.Add(operation);
        hash.Add(LatestPerKeyPath, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static IReadOnlySet<BoundedQueryResultOperation> DefaultResultOperations(bool supportsTotalCount)
    {
        var operations = new HashSet<BoundedQueryResultOperation>
        {
            BoundedQueryResultOperation.Documents,
            BoundedQueryResultOperation.Any,
            BoundedQueryResultOperation.First
        };
        if (supportsTotalCount)
            operations.Add(BoundedQueryResultOperation.Count);
        return operations;
    }
}

/// <summary>A stable path demanded by a scale-bearing bounded query.</summary>
public sealed record ScaleBearingPathDemand(
    string QueryIdentity,
    string IndexIdentity,
    string Path,
    PhysicalSortDirection SortDirection,
    IndexValueKind ValueKind,
    MissingValueBehavior MissingValueBehavior,
    IReadOnlyList<PortableQueryOperation> Operations,
    QuerySortSupport SortSupport,
    QueryPagingSupport PagingSupport,
    bool SupportsDisjunction,
    bool SupportsTotalCount,
    IReadOnlyList<BoundedQueryPredicateField> PredicateFields,
    IReadOnlyList<BoundedQueryResultOperation> ResultOperations,
    string? LatestPerKeyPath)
{
    public bool Equals(ScaleBearingPathDemand? other) =>
        other is not null &&
        QueryIdentity == other.QueryIdentity &&
        IndexIdentity == other.IndexIdentity &&
        Path == other.Path &&
        SortDirection == other.SortDirection &&
        ValueKind == other.ValueKind &&
        MissingValueBehavior == other.MissingValueBehavior &&
        Operations.Count == other.Operations.Count &&
        Operations.ToHashSet().SetEquals(other.Operations) &&
        SortSupport == other.SortSupport &&
        PagingSupport == other.PagingSupport &&
        SupportsDisjunction == other.SupportsDisjunction &&
        SupportsTotalCount == other.SupportsTotalCount &&
        PredicateFields.SequenceEqual(other.PredicateFields) &&
        ResultOperations.Count == other.ResultOperations.Count &&
        ResultOperations.ToHashSet().SetEquals(other.ResultOperations) &&
        LatestPerKeyPath == other.LatestPerKeyPath;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(QueryIdentity, StringComparer.Ordinal);
        hash.Add(IndexIdentity, StringComparer.Ordinal);
        hash.Add(Path, StringComparer.Ordinal);
        hash.Add(SortDirection);
        hash.Add(ValueKind);
        hash.Add(MissingValueBehavior);
        foreach (var operation in Operations.Order())
            hash.Add(operation);
        hash.Add(SortSupport);
        hash.Add(PagingSupport);
        hash.Add(SupportsDisjunction);
        hash.Add(SupportsTotalCount);
        foreach (var predicateField in PredicateFields)
            hash.Add(predicateField);
        foreach (var operation in ResultOperations.Order())
            hash.Add(operation);
        hash.Add(LatestPerKeyPath, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
