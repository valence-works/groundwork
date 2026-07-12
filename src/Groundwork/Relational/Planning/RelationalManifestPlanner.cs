using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;

namespace Groundwork.Relational.Planning;

public sealed class RelationalManifestPlanner(MaterializationPlanner materializationPlanner)
{
    public RelationalPlan Plan(
        StorageManifest manifest,
        ProviderCapabilityReport runtimeCapabilities,
        MaterializationCapabilityReport materializationCapabilities)
    {
        var materializationPlan = materializationPlanner.Plan(manifest, runtimeCapabilities, materializationCapabilities);

        if (!materializationPlan.IsPlannable)
            return new RelationalPlan([], materializationPlan);

        var tables = manifest.StorageUnits
            .Select(unit => new RelationalTablePlan(
                unit.Identity.Value,
                CreateColumns(unit),
                unit.Indexes
                    .Select(index => new RelationalIndexPlan(
                        index.Identity,
                        index.Fields.Select(field => field.Path).ToList(),
                        index.IsUnique,
                        index.IsSortable))
                    .ToList()))
            .ToList();

        return new RelationalPlan(tables, materializationPlan);
    }

    private static IReadOnlyList<RelationalColumnPlan> CreateColumns(StorageUnit unit)
    {
        var columns = new List<RelationalColumnPlan>
        {
            new(unit.IdentityPolicy.FieldName, "Identity")
        };

        if (unit.Concurrency.TokenField is not null)
            columns.Add(new RelationalColumnPlan(unit.Concurrency.TokenField, "Concurrency"));

        columns.Add(new RelationalColumnPlan("storage_scope", "StorageScope"));

        columns.AddRange(unit.Indexes.SelectMany(index => index.Fields).Select(field => new RelationalColumnPlan(field.Path, "Index")));
        return columns.DistinctBy(column => column.Name).ToList();
    }

}
