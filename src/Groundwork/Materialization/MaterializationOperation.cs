using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Materialization;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Text;

namespace Groundwork.Materialization;

public abstract record MaterializationOperation : IProviderMaterializationOperation
{
    public abstract MaterializationOperationKind Kind { get; }
    public abstract string Target { get; }
}

public sealed record CreateStorageUnitOperation(MaterializedStorageUnit StorageUnit) : MaterializationOperation
{
    public override MaterializationOperationKind Kind => MaterializationOperationKind.CreateStorageUnit;
    public override string Target => StorageUnit.Identity;
}

public sealed record CreateIndexOperation(MaterializedIndex Index) : MaterializationOperation
{
    public override MaterializationOperationKind Kind => MaterializationOperationKind.CreateIndex;
    public override string Target => $"{Index.UnitIdentity}.{Index.Identity}";
}

public sealed record CreateOptimizedProjectionOperation(MaterializedProjection Projection) : MaterializationOperation
{
    public override MaterializationOperationKind Kind => MaterializationOperationKind.CreateOptimizedProjection;
    public override string Target => $"{Projection.UnitIdentity}.optimized-projection";
}

public sealed record RecordSchemaHistoryOperation(SchemaHistoryEntry Entry) : MaterializationOperation
{
    public override MaterializationOperationKind Kind => MaterializationOperationKind.RecordSchemaHistory;
    public override string Target => Entry.ManifestIdentity.Value;
}

public sealed record MaterializedStorageUnit(
    string Identity,
    string IdentityField,
    string? ConcurrencyField,
    string? StorageScopeField,
    string? SchemaField,
    IdentityPolicy IdentityPolicy)
{
    public StringIdentityCasePolicy StringIdentityCasePolicy => IdentityPolicy.StringCasePolicy;

    public string ComparisonAlgorithmId => PortableStringComparison.GetAlgorithmId(
        PortableStringComparison.ForIdentityPolicy(StringIdentityCasePolicy));

    public string LookupAlgorithmId => PortableStringComparison.LookupHashAlgorithmId;
}

public sealed record MaterializedIndex(
    string UnitIdentity,
    string Identity,
    IReadOnlyList<string> FieldPaths,
    IndexValueKind ValueKind,
    bool IsUnique,
    bool IsSortable,
    MissingValueBehavior MissingValueBehavior);

public sealed record MaterializedProjection(
    string UnitIdentity,
    IReadOnlyList<PhysicalizedFieldPlan> Fields);
