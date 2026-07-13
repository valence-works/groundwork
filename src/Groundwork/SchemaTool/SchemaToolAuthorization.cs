using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;

namespace Groundwork.SchemaTool;

internal static class SchemaToolAuthorization
{
    public static PhysicalSchemaPlanAuthorization Evaluate(
        PhysicalSchemaTarget target,
        PhysicalSchemaDiffPlan plan,
        SchemaToolOptions options)
    {
        if (options.ExpectedPlanFingerprint is not null)
        {
            var actual = SchemaToolReport.Fingerprint(
                target,
                plan.ExpectedAppliedTargetFingerprint,
                plan.Operations);
            if (!string.Equals(actual, options.ExpectedPlanFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return PhysicalSchemaPlanAuthorization.Deny(
                [
                    GroundworkDiagnostic.Error(
                        "GW-CLI-011",
                        "The locked provider-state plan does not match '--expected-plan'; obtain a fresh plan before authorizing apply.",
                        "authorization")
                ]);
            }
        }

        var diagnostics = new List<GroundworkDiagnostic>();
        foreach (var identity in DestructiveOperationIdentities(plan.Operations))
        {
            if (options.ApplySafe || !options.AllowedDestructiveOperations.Contains(identity))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CLI-007",
                    $"Apply requires exact '--allow-destructive {identity}' approval for this operation.",
                    "authorization"));
            }
        }
        foreach (var identity in SemanticIdentities(plan.Operations))
        {
            if (options.ApplySafe || !options.AllowedSemanticMigrations.Contains(identity))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CLI-007",
                    $"Apply requires exact '--allow-semantic {identity}' approval for this migration.",
                    "authorization"));
            }
        }
        return diagnostics.Count == 0
            ? PhysicalSchemaPlanAuthorization.Allow
            : PhysicalSchemaPlanAuthorization.Deny(diagnostics);
    }

    public static bool RequiresDestructive(IReadOnlyList<PhysicalSchemaOperation> operations) =>
        DestructiveOperationIdentities(operations).Count != 0;

    public static IReadOnlyList<string> DestructiveOperationIdentities(
        IReadOnlyList<PhysicalSchemaOperation> operations) =>
        Evolutions(operations)
            .Where(item => item.Evolution?.IsDestructive == true)
            .Select(item => item.Operation.Identity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> SemanticIdentities(IReadOnlyList<PhysicalSchemaOperation> operations) =>
        Evolutions(operations)
            .Select(item => item.Evolution?.SemanticMigrationIdentity)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<(PhysicalSchemaOperation Operation, PhysicalEvolutionMetadata? Evolution)> Evolutions(
        IReadOnlyList<PhysicalSchemaOperation> operations) => operations.Select(operation =>
        (
            operation,
            operation switch
            {
                CreatePrimaryStorageOperation create => create.Storage.Evolution,
                CreateLinkedStorageOperation create => create.Storage.Evolution,
                CreatePhysicalEntityStorageOperation create => create.Storage.Evolution,
                CreatePhysicalIndexOperation create => create.Index.Definition.Evolution,
                _ => null
            }
        ));
}
