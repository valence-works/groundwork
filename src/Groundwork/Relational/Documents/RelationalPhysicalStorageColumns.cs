using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Relational.Documents;

/// <summary>Reserved relational envelope columns not yet represented by the portable document envelope.</summary>
public static class RelationalPhysicalStorageColumns
{
    public const string CreatedUtc = "created_utc";
    public const string UpdatedUtc = "updated_utc";
    public const string MutationOperationsTable = "groundwork_document_mutation_operations";

    public static void Validate(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var primaryColumns = new[]
        {
            route.Envelope.DocumentKind.Identifier,
            route.Envelope.StorageScope.Identifier,
            route.Envelope.Identity.OriginalId.Identifier,
            route.Envelope.Identity.ComparisonKey.Identifier,
            route.Envelope.Identity.LookupKey.Identifier,
            route.Envelope.SchemaVersion.Identifier,
            route.Envelope.Version.Identifier,
            route.Envelope.CanonicalJson.Identifier
        }.Concat(route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage)
            .Select(column => column.Column.Identifier));
        var collision = primaryColumns.FirstOrDefault(identifier =>
            identifier is CreatedUtc or UpdatedUtc);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Executable route '{route.StorageUnit.Value}' maps provider column '{collision}', which is reserved by the relational document envelope.");
        }
    }
}
