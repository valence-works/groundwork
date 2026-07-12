using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Validation;

namespace Groundwork.Materialization;

public sealed class MaterializationPlanner
{
    private readonly StorageManifestValidator manifestValidator;
    private readonly ProviderCapabilityValidator capabilityValidator;

    public MaterializationPlanner(
        StorageManifestValidator manifestValidator,
        ProviderCapabilityValidator capabilityValidator)
    {
        this.manifestValidator = manifestValidator;
        this.capabilityValidator = capabilityValidator;
    }

    public MaterializationPlan Plan(
        StorageManifest manifest,
        ProviderCapabilityReport runtimeCapabilities,
        MaterializationCapabilityReport materializationCapabilities)
    {
        var diagnostics = new List<GroundworkDiagnostic>();
        diagnostics.AddRange(manifestValidator.Validate(manifest).Diagnostics);

        if (runtimeCapabilities.Provider != materializationCapabilities.Provider)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MAT-003",
                $"Runtime provider '{runtimeCapabilities.Provider}' does not match materialization provider '{materializationCapabilities.Provider}'.",
                "provider"));
        }

        if (diagnostics.All(diagnostic => !diagnostic.IsError))
            diagnostics.AddRange(capabilityValidator.ValidateRuntimeFit(manifest, runtimeCapabilities).Diagnostics);

        var operations = diagnostics.Any(diagnostic => diagnostic.IsError)
            ? []
            : DeriveOperations(manifest);

        if (operations.Count != 0)
            ValidateMaterializationCapabilities(operations, materializationCapabilities, diagnostics);
        else if (!materializationCapabilities.SupportsSchemaHistory)
            diagnostics.Add(SchemaHistoryUnsupportedDiagnostic());

        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return CreateUnplannablePlan(manifest, materializationCapabilities.Provider, diagnostics);

        var schemaHistory = CreateSchemaHistory(manifest, materializationCapabilities.Provider, operations);
        var plannedOperations = operations.Concat([new RecordSchemaHistoryOperation(schemaHistory)]).ToList();

        return new MaterializationPlan(
            materializationCapabilities.Provider,
            manifest.Identity,
            manifest.Version,
            plannedOperations,
            schemaHistory,
            diagnostics);
    }

    private static void ValidateMaterializationCapabilities(
        IReadOnlyList<MaterializationOperation> operations,
        MaterializationCapabilityReport capabilities,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (!capabilities.SupportsSchemaHistory)
            diagnostics.Add(SchemaHistoryUnsupportedDiagnostic());

        var requiredOperations = operations
            .Select(operation => operation.Kind)
            .Append(MaterializationOperationKind.RecordSchemaHistory)
            .Distinct()
            .OrderBy(operation => operation.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var operation in requiredOperations)
        {
            if (capabilities.SupportedOperations.Contains(operation))
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-MAT-002",
                $"Provider '{capabilities.Provider}' does not support materialization operation '{operation}'.",
                $"materialization.operations.{operation}"));
        }
    }

    private static IReadOnlyList<MaterializationOperation> DeriveOperations(StorageManifest manifest)
    {
        var operations = new List<MaterializationOperation>();

        foreach (var unit in manifest.StorageUnits)
        {
            operations.Add(new CreateStorageUnitOperation(
                new MaterializedStorageUnit(
                    unit.Identity.Value,
                    unit.IdentityPolicy.FieldName,
                    unit.Concurrency.TokenField,
                    "storage_scope",
                    unit.Serialization.SchemaField)));

            operations.AddRange(unit.Indexes.Select(index => new CreateIndexOperation(
                new MaterializedIndex(
                    unit.Identity.Value,
                    index.Identity,
                    index.Fields.Select(field => field.Path).ToList(),
                    index.ValueKind,
                    index.IsUnique,
                    index.IsSortable,
                    index.MissingValueBehavior))));

            var projectionFields = PhysicalizationProjection.EligibleFields(unit);
            if (projectionFields.Count != 0)
                operations.Add(new CreateOptimizedProjectionOperation(new MaterializedProjection(unit.Identity.Value, projectionFields)));
        }

        return operations;
    }

    private static MaterializationPlan CreateUnplannablePlan(
        StorageManifest manifest,
        ProviderIdentity provider,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        var schemaHistory = new SchemaHistoryEntry(manifest.Identity, manifest.Version, provider, DateTimeOffset.UnixEpoch, []);
        return new MaterializationPlan(provider, manifest.Identity, manifest.Version, [], schemaHistory, diagnostics);
    }

    private static SchemaHistoryEntry CreateSchemaHistory(
        StorageManifest manifest,
        ProviderIdentity provider,
        IReadOnlyList<MaterializationOperation> operations) =>
        new(
            manifest.Identity,
            manifest.Version,
            provider,
            DateTimeOffset.UtcNow,
            operations.Select(operation => operation.Target).ToList());

    private static GroundworkDiagnostic SchemaHistoryUnsupportedDiagnostic() =>
        GroundworkDiagnostic.Error(
            "GW-MAT-001",
            "Provider must support schema history for materialization planning.",
            "materialization.schemaHistory");
}
