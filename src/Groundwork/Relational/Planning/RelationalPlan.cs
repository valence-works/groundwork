using Groundwork.Core.Validation;
using Groundwork.Materialization;

namespace Groundwork.Relational.Planning;

public sealed record RelationalPlan(
    IReadOnlyList<RelationalTablePlan> Tables,
    MaterializationPlan MaterializationPlan)
{
    public IReadOnlyList<Groundwork.Core.Materialization.IProviderMaterializationOperation> Operations =>
        MaterializationPlan.Operations;
    public SchemaHistoryEntry SchemaHistory => MaterializationPlan.SchemaHistory;
    public IReadOnlyList<GroundworkDiagnostic> Diagnostics => MaterializationPlan.Diagnostics;
    public bool IsPlannable => MaterializationPlan.IsPlannable;
}

public sealed record RelationalTablePlan(
    string Name,
    IReadOnlyList<RelationalColumnPlan> Columns,
    IReadOnlyList<RelationalIndexPlan> Indexes);

public sealed record RelationalColumnPlan(string Name, string Role);

public sealed record RelationalIndexPlan(
    string Name,
    IReadOnlyList<string> Fields,
    bool IsUnique,
    bool IsSortable);
