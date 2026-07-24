using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;

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

    public static ExecutableStorageRoute Deserialize(string canonicalJson)
    {
        var route = DeserializeRaw(canonicalJson);
        route.EnsureSupportedIdentityAlgorithms();
        return route;
    }

    internal static ExecutableStorageRoute DeserializeRaw(string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        var sharedStorage = root.GetProperty("sharedStorage");
        var linkedStorage = root.GetProperty("linkedIndexStorage");
        var linkedRelationship = root.GetProperty("linkedRelationship");
        var auxiliaryKey = root.GetProperty("auxiliaryKey");
        var envelope = root.GetProperty("envelope");
        var discriminator = root.GetProperty("discriminator");
        var scopeKey = root.GetProperty("scopeKey");
        var projectedColumns = root.GetProperty("projectedColumns").EnumerateArray().Select(ReadProjectedColumn).ToArray();
        var collectionElementStorages = (root.TryGetProperty("collectionElementStorages", out var collectionElementStorageJson)
                ? collectionElementStorageJson.EnumerateArray()
                : Enumerable.Empty<JsonElement>())
            .Select(element => new ExecutableCollectionElementStorageRoute(
                ReadStorageObject(element.GetProperty("storage")),
                projectedColumns.Single(projection => projection.Definition.LogicalName ==
                    element.GetProperty("projection").GetString()),
                ReadColumn(element.GetProperty("documentKind")),
                ReadColumn(element.GetProperty("storageScope")),
                ReadColumn(element.GetProperty("idComparisonKey")),
                ReadColumn(element.GetProperty("idLookupKey")),
                ReadColumn(element.GetProperty("ordinal")),
                new ExecutableProjectedColumnRoute(
                    projectedColumns.Single(projection => projection.Definition.LogicalName == element.GetProperty("projection").GetString()).Definition,
                    ReadColumn(element.GetProperty("value")),
                    ExecutableStorageObjectRole.CollectionElementStorage,
                    ReadStorageObject(element.GetProperty("storage")).Name)))
            .ToArray();
        var route = new ExecutableStorageRoute(
            new StorageUnitIdentity(root.GetProperty("storageUnit").GetString()!),
            ReadEnum<StorageUnitProvisioningMode>(root, "provisioningMode"),
            ReadEnum<PhysicalStorageForm>(root, "form"),
            sharedStorage.ValueKind == JsonValueKind.Null
                ? null
                : new SharedStorageBinding(sharedStorage.GetString()!),
            ReadEnum<StorageScopePolicy>(root, "scopePolicy"),
            ReadStorageObject(root.GetProperty("primaryStorage")),
            linkedStorage.ValueKind == JsonValueKind.Null ? null : ReadStorageObject(linkedStorage),
            new ExecutableDocumentEnvelopeRoute(
                ReadIdentity(envelope.GetProperty("identity")),
                ReadColumn(envelope.GetProperty("documentKind")),
                ReadColumn(envelope.GetProperty("storageScope")),
                ReadColumn(envelope.GetProperty("version")),
                ReadColumn(envelope.GetProperty("schemaVersion")),
                ReadColumn(envelope.GetProperty("canonicalJson"))),
            linkedRelationship.ValueKind == JsonValueKind.Null
                ? null
                : new ExecutableLinkedRelationshipRoute(
                    ReadIdentity(linkedRelationship.GetProperty("identity")),
                    ReadColumn(linkedRelationship.GetProperty("documentKind")),
                    ReadColumn(linkedRelationship.GetProperty("storageScope"))),
            new ExecutableDiscriminatorRoute(
                ReadColumn(discriminator.GetProperty("column")),
                discriminator.GetProperty("value").GetString()!,
                discriminator.GetProperty("primaryKey").GetBoolean()),
            new ExecutableScopeKeyRoute(
                ReadColumn(scopeKey.GetProperty("column")),
                ReadEnum<StorageScopePolicy>(scopeKey, "policy"),
                scopeKey.GetProperty("primaryKey").GetBoolean(),
                scopeKey.GetProperty("auxiliaryKey").GetBoolean()),
            ReadKey(root.GetProperty("primaryKey")),
            auxiliaryKey.ValueKind == JsonValueKind.Null ? null : ReadKey(auxiliaryKey),
            projectedColumns,
            collectionElementStorages,
            root.GetProperty("indexes").EnumerateArray().Select(ReadIndex).ToArray(),
            root.GetProperty("maintenance").EnumerateArray().Select(ReadMaintenance).ToArray(),
            root.GetProperty("queryPaths").EnumerateArray().Select(ReadQueryPath).ToArray(),
            root.GetProperty("capabilities").EnumerateArray()
                .Select(capability => Enum.Parse<ExecutableStorageCapability>(capability.GetString()!))
                .ToArray(),
            root.GetProperty("definitionFingerprint").GetString()!,
            root.GetProperty("fingerprint").GetString()!);
        if (!string.Equals(Serialize(route), canonicalJson, StringComparison.Ordinal))
            throw new InvalidOperationException("Executable storage route snapshot is not in canonical form.");
        return route;
    }

    internal static void ValidateCanonicalSnapshot(
        string canonicalJson,
        string expectedDefinitionFingerprint,
        string expectedRouteFingerprint)
    {
        var route = DeserializeRaw(canonicalJson);
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            route.DefinitionFingerprint != expectedDefinitionFingerprint ||
            route.Fingerprint != expectedRouteFingerprint)
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
            WriteIdentity(writer, "identity", route.Envelope.Identity);
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
                WriteIdentity(writer, "identity", route.LinkedRelationship.Identity);
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
                writer.WriteString("cardinality", projection.Definition.Cardinality.ToString());
                WriteNullableNumber(writer, "maxCollectionElements", projection.Definition.MaxCollectionElements);
                writer.WriteString("target", projection.Target.ToString());
                WriteColumn(writer, "column", projection.Column);
                WriteName(writer, "name", projection.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (route.CollectionElementStorages.Count != 0)
            {
                writer.WritePropertyName("collectionElementStorages");
                writer.WriteStartArray();
                foreach (var collection in route.CollectionElementStorages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("projection", collection.Projection.Definition.LogicalName);
                    WriteStorageObject(writer, "storage", collection.Storage);
                    WriteColumn(writer, "documentKind", collection.DocumentKind);
                    WriteColumn(writer, "storageScope", collection.StorageScope);
                    WriteColumn(writer, "idComparisonKey", collection.IdComparisonKey);
                    WriteColumn(writer, "idLookupKey", collection.IdLookupKey);
                    WriteColumn(writer, "ordinal", collection.Ordinal);
                    WriteColumn(writer, "value", collection.Value.Column);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

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

    private static ExecutableStorageObjectRoute ReadStorageObject(JsonElement element) =>
        new(
            ReadEnum<ExecutableStorageObjectRole>(element, "role"),
            ReadName(element.GetProperty("name")),
            element.GetProperty("schemaVersion").GetInt32(),
            ReadEvolution(element));

    private static ExecutableDocumentIdentityRoute ReadIdentity(JsonElement element) =>
        new(
            ReadEnum<StringIdentityCasePolicy>(element, "stringCasePolicy"),
            element.GetProperty("comparisonAlgorithm").GetString()!,
            element.GetProperty("lookupAlgorithm").GetString()!,
            ReadColumn(element.GetProperty("original")),
            ReadColumn(element.GetProperty("comparisonKey")),
            ReadColumn(element.GetProperty("lookupKey")));

    private static ExecutableKeyRoute ReadKey(JsonElement element) =>
        new(
            ReadEnum<ExecutableStorageObjectRole>(element, "target"),
            element.GetProperty("columns").EnumerateArray().Select(ReadColumn).ToArray());

    private static ExecutableProjectedColumnRoute ReadProjectedColumn(JsonElement element)
    {
        var definition = new ProjectedColumnDefinition(
            element.GetProperty("logicalName").GetString()!,
            element.GetProperty("path").GetString()!,
            ReadEnum<PortablePhysicalType>(element, "type"),
            ReadNullableInt32(element, "length"),
            ReadNullableInt32(element, "precision"),
            ReadNullableInt32(element, "scale"),
            element.GetProperty("nullable").GetBoolean(),
            ReadNullableString(element, "collation"),
            ReadNullableString(element, "default"),
            ReadEnum<ProjectionRebuildMode>(element, "rebuild"),
            element.TryGetProperty("cardinality", out var cardinality)
                ? Enum.Parse<ProjectionCardinality>(cardinality.GetString()!)
                : ProjectionCardinality.Scalar,
            ReadNullableInt32(element, "maxCollectionElements"));
        return new ExecutableProjectedColumnRoute(
            definition,
            ReadColumn(element.GetProperty("column")),
            ReadEnum<ExecutableStorageObjectRole>(element, "target"),
            ReadName(element.GetProperty("name")));
    }

    private static ExecutablePhysicalIndexRoute ReadIndex(JsonElement element)
    {
        var columns = ReadIndexColumns(element).ToArray();
        var definition = new PhysicalIndexDefinition(
            element.GetProperty("identity").GetString()!,
            columns.Select(column => new PhysicalIndexColumnDefinition(
                    column.Column.LogicalName,
                    column.Order,
                    column.Direction))
                .ToArray(),
            element.GetProperty("unique").GetBoolean(),
            element.GetProperty("schemaVersion").GetInt32(),
            ReadEvolution(element),
            ReadEnum<PhysicalIndexStorageTarget>(element, "declaredTarget"),
            ReadEnum<MissingValueBehavior>(element, "missingValueBehavior"));
        return new ExecutablePhysicalIndexRoute(
            definition,
            ReadName(element.GetProperty("name")),
            ReadEnum<ExecutableStorageObjectRole>(element, "target"),
            columns);
    }

    private static ExecutableMaintenanceRoute ReadMaintenance(JsonElement element) =>
        new(
            ReadEnum<ExecutableMaintenanceOperation>(element, "operation"),
            element.GetProperty("targets").EnumerateArray()
                .Select(target => Enum.Parse<ExecutableStorageObjectRole>(target.GetString()!))
                .ToArray());

    private static ExecutableQueryPathRoute ReadQueryPath(JsonElement element)
    {
        var indexName = element.GetProperty("indexName");
        return new ExecutableQueryPathRoute(
            element.GetProperty("identity").GetString()!,
            ReadEnum<ExecutableQueryPathKind>(element, "kind"),
            ReadEnum<ExecutableStorageObjectRole>(element, "target"),
            indexName.ValueKind == JsonValueKind.Null ? null : ReadName(indexName),
            ReadIndexColumns(element).ToArray(),
            element.GetProperty("queries").EnumerateArray().Select(query => query.GetString()!).ToArray(),
            element.GetProperty("scaleBearing").GetBoolean());
    }

    private static IEnumerable<ExecutableIndexColumnRoute> ReadIndexColumns(JsonElement element) =>
        element.GetProperty("columns").EnumerateArray().Select(column =>
            new ExecutableIndexColumnRoute(
                ReadColumn(column.GetProperty("column")),
                column.GetProperty("order").GetInt32(),
                ReadEnum<PhysicalSortDirection>(column, "direction")));

    private static ExecutableColumnRoute ReadColumn(JsonElement element) =>
        new(
            element.GetProperty("logical").GetString()!,
            element.GetProperty("identifier").GetString()!);

    private static ProviderPhysicalObjectName ReadName(JsonElement element) =>
        new(
            ReadEnum<PhysicalObjectKind>(element, "kind"),
            element.GetProperty("featureDefault").GetString()!,
            element.GetProperty("logical").GetString()!,
            element.GetProperty("identifier").GetString()!,
            element.GetProperty("collisionScope").GetString()!,
            new StorageUnitIdentity(element.GetProperty("namingOwner").GetString()!));

    private static PhysicalEvolutionMetadata? ReadEvolution(JsonElement element)
    {
        if (!element.TryGetProperty("evolution", out var evolution))
            return null;
        return new PhysicalEvolutionMetadata(
            evolution.GetProperty("requiresBackfill").GetBoolean(),
            evolution.GetProperty("destructive").GetBoolean(),
            ReadNullableString(evolution, "semanticMigrationIdentity"));
    }

    private static T ReadEnum<T>(JsonElement element, string property) where T : struct, Enum =>
        Enum.Parse<T>(element.GetProperty(property).GetString()!);

    private static int? ReadNullableInt32(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    }

    private static string? ReadNullableString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
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

    private static void WriteIdentity(
        Utf8JsonWriter writer,
        string property,
        ExecutableDocumentIdentityRoute identity)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("stringCasePolicy", identity.StringCasePolicy.ToString());
        writer.WriteString("comparisonAlgorithm", identity.ComparisonAlgorithmId);
        writer.WriteString("lookupAlgorithm", identity.LookupAlgorithmId);
        WriteColumn(writer, "original", identity.OriginalId);
        WriteColumn(writer, "comparisonKey", identity.ComparisonKey);
        WriteColumn(writer, "lookupKey", identity.LookupKey);
        writer.WriteEndObject();
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
