using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Produces the canonical, deterministic provider-definition representation used for snapshots,
/// schema history, and fingerprints.
/// </summary>
public static class PhysicalStorageDefinitionSerializer
{
    public static string Serialize(ProviderPhysicalTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Encoding.UTF8.GetString(SerializeCore(definition.Resolved, definition.Names));
    }

    internal static string CreateFingerprint(
        ResolvedPhysicalTableDefinition resolved,
        IReadOnlyList<ProviderPhysicalObjectName> providerNames)
        => Convert.ToHexString(SHA256.HashData(SerializeCore(resolved, providerNames))).ToLowerInvariant();

    private static byte[] SerializeCore(
        ResolvedPhysicalTableDefinition resolved,
        IReadOnlyList<ProviderPhysicalObjectName> providerNames)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("storageUnit", resolved.StorageUnit.Value);
            writer.WriteString("provisioningMode", resolved.ProvisioningMode.ToString());
            writer.WriteString("scopePolicy", resolved.ScopePolicy.ToString());
            WriteDefinition(writer, resolved.Definition);
            if (resolved.SharedStorageDefinition is not null)
                WriteSharedStorageDefinition(writer, resolved.SharedStorageDefinition);
            writer.WritePropertyName("scaleBearingDemand");
            writer.WriteStartArray();
            foreach (var demand in resolved.ScaleBearingDemand
                         .OrderBy(x => x.QueryIdentity, StringComparer.Ordinal)
                         .ThenBy(x => x.IndexIdentity, StringComparer.Ordinal)
                         .ThenBy(x => x.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("query", demand.QueryIdentity);
                writer.WriteString("index", demand.IndexIdentity);
                writer.WriteString("path", demand.Path);
                writer.WriteString("sortDirection", demand.SortDirection.ToString());
                writer.WriteString("valueKind", demand.ValueKind.ToString());
                writer.WriteString("missingValueBehavior", demand.MissingValueBehavior.ToString());
                writer.WritePropertyName("operations");
                writer.WriteStartArray();
                foreach (var operation in demand.Operations.Order())
                    writer.WriteStringValue(operation.ToString());
                writer.WriteEndArray();
                writer.WriteString("sortSupport", demand.SortSupport.ToString());
                writer.WriteString("pagingSupport", demand.PagingSupport.ToString());
                writer.WriteBoolean("supportsDisjunction", demand.SupportsDisjunction);
                writer.WriteBoolean("supportsTotalCount", demand.SupportsTotalCount);
                writer.WritePropertyName("predicateFields");
                writer.WriteStartArray();
                foreach (var predicate in demand.PredicateFields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", predicate.Path);
                    writer.WritePropertyName("operations");
                    writer.WriteStartArray();
                    foreach (var operation in predicate.Operations.Order())
                        writer.WriteStringValue(operation.ToString());
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WritePropertyName("resultOperations");
                writer.WriteStartArray();
                foreach (var operation in demand.ResultOperations.Order())
                    writer.WriteStringValue(operation.ToString());
                writer.WriteEndArray();
                WriteNullableString(writer, "latestPerKeyPath", demand.LatestPerKeyPath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("names");
            writer.WriteStartArray();
            foreach (var name in providerNames
                         .OrderBy(x => x.ObjectKind)
                         .ThenBy(x => x.FeatureDefaultLogicalName, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", name.ObjectKind.ToString());
                writer.WriteString("featureDefault", name.FeatureDefaultLogicalName);
                writer.WriteString("logical", name.LogicalName);
                writer.WriteString("identifier", name.Identifier);
                writer.WriteString("collisionScope", name.CollisionScope);
                writer.WriteString("namingOwner", name.NamingOwner.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteDefinition(Utf8JsonWriter writer, PhysicalTableDefinition definition)
    {
        writer.WritePropertyName("definition");
        writer.WriteStartObject();
        writer.WriteString("form", definition.Form.ToString());
        WriteNullableString(writer, "featureDefaultLogicalName", definition.FeatureDefaultLogicalName);
        WriteNullableString(writer, "sharedStorage", definition.SharedStorage?.Value);
        if (definition.LinkedProjectionLogicalName is not null)
            writer.WriteString("linkedProjectionLogicalName", definition.LinkedProjectionLogicalName);
        if (definition.LinkedKey is not null)
        {
            writer.WritePropertyName("linkedKey");
            writer.WriteStartObject();
            writer.WriteString("documentId", definition.LinkedKey.DocumentIdColumn);
            writer.WriteString("documentKind", definition.LinkedKey.DocumentKindColumn);
            writer.WriteString("storageScope", definition.LinkedKey.StorageScopeColumn);
            writer.WriteEndObject();
        }
        writer.WriteNumber("schemaVersion", definition.SchemaVersion);
        WriteEvolution(writer, definition.Evolution);
        if (definition.Envelope is not null)
        {
            writer.WritePropertyName("envelope");
            writer.WriteStartObject();
            writer.WriteString("id", definition.Envelope.IdColumn);
            writer.WriteString("documentKind", definition.Envelope.DocumentKindColumn);
            writer.WriteString("storageScope", definition.Envelope.StorageScopeColumn);
            writer.WriteString("version", definition.Envelope.VersionColumn);
            writer.WriteString("schemaVersion", definition.Envelope.SchemaVersionColumn);
            writer.WriteString("canonicalJson", definition.Envelope.CanonicalJsonColumn);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("projectedColumns");
        writer.WriteStartArray();
        foreach (var column in definition.ProjectedColumns.OrderBy(x => x.LogicalName, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("logicalName", column.LogicalName);
            writer.WriteString("path", column.Path);
            writer.WriteString("type", column.Type.ToString());
            WriteNullableNumber(writer, "length", column.Length);
            WriteNullableNumber(writer, "precision", column.Precision);
            WriteNullableNumber(writer, "scale", column.Scale);
            writer.WriteBoolean("nullable", column.IsNullable);
            WriteNullableString(writer, "collation", column.Collation);
            WriteNullableString(writer, "default", column.DefaultValue);
            writer.WriteString("rebuild", column.RebuildMode.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("indexes");
        writer.WriteStartArray();
        foreach (var index in definition.Indexes.OrderBy(x => x.LogicalName, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("logicalName", index.LogicalName);
            writer.WriteBoolean("unique", index.IsUnique);
            writer.WriteString("target", index.Target.ToString());
            writer.WriteNumber("schemaVersion", index.SchemaVersion);
            WriteEvolution(writer, index.Evolution);
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (var column in index.Columns.OrderBy(x => x.Order))
            {
                writer.WriteStartObject();
                writer.WriteString("logicalName", column.ColumnLogicalName);
                writer.WriteNumber("order", column.Order);
                writer.WriteString("direction", column.Direction.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSharedStorageDefinition(
        Utf8JsonWriter writer,
        SharedDocumentStorageDefinition definition)
    {
        writer.WritePropertyName("sharedStorageDefinition");
        writer.WriteStartObject();
        writer.WriteString("binding", definition.Binding.Value);
        writer.WriteString("featureDefaultLogicalName", definition.FeatureDefaultLogicalName);
        writer.WriteNumber("schemaVersion", definition.SchemaVersion);
        writer.WritePropertyName("envelope");
        writer.WriteStartObject();
        writer.WriteString("id", definition.Envelope.IdColumn);
        writer.WriteString("documentKind", definition.Envelope.DocumentKindColumn);
        writer.WriteString("storageScope", definition.Envelope.StorageScopeColumn);
        writer.WriteString("version", definition.Envelope.VersionColumn);
        writer.WriteString("schemaVersion", definition.Envelope.SchemaVersionColumn);
        writer.WriteString("canonicalJson", definition.Envelope.CanonicalJsonColumn);
        writer.WriteEndObject();
        WriteEvolution(writer, definition.Evolution);
        writer.WriteEndObject();
    }

    private static void WriteEvolution(Utf8JsonWriter writer, PhysicalEvolutionMetadata? evolution)
    {
        if (evolution is null)
            return;

        writer.WritePropertyName("evolution");
        writer.WriteStartObject();
        writer.WriteBoolean("requiresBackfill", evolution.RequiresBackfill);
        writer.WriteBoolean("destructive", evolution.IsDestructive);
        WriteNullableString(writer, "semanticMigrationIdentity", evolution.SemanticMigrationIdentity);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }
}
