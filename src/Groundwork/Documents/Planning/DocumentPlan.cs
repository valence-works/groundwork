using Groundwork.Core.Indexing;
using Groundwork.Core.Validation;
using Groundwork.Materialization;

namespace Groundwork.Documents.Planning;

public sealed record DocumentPlan(
    IReadOnlyList<DocumentStoragePlan> Documents,
    MaterializationPlan MaterializationPlan)
{
    public IReadOnlyList<MaterializationOperation> Operations => MaterializationPlan.Operations;
    public SchemaHistoryEntry SchemaHistory => MaterializationPlan.SchemaHistory;
    public IReadOnlyList<GroundworkDiagnostic> Diagnostics => MaterializationPlan.Diagnostics;
    public bool IsPlannable => MaterializationPlan.IsPlannable;
}

public sealed record DocumentStoragePlan(
    string DocumentKind,
    DocumentEnvelopePlan Envelope,
    IReadOnlyList<DocumentIndexPlan> Indexes,
    IReadOnlyList<DocumentQueryPlan> Queries);

public sealed record DocumentEnvelopePlan(
    string IdentityField,
    string? ConcurrencyField,
    string? StorageScopeField,
    string? SchemaField);

public sealed record DocumentIndexPlan(
    string Name,
    IReadOnlyList<string> Fields,
    bool IsUnique,
    bool IsSortable);

public sealed record DocumentQueryPlan(
    string Name,
    string IndexName,
    IReadOnlyList<PortableQueryOperation> Operations);
