using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;

namespace Groundwork.Core.Validation;

public sealed class StorageManifestValidator
{
    public ManifestValidationResult Validate(StorageManifest manifest)
    {
        var diagnostics = new List<GroundworkDiagnostic>();

        ValidateRequired(manifest.Identity.Value, "GW-MANIFEST-001", "Manifest identity is required.", "manifest.identity", diagnostics);
        ValidateRequired(manifest.Owner.Value, "GW-MANIFEST-002", "Manifest owner is required.", "manifest.owner", diagnostics);
        ValidateRequired(manifest.Version.Value, "GW-MANIFEST-003", "Manifest version is required.", "manifest.version", diagnostics);

        if (manifest.StorageUnits.Count == 0)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-MANIFEST-004", "Manifest must declare at least one storage unit.", "manifest.storageUnits"));

        AddDuplicateDiagnostics(
            manifest.StorageUnits.Select(unit => unit.Identity.Value),
            "GW-MANIFEST-005",
            "Storage unit identities must be unique within a manifest.",
            "manifest.storageUnits",
            diagnostics);

        for (var unitIndex = 0; unitIndex < manifest.StorageUnits.Count; unitIndex++)
            ValidateStorageUnit(manifest.StorageUnits[unitIndex], unitIndex, diagnostics);

        return diagnostics.Count == 0 ? ManifestValidationResult.Success : new ManifestValidationResult(diagnostics);
    }

    private static void ValidateStorageUnit(StorageUnit unit, int unitIndex, List<GroundworkDiagnostic> diagnostics)
    {
        var target = $"manifest.storageUnits[{unitIndex}]";
        ValidateRequired(unit.Identity.Value, "GW-UNIT-001", "Storage unit identity is required.", $"{target}.identity", diagnostics);

        if (ProviderNeutralityRules.LooksProviderSpecific(unit.Identity.Value))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-002",
                "Storage unit identity must describe provider-neutral intent, not provider-specific physical shape.",
                $"{target}.identity"));
        }

        if (unit.Intent is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-003", "Storage unit intent is required.", $"{target}.intent"));
        }
        else
        {
            ValidateStorageIntent(unit.Intent, target, diagnostics);
        }

        if (unit.Lifecycle is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-006", "Storage unit lifecycle policy is required.", $"{target}.lifecycle"));

        if (IdentityPolicyAdmission.Validate(
                unit.IdentityPolicy,
                $"{target}.identityPolicy") is { } identityPolicyDiagnostic)
        {
            diagnostics.Add(identityPolicyDiagnostic);
        }

        if (unit.Tenancy is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-011", "Storage unit tenancy policy is required.", $"{target}.tenancy"));
        else if (unit.Tenancy.Kind is not TenancyKind.Global and not TenancyKind.Scoped)
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-012",
                $"Tenancy policy '{unit.Tenancy.Kind}' has no executable storage-scope handler.",
                $"{target}.tenancy"));

        if (unit.Concurrency is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-008", "Storage unit concurrency policy is required.", $"{target}.concurrency"));

        if (unit.Serialization is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-009", "Storage unit serialization policy is required.", $"{target}.serialization"));

        if (unit.Physicalization is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-010", "Storage unit physicalization policy is required.", $"{target}.physicalization"));

        AddDuplicateDiagnostics(
            unit.Indexes.Select(index => index.Identity),
            "GW-INDEX-001",
            "Index identities must be unique within a storage unit.",
            $"{target}.indexes",
            diagnostics);

        ValidateIndexes(unit, target, diagnostics);
        ValidateQueries(unit, target, diagnostics);
    }

    private static void ValidateStorageIntent(StorageIntent intent, string unitTarget, List<GroundworkDiagnostic> diagnostics)
    {
        if (intent.Requirements.Count != 0 && string.IsNullOrWhiteSpace(intent.Rationale))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-005",
                "Storage intents that declare requirements must provide a rationale.",
                $"{unitTarget}.intent.rationale"));
        }
    }

    private static void ValidateIndexes(StorageUnit unit, string unitTarget, List<GroundworkDiagnostic> diagnostics)
    {
        for (var indexIndex = 0; indexIndex < unit.Indexes.Count; indexIndex++)
        {
            var index = unit.Indexes[indexIndex];
            var target = $"{unitTarget}.indexes[{indexIndex}]";

            ValidateRequired(index.Identity, "GW-INDEX-002", "Index identity is required.", $"{target}.identity", diagnostics);

            if (index.Fields.Count == 0)
                diagnostics.Add(GroundworkDiagnostic.Error("GW-INDEX-003", "Index must declare at least one field.", $"{target}.fields"));

            if (index.Fields.Count > 1)
                diagnostics.Add(GroundworkDiagnostic.Error("GW-INDEX-006", "Compound indexes are not supported by the portable persistence contract yet.", $"{target}.fields"));

            if (index.Fields.Any(field => string.IsNullOrWhiteSpace(field.Path)))
                diagnostics.Add(GroundworkDiagnostic.Error("GW-INDEX-004", "Index field paths are required.", $"{target}.fields"));

            if (index.IsUnique && index.MissingValueBehavior == MissingValueBehavior.IncludedAsNull)
            {
                diagnostics.Add(GroundworkDiagnostic.Warning(
                    "GW-INDEX-005",
                    "Unique index includes missing values as null; providers may enforce this differently.",
                    $"{target}.missingValueBehavior"));
            }
        }
    }

    private static void ValidateQueries(StorageUnit unit, string unitTarget, List<GroundworkDiagnostic> diagnostics)
    {
        var indexIdentities = unit.Indexes.Select(index => index.Identity).ToHashSet(StringComparer.Ordinal);

        for (var queryIndex = 0; queryIndex < unit.Queries.Count; queryIndex++)
        {
            var query = unit.Queries[queryIndex];
            var target = $"{unitTarget}.queries[{queryIndex}]";

            ValidateRequired(query.Identity, "GW-QUERY-001", "Query identity is required.", $"{target}.identity", diagnostics);

            if (!indexIdentities.Contains(query.IndexIdentity))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-QUERY-002",
                    $"Portable query '{query.Identity}' references undeclared index '{query.IndexIdentity}'.",
                    $"{target}.indexIdentity"));
                continue;
            }

            var index = unit.Indexes.Single(declaredIndex => declaredIndex.Identity == query.IndexIdentity);
            var unsupportedOperations = query.Operations.Except(index.SupportedOperations).ToList();
            if (unsupportedOperations.Count != 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-QUERY-003",
                    $"Portable query '{query.Identity}' requires operations not supported by index '{query.IndexIdentity}': {string.Join(", ", unsupportedOperations)}.",
                    $"{target}.operations"));
            }

            if (query.SortSupport != QuerySortSupport.None && !index.IsSortable)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-QUERY-004",
                    $"Portable query '{query.Identity}' declares ordering support but index '{query.IndexIdentity}' is not sortable.",
                    $"{target}.sortSupport"));
            }
        }
    }

    private static void ValidateRequired(
        string? value,
        string code,
        string message,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
            diagnostics.Add(GroundworkDiagnostic.Error(code, message, target));
    }

    private static void AddDuplicateDiagnostics(
        IEnumerable<string> values,
        string code,
        string message,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (values.Where(value => !string.IsNullOrWhiteSpace(value)).GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
            diagnostics.Add(GroundworkDiagnostic.Error(code, message, target));
    }
}
