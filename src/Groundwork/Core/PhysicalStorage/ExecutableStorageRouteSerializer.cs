using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>Canonical serialization and fingerprinting for executable storage routes.</summary>
public static class ExecutableStorageRouteSerializer
{
    public static string Serialize(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return Encoding.UTF8.GetString(SerializeCore(route, includeFingerprint: true));
    }

    internal static string CreateFingerprint(ExecutableStorageRoute route) =>
        Convert.ToHexString(SHA256.HashData(SerializeCore(route, includeFingerprint: false))).ToLowerInvariant();

    internal static void ValidateCanonicalSnapshot(
        string canonicalJson,
        string expectedDefinitionFingerprint,
        string expectedRouteFingerprint)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("definitionFingerprint").GetString() != expectedDefinitionFingerprint ||
            root.GetProperty("fingerprint").GetString() != expectedRouteFingerprint)
        {
            throw new InvalidOperationException("Applied route snapshot fingerprints do not match its canonical route.");
        }

        using var canonicalStream = new MemoryStream();
        using var fingerprintStream = new MemoryStream();
        using (var canonicalWriter = new Utf8JsonWriter(canonicalStream))
        using (var fingerprintWriter = new Utf8JsonWriter(fingerprintStream))
        {
            canonicalWriter.WriteStartObject();
            fingerprintWriter.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                canonicalWriter.WritePropertyName(property.Name);
                property.Value.WriteTo(canonicalWriter);
                if (property.NameEquals("fingerprint"))
                    continue;
                fingerprintWriter.WritePropertyName(property.Name);
                property.Value.WriteTo(fingerprintWriter);
            }
            canonicalWriter.WriteEndObject();
            fingerprintWriter.WriteEndObject();
        }

        var normalized = Encoding.UTF8.GetString(canonicalStream.ToArray());
        var actualFingerprint = Convert.ToHexString(
                SHA256.HashData(fingerprintStream.ToArray()))
            .ToLowerInvariant();
        if (!string.Equals(normalized, canonicalJson, StringComparison.Ordinal) ||
            !string.Equals(actualFingerprint, expectedRouteFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Applied route snapshot is non-canonical or has an invalid fingerprint.");
        }
    }

    private static byte[] SerializeCore(ExecutableStorageRoute route, bool includeFingerprint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("storageUnit", route.StorageUnit.Value);
            writer.WriteString("provisioningMode", route.ProvisioningMode.ToString());
            writer.WriteString("form", route.Form.ToString());
            if (route.SharedStorage is null)
                writer.WriteNull("sharedStorage");
            else
                writer.WriteString("sharedStorage", route.SharedStorage.Value);
            writer.WriteString("scopePolicy", route.ScopePolicy.ToString());
            WriteStorageObject(writer, "primaryStorage", route.PrimaryStorage);
            if (route.LinkedIndexStorage is null)
                writer.WriteNull("linkedIndexStorage");
            else
                WriteStorageObject(writer, "linkedIndexStorage", route.LinkedIndexStorage);

            writer.WritePropertyName("envelope");
            writer.WriteStartObject();
            WriteColumn(writer, "id", route.Envelope.Id);
            WriteColumn(writer, "documentKind", route.Envelope.DocumentKind);
            WriteColumn(writer, "storageScope", route.Envelope.StorageScope);
            WriteColumn(writer, "version", route.Envelope.Version);
            WriteColumn(writer, "schemaVersion", route.Envelope.SchemaVersion);
            WriteColumn(writer, "canonicalJson", route.Envelope.CanonicalJson);
            writer.WriteEndObject();

            if (route.LinkedRelationship is null)
            {
                writer.WriteNull("linkedRelationship");
            }
            else
            {
                writer.WritePropertyName("linkedRelationship");
                writer.WriteStartObject();
                WriteColumn(writer, "documentId", route.LinkedRelationship.DocumentId);
                WriteColumn(writer, "documentKind", route.LinkedRelationship.DocumentKind);
                WriteColumn(writer, "storageScope", route.LinkedRelationship.StorageScope);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("discriminator");
            writer.WriteStartObject();
            WriteColumn(writer, "column", route.Discriminator.Column);
            writer.WriteString("value", route.Discriminator.Value);
            writer.WriteBoolean("primaryKey", route.Discriminator.ParticipatesInPrimaryKey);
            writer.WriteEndObject();

            writer.WritePropertyName("scopeKey");
            writer.WriteStartObject();
            WriteColumn(writer, "column", route.ScopeKey.Column);
            writer.WriteString("policy", route.ScopeKey.Policy.ToString());
            writer.WriteBoolean("globalSentinel", route.ScopeKey.UsesGlobalSentinel);
            writer.WriteBoolean("primaryKey", route.ScopeKey.ParticipatesInPrimaryKey);
            writer.WriteBoolean("auxiliaryKey", route.ScopeKey.ParticipatesInAuxiliaryKey);
            writer.WriteEndObject();

            WriteKey(writer, "primaryKey", route.PrimaryKey);
            if (route.AuxiliaryKey is null)
                writer.WriteNull("auxiliaryKey");
            else
                WriteKey(writer, "auxiliaryKey", route.AuxiliaryKey);

            writer.WritePropertyName("projectedColumns");
            writer.WriteStartArray();
            foreach (var projection in route.ProjectedColumns)
            {
                writer.WriteStartObject();
                writer.WriteString("logicalName", projection.Definition.LogicalName);
                writer.WriteString("path", projection.Definition.Path);
                writer.WriteString("type", projection.Definition.Type.ToString());
                WriteNullableNumber(writer, "length", projection.Definition.Length);
                WriteNullableNumber(writer, "precision", projection.Definition.Precision);
                WriteNullableNumber(writer, "scale", projection.Definition.Scale);
                writer.WriteBoolean("nullable", projection.Definition.IsNullable);
                WriteNullableString(writer, "collation", projection.Definition.Collation);
                WriteNullableString(writer, "default", projection.Definition.DefaultValue);
                writer.WriteString("rebuild", projection.Definition.RebuildMode.ToString());
                writer.WriteString("target", projection.Target.ToString());
                WriteColumn(writer, "column", projection.Column);
                WriteName(writer, "name", projection.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("indexes");
            writer.WriteStartArray();
            foreach (var index in route.Indexes)
            {
                writer.WriteStartObject();
                writer.WriteString("identity", index.Identity);
                WriteName(writer, "name", index.Name);
                writer.WriteString("target", index.Target.ToString());
                writer.WriteBoolean("unique", index.IsUnique);
                writer.WriteString("missingValueBehavior", index.MissingValueBehavior.ToString());
                writer.WriteString("declaredTarget", index.Definition.Target.ToString());
                writer.WriteNumber("schemaVersion", index.Definition.SchemaVersion);
                WriteEvolution(writer, index.Definition.Evolution);
                WriteIndexColumns(writer, index.Columns);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("maintenance");
            writer.WriteStartArray();
            foreach (var maintenance in route.MaintenanceRoutes)
            {
                writer.WriteStartObject();
                writer.WriteString("operation", maintenance.Operation.ToString());
                writer.WritePropertyName("targets");
                writer.WriteStartArray();
                foreach (var target in maintenance.Targets)
                    writer.WriteStringValue(target.ToString());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("queryPaths");
            writer.WriteStartArray();
            foreach (var path in route.CandidateQueryPaths)
            {
                writer.WriteStartObject();
                writer.WriteString("identity", path.Identity);
                writer.WriteString("kind", path.Kind.ToString());
                writer.WriteString("target", path.Target.ToString());
                if (path.IndexName is null)
                    writer.WriteNull("indexName");
                else
                    WriteName(writer, "indexName", path.IndexName);
                WriteIndexColumns(writer, path.Columns);
                writer.WritePropertyName("queries");
                writer.WriteStartArray();
                foreach (var query in path.QueryIdentities)
                    writer.WriteStringValue(query);
                writer.WriteEndArray();
                writer.WriteBoolean("scaleBearing", path.IsScaleBearing);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in route.CapabilityRequirements)
                writer.WriteStringValue(capability.ToString());
            writer.WriteEndArray();
            writer.WriteString("definitionFingerprint", route.DefinitionFingerprint);
            if (includeFingerprint)
                writer.WriteString("fingerprint", route.Fingerprint);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteStorageObject(Utf8JsonWriter writer, string property, ExecutableStorageObjectRoute storage)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("role", storage.Role.ToString());
        WriteName(writer, "name", storage.Name);
        writer.WriteNumber("schemaVersion", storage.SchemaVersion);
        WriteEvolution(writer, storage.Evolution);
        writer.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter writer, string property, ExecutableKeyRoute key)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("target", key.Target.ToString());
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var column in key.Columns)
            WriteColumnValue(writer, column);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteIndexColumns(Utf8JsonWriter writer, IReadOnlyList<ExecutableIndexColumnRoute> columns)
    {
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var column in columns)
        {
            writer.WriteStartObject();
            WriteColumn(writer, "column", column.Column);
            writer.WriteNumber("order", column.Order);
            writer.WriteString("direction", column.Direction.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteColumn(Utf8JsonWriter writer, string property, ExecutableColumnRoute column)
    {
        writer.WritePropertyName(property);
        WriteColumnValue(writer, column);
    }

    private static void WriteColumnValue(Utf8JsonWriter writer, ExecutableColumnRoute column)
    {
        writer.WriteStartObject();
        writer.WriteString("logical", column.LogicalName);
        writer.WriteString("identifier", column.Identifier);
        writer.WriteEndObject();
    }

    private static void WriteName(Utf8JsonWriter writer, string property, ProviderPhysicalObjectName name)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("kind", name.ObjectKind.ToString());
        writer.WriteString("featureDefault", name.FeatureDefaultLogicalName);
        writer.WriteString("logical", name.LogicalName);
        writer.WriteString("identifier", name.Identifier);
        writer.WriteString("collisionScope", name.CollisionScope);
        writer.WriteString("namingOwner", name.NamingOwner.Value);
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
