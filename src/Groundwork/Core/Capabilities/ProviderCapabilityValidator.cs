using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Validation;

namespace Groundwork.Core.Capabilities;

public sealed class ProviderCapabilityValidator
{
    private readonly ICapabilityRegistry registry;

    /// <summary>Creates a validator backed by the built-in capability registry.</summary>
    public ProviderCapabilityValidator()
        : this(CapabilityRegistry.Default)
    {
    }

    /// <summary>
    /// Creates a validator backed by a custom registry (built-ins plus module contributions). The
    /// registry is the authority for whether a required capability id is recognized at all.
    /// </summary>
    public ProviderCapabilityValidator(ICapabilityRegistry registry) =>
        this.registry = registry;

    /// <summary>
    /// Derives the provider's fit for a manifest from storage requirements alone (plus the evidence
    /// policy). This is the single, capability-derived verdict — fit is never author-declared.
    /// </summary>
    public ProviderFit Evaluate(
        StorageManifest manifest,
        ProviderCapabilityReport capabilities,
        WorkloadEvidencePolicy? policy = null)
    {
        policy ??= WorkloadEvidencePolicy.Default;

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var needsEvidence = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var unit in manifest.StorageUnits)
        {
            foreach (var requirement in unit.Intent.Requirements)
            {
                if (!capabilities.SupportedCapabilities.Contains(requirement))
                    missing.Add(requirement.Value);
                else if (policy.EvidenceGatedCapabilities.Contains(requirement) &&
                         !capabilities.EvidencedCapabilities.Contains(requirement))
                    needsEvidence.Add(requirement.Value);
            }
        }

        if (missing.Count != 0)
            return new ProviderFit.Unsupported(missing.Select(value => new CapabilityId(value)).ToList());

        if (needsEvidence.Count != 0)
            return new ProviderFit.RequiresEvidence(needsEvidence.Select(EvidenceReason).ToList());

        return new ProviderFit.Supported();
    }

    public CapabilityCompatibilityResult Validate(StorageManifest manifest, ProviderCapabilityReport capabilities)
    {
        var diagnostics = new List<GroundworkDiagnostic>();

        if (!capabilities.SupportsSchemaHistory)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-CAP-001", "Provider must support schema history for materializable plans.", "provider.schemaHistory"));

        foreach (var warning in capabilities.Warnings)
            diagnostics.Add(GroundworkDiagnostic.Warning("GW-CAP-002", warning, "provider.warnings"));

        ValidateRequiredCapabilities(manifest, capabilities, diagnostics);

        foreach (var unit in manifest.StorageUnits)
            ValidateUnit(unit, capabilities, diagnostics);

        return diagnostics.Count == 0
            ? CapabilityCompatibilityResult.Compatible
            : new CapabilityCompatibilityResult(diagnostics);
    }

    private ProviderFit EvaluateUnit(StorageUnit unit, ProviderCapabilityReport capabilities, WorkloadEvidencePolicy policy)
    {
        var missing = unit.Intent.Requirements
            .Where(requirement => !capabilities.SupportedCapabilities.Contains(requirement))
            .OrderBy(requirement => requirement.Value, StringComparer.Ordinal)
            .ToList();

        if (missing.Count != 0)
            return new ProviderFit.Unsupported(missing);

        var needsEvidence = UnitNeedsEvidence(unit, capabilities, policy).ToList();
        if (needsEvidence.Count != 0)
            return new ProviderFit.RequiresEvidence(needsEvidence.Select(requirement => EvidenceReason(requirement.Value)).ToList());

        return new ProviderFit.Supported();
    }

    private static IEnumerable<CapabilityId> UnitNeedsEvidence(StorageUnit unit, ProviderCapabilityReport capabilities, WorkloadEvidencePolicy policy) =>
        unit.Intent.Requirements
            .Where(requirement =>
                policy.EvidenceGatedCapabilities.Contains(requirement) &&
                !capabilities.EvidencedCapabilities.Contains(requirement))
            .OrderBy(requirement => requirement.Value, StringComparer.Ordinal);

    private static string EvidenceReason(string requirement) =>
        $"Requirement '{requirement}' is evidence-gated; the provider must supply benchmark or operational evidence before serving it.";

    private static void ValidateRequiredCapabilities(StorageManifest manifest, ProviderCapabilityReport capabilities, List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var requiredCapability in manifest.RequiredCapabilities)
        {
            switch (requiredCapability)
            {
                case "schema-history":
                    break;
                case "optimistic-concurrency" when !capabilities.SupportedConcurrencyModes.Contains(ConcurrencyKind.Optimistic):
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-CAP-005",
                        "Provider does not support required manifest capability 'optimistic-concurrency'.",
                        "requiredCapabilities.optimistic-concurrency"));
                    break;
                case "optimistic-concurrency":
                    break;
                default:
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-CAP-012",
                        $"Manifest requires unknown provider capability '{requiredCapability}'.",
                        $"requiredCapabilities.{requiredCapability}"));
                    break;
            }
        }
    }

    private void ValidateUnit(StorageUnit unit, ProviderCapabilityReport capabilities, List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var requirement in unit.Intent.Requirements)
        {
            if (!registry.IsRegistered(requirement))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CAP-014",
                    $"Storage unit '{unit.Identity.Value}' requires unregistered capability '{requirement}'. Register it via an IGroundworkModule before validating.",
                    $"storageUnits.{unit.Identity}.intent.requirements"));
            }
        }

        switch (EvaluateUnit(unit, capabilities, WorkloadEvidencePolicy.FromRegistry(registry)))
        {
            case ProviderFit.Unsupported unsupported:
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CAP-004",
                    $"Provider does not support storage requirements: {string.Join(", ", unsupported.MissingRequirements.Select(requirement => requirement.Value))}.",
                    $"storageUnits.{unit.Identity}.intent.requirements"));
                break;
            case ProviderFit.RequiresEvidence requiresEvidence:
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CAP-013",
                    $"Provider requires evidence before serving storage unit '{unit.Identity.Value}': {string.Join(" ", requiresEvidence.Reasons)}",
                    $"storageUnits.{unit.Identity}.intent.requirements"));
                break;
        }

        if (!capabilities.SupportedConcurrencyModes.Contains(unit.Concurrency.Kind))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-005",
                $"Provider does not support concurrency mode '{unit.Concurrency.Kind}'.",
                $"storageUnits.{unit.Identity}.concurrency"));
        }

        ValidateMaterializationOperations(unit, capabilities, diagnostics);

        foreach (var index in unit.Indexes)
            ValidateIndex(unit, index, capabilities, diagnostics);
    }

    private static void ValidateMaterializationOperations(StorageUnit unit, ProviderCapabilityReport capabilities, List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var operation in RequiredMaterializationOperations(unit))
        {
            if (capabilities.SupportedMaterializationOperations.Contains(operation))
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-011",
                $"Provider does not support materialization operation '{operation}' required by storage unit '{unit.Identity.Value}'.",
                $"storageUnits.{unit.Identity}.materialization"));
        }
    }

    private static IEnumerable<MaterializationOperationKind> RequiredMaterializationOperations(StorageUnit unit)
    {
        yield return MaterializationOperationKind.CreateStorageUnit;

        if (unit.Indexes.Count != 0)
            yield return MaterializationOperationKind.CreateIndex;

        if (PhysicalizationProjection.EligibleFields(unit).Count != 0)
            yield return MaterializationOperationKind.CreateOptimizedProjection;
    }

    private static void ValidateIndex(StorageUnit unit, IndexDeclaration index, ProviderCapabilityReport capabilities, List<GroundworkDiagnostic> diagnostics)
    {
        if (!capabilities.Indexes.SupportedValueKinds.Contains(index.ValueKind))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-006",
                $"Provider does not support index value kind '{index.ValueKind}'.",
                $"storageUnits.{unit.Identity}.indexes.{index.Identity}.valueKind"));
        }

        if (index.IsUnique && !capabilities.Indexes.SupportsUniqueIndexes)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-007",
                $"Provider does not support unique index '{index.Identity}'.",
                $"storageUnits.{unit.Identity}.indexes.{index.Identity}.unique"));
        }

        if (index.IsSortable && !capabilities.Indexes.SupportsSortableIndexes)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-008",
                $"Provider does not support sortable index '{index.Identity}'.",
                $"storageUnits.{unit.Identity}.indexes.{index.Identity}.sortable"));
        }

        if (!capabilities.Indexes.SupportedMissingValueBehaviors.Contains(index.MissingValueBehavior))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-009",
                $"Provider does not support missing value behavior '{index.MissingValueBehavior}'.",
                $"storageUnits.{unit.Identity}.indexes.{index.Identity}.missingValueBehavior"));
        }

        var unsupportedOperations = index.SupportedOperations.Except(capabilities.SupportedQueryOperations).ToList();
        if (unsupportedOperations.Count != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-CAP-010",
                $"Provider does not support query operations required by index '{index.Identity}': {string.Join(", ", unsupportedOperations)}.",
                $"storageUnits.{unit.Identity}.indexes.{index.Identity}.operations"));
        }
    }
}
