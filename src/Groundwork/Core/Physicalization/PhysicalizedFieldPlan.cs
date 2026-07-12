using Groundwork.Core.Indexing;

namespace Groundwork.Core.Physicalization;

[Obsolete(
    "Use ProjectedColumnDefinition within PhysicalTableDefinition.",
    DiagnosticId = "GW0004")]
public sealed record PhysicalizedFieldPlan(
    string Name,
    string Path,
    IndexValueKind ValueKind,
    bool IsUnique,
    bool IsSortable);
