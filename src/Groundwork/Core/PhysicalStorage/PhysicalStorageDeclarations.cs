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
        IReadOnlyList<PhysicalObjectNameOverride>? nameOverrides = null,
        IReadOnlyList<BoundedMutationDeclaration>? boundedMutations = null)
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
        BoundedMutations = boundedMutations?
            .OrderBy(x => x.Identity, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    public StorageUnitProvisioningMode ProvisioningMode { get; }

    public PhysicalStoragePolicy Policy { get; }

    public IReadOnlyList<LogicalIndexDeclaration> LogicalIndexes { get; }

    public IReadOnlyList<BoundedQueryDeclaration> BoundedQueries { get; }

    public IReadOnlyList<PhysicalObjectNameOverride> NameOverrides { get; }

    public IReadOnlyList<BoundedMutationDeclaration> BoundedMutations { get; }

    public bool Equals(StorageUnitPhysicalStorage? other) =>
        other is not null &&
        ProvisioningMode == other.ProvisioningMode &&
        Policy == other.Policy &&
        LogicalIndexes.SequenceEqual(other.LogicalIndexes) &&
        BoundedQueries.SequenceEqual(other.BoundedQueries) &&
        NameOverrides.SequenceEqual(other.NameOverrides) &&
        BoundedMutations.SequenceEqual(other.BoundedMutations);

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
        foreach (var mutation in BoundedMutations)
            hash.Add(mutation);
        return hash.ToHashCode();
    }
}

/// <summary>The closed set of effects available to a declared bounded mutation.</summary>
public enum BoundedMutationActionKind
{
    Transition,
    Delete
}

/// <summary>A fixed mutation effect selected by manifest code rather than runtime callers.</summary>
public abstract class BoundedMutationAction : IEquatable<BoundedMutationAction>
{
    protected BoundedMutationAction(BoundedMutationActionKind kind) => Kind = kind;

    public BoundedMutationActionKind Kind { get; }

    public static BoundedMutationAction Delete() => new BoundedDeleteMutationAction();

    public static BoundedMutationAction Transition(
        string path,
        IReadOnlyList<string> allowedSourceValues,
        string targetValue) =>
        new BoundedTransitionMutationAction(path, allowedSourceValues, targetValue);

    public abstract bool Equals(BoundedMutationAction? other);

    public override bool Equals(object? obj) => Equals(obj as BoundedMutationAction);

    public abstract override int GetHashCode();
}

/// <summary>Deletes every document selected by the declaration's closed bounded predicate.</summary>
public sealed class BoundedDeleteMutationAction() : BoundedMutationAction(BoundedMutationActionKind.Delete)
{
    public override bool Equals(BoundedMutationAction? other) => other is BoundedDeleteMutationAction;

    public override int GetHashCode() => (int)Kind;
}

/// <summary>Changes one stable field only from the declared source values to one declared target.</summary>
public sealed class BoundedTransitionMutationAction : BoundedMutationAction
{
    public BoundedTransitionMutationAction(
        string path,
        IReadOnlyList<string> allowedSourceValues,
        string targetValue)
        : base(BoundedMutationActionKind.Transition)
    {
        Path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A transition stable path is required.", nameof(path))
            : path;
        AllowedSourceValues = Array.AsReadOnly(
            (allowedSourceValues ?? throw new ArgumentNullException(nameof(allowedSourceValues)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());
        if (AllowedSourceValues.Count == 0 || AllowedSourceValues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty transition source value is required.", nameof(allowedSourceValues));
        TargetValue = string.IsNullOrWhiteSpace(targetValue)
            ? throw new ArgumentException("A transition target value is required.", nameof(targetValue))
            : targetValue;
        if (AllowedSourceValues.Contains(TargetValue, StringComparer.Ordinal))
            throw new ArgumentException("A transition target cannot also be an allowed source value.", nameof(targetValue));
    }

    public string Path { get; }

    public IReadOnlyList<string> AllowedSourceValues { get; }

    public string TargetValue { get; }

    public override bool Equals(BoundedMutationAction? other) =>
        other is BoundedTransitionMutationAction transition &&
        Path == transition.Path &&
        AllowedSourceValues.SequenceEqual(transition.AllowedSourceValues) &&
        TargetValue == transition.TargetValue;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Path, StringComparer.Ordinal);
        foreach (var source in AllowedSourceValues)
            hash.Add(source, StringComparer.Ordinal);
        hash.Add(TargetValue, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Binds a caller-visible mutation identity to one existing bounded-query declaration. The query
/// supplies the closed predicate shape; the action supplies the only effect the caller may invoke.
/// </summary>
public sealed class BoundedMutationDeclaration : IEquatable<BoundedMutationDeclaration>
{
    public BoundedMutationDeclaration(
        string identity,
        string predicateQueryIdentity,
        BoundedMutationAction action)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("A bounded-mutation identity is required.", nameof(identity))
            : identity;
        PredicateQueryIdentity = string.IsNullOrWhiteSpace(predicateQueryIdentity)
            ? throw new ArgumentException("A bounded predicate-query identity is required.", nameof(predicateQueryIdentity))
            : predicateQueryIdentity;
        Action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public string Identity { get; }

    public string PredicateQueryIdentity { get; }

    public BoundedMutationAction Action { get; }

    public bool Equals(BoundedMutationDeclaration? other) =>
        other is not null &&
        Identity == other.Identity &&
        PredicateQueryIdentity == other.PredicateQueryIdentity &&
        Action.Equals(other.Action);

    public override bool Equals(object? obj) => Equals(obj as BoundedMutationDeclaration);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Identity),
        StringComparer.Ordinal.GetHashCode(PredicateQueryIdentity),
        Action);
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

    public IndexValueKind GetValueKind(IndexField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.ValueKind ?? ValueKind;
    }

    public IndexValueKind GetValueKind(string path) =>
        GetValueKind(Fields.Single(field => field.Path == path));

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

/// <summary>
/// Declares an optional server-side predicate that is not part of the certified physical-index key.
/// Residual predicates remain closed, typed query-plan fields and execute before result operations,
/// paging limits, hydration, or materialization.
/// </summary>
public sealed class BoundedQueryResidualPredicateField : IEquatable<BoundedQueryResidualPredicateField>
{
    public BoundedQueryResidualPredicateField(
        string path,
        IndexValueKind valueKind,
        IReadOnlySet<PortableQueryOperation> operations,
        bool isRequired = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A stable residual predicate path is required.", nameof(path));

        Path = path;
        ValueKind = valueKind;
        Operations = (operations ?? throw new ArgumentNullException(nameof(operations))).ToFrozenSet();
        IsRequired = isRequired;
    }

    public string Path { get; }

    public IndexValueKind ValueKind { get; }

    public IReadOnlySet<PortableQueryOperation> Operations { get; }

    public bool IsRequired { get; }

    public bool Equals(BoundedQueryResidualPredicateField? other) =>
        other is not null &&
        Path == other.Path &&
        ValueKind == other.ValueKind &&
        IsRequired == other.IsRequired &&
        Operations.SetEquals(other.Operations);

    public override bool Equals(object? obj) => Equals(obj as BoundedQueryResidualPredicateField);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Path, StringComparer.Ordinal);
        hash.Add(ValueKind);
        hash.Add(IsRequired);
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
        string? latestPerKeyPath = null,
        IReadOnlyList<BoundedQueryResidualPredicateField>? residualPredicateFields = null)
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
        ResidualPredicateFields = Array.AsReadOnly(residualPredicateFields?.ToArray() ?? []);
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

    /// <summary>
    /// Optional typed predicate paths evaluated by the provider after indexed access selection and before
    /// every terminal operation. They are not physical-index prefix fields.
    /// </summary>
    public IReadOnlyList<BoundedQueryResidualPredicateField> ResidualPredicateFields { get; }

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
        ResidualPredicateFields.SequenceEqual(other.ResidualPredicateFields) &&
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
        foreach (var residualPredicateField in ResidualPredicateFields)
            hash.Add(residualPredicateField);
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
    IReadOnlyList<BoundedQueryResidualPredicateField> ResidualPredicateFields,
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
        ResidualPredicateFields.SequenceEqual(other.ResidualPredicateFields) &&
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
        foreach (var residualPredicateField in ResidualPredicateFields)
            hash.Add(residualPredicateField);
        foreach (var operation in ResultOperations.Order())
            hash.Add(operation);
        hash.Add(LatestPerKeyPath, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
