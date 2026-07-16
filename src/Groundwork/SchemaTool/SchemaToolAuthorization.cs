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
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        foreach (var identity in protection.DestructiveOperationIdentities)
        {
            if (options.ApplySafe || !options.AllowedDestructiveOperations.Contains(identity))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CLI-007",
                    $"Apply requires exact '--allow-destructive {identity}' approval for this operation.",
                    "authorization"));
            }
        }
        foreach (var identity in protection.SemanticMigrationIdentities)
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
}
