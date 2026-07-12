using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Materialization;

namespace Groundwork.Documents.Planning;

public sealed class DocumentManifestPlanner(MaterializationPlanner materializationPlanner)
{
    public DocumentPlan Plan(
        StorageManifest manifest,
        ProviderCapabilityReport runtimeCapabilities,
        MaterializationCapabilityReport materializationCapabilities)
    {
        var materializationPlan = materializationPlanner.Plan(manifest, runtimeCapabilities, materializationCapabilities);

        if (!materializationPlan.IsPlannable)
            return new DocumentPlan([], materializationPlan);

        var documents = manifest.StorageUnits
            .Select(unit => new DocumentStoragePlan(
                unit.Identity.Value,
                new DocumentEnvelopePlan(
                    unit.IdentityPolicy.FieldName,
                    unit.Concurrency.TokenField,
                    "storage_scope",
                    unit.Serialization.SchemaField),
                unit.Indexes
                    .Select(index => new DocumentIndexPlan(
                        index.Identity,
                        index.Fields.Select(field => field.Path).ToList(),
                        index.IsUnique,
                        index.IsSortable))
                    .ToList(),
                unit.Queries
                    .Select(query => new DocumentQueryPlan(
                        query.Identity,
                        query.IndexIdentity,
                        query.Operations.ToList()))
                    .ToList()))
            .ToList();

        return new DocumentPlan(documents, materializationPlan);
    }
}
