namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Protected work in one exact physical-schema plan. Safe application is permitted only when both
/// collections are empty.
/// </summary>
public sealed record PhysicalSchemaPlanProtection(
    IReadOnlyList<string> DestructiveOperationIdentities,
    IReadOnlyList<string> SemanticMigrationIdentities)
{
    public bool IsSafe =>
        DestructiveOperationIdentities.Count == 0 &&
        SemanticMigrationIdentities.Count == 0;

    public static PhysicalSchemaPlanProtection Inspect(
        IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var evolutions = operations.Select(operation =>
        (
            Operation: operation,
            Evolution: operation switch
            {
                CreatePrimaryStorageOperation create => create.Storage.Evolution,
                CreateLinkedStorageOperation create => create.Storage.Evolution,
                CreatePhysicalEntityStorageOperation create => create.Storage.Evolution,
                CreatePhysicalIndexOperation create => create.Index.Definition.Evolution,
                _ => null
            }
        )).ToArray();
        return new PhysicalSchemaPlanProtection(
            evolutions
                .Where(item => item.Evolution?.IsDestructive == true)
                .Select(item => item.Operation.Identity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            evolutions
                .Select(item => item.Evolution?.SemanticMigrationIdentity)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }
}
