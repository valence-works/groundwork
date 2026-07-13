using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;

namespace Groundwork.SchemaTool;

internal static class SchemaToolAuthorization
{
    public static IReadOnlyList<string> FindMissing(
        IReadOnlyList<PhysicalSchemaOperation> operations,
        SchemaToolOptions options)
    {
        var missing = new List<string>();
        if (RequiresDestructive(operations) && !options.AuthorizeDestructive)
            missing.Add("destructive");
        missing.AddRange(SemanticIdentities(operations)
            .Where(identity => !options.AuthorizedSemanticMigrations.Contains(identity))
            .Select(identity => $"semantic:{identity}"));
        return missing;
    }

    public static SchemaToolReport Report(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        PhysicalSchemaDiffPlan plan,
        IReadOnlyList<string> missing)
    {
        var diagnostics = missing.Select(item => GroundworkDiagnostic.Error(
            "GW-CLI-007",
            item == "destructive"
                ? "Apply requires explicit '--authorize-destructive' approval for this target."
                : $"Apply requires explicit '--authorize-semantic {item["semantic:".Length..]}' approval for this target.",
            "authorization")).ToArray();
        return SchemaToolReport.FromPlan("apply", target, history, plan) with
        {
            Outcome = "authorization-required",
            Diagnostics = diagnostics
        };
    }

    public static bool RequiresDestructive(IReadOnlyList<PhysicalSchemaOperation> operations) =>
        Evolutions(operations).Any(evolution => evolution?.IsDestructive == true);

    public static IReadOnlyList<string> SemanticIdentities(IReadOnlyList<PhysicalSchemaOperation> operations) =>
        Evolutions(operations)
        .Select(evolution => evolution?.SemanticMigrationIdentity)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private static IEnumerable<PhysicalEvolutionMetadata?> Evolutions(
        IReadOnlyList<PhysicalSchemaOperation> operations) => operations.Select(operation => operation switch
        {
            CreatePrimaryStorageOperation create => create.Storage.Evolution,
            CreateLinkedStorageOperation create => create.Storage.Evolution,
            CreatePhysicalEntityStorageOperation create => create.Storage.Evolution,
            CreatePhysicalIndexOperation create => create.Index.Definition.Evolution,
            _ => null
        });
}
