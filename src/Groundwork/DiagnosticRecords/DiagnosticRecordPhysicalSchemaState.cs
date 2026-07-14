using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Groundwork.DiagnosticRecords;

/// <summary>
/// Canonical, provider-neutral state persisted beside each diagnostic stream definition. It binds
/// physical comparison keys to both the logical definition and the exact algorithm versions that
/// created them.
/// </summary>
public sealed record DiagnosticRecordPhysicalSchemaState(
    string DefinitionFingerprint,
    string ComparisonAlgorithmManifest,
    string ComparisonAlgorithmManifestFingerprint,
    string CanonicalDefinition)
{
    public static DiagnosticRecordPhysicalSchemaState Capture(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
        definition = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);
        var manifest = CreateAlgorithmManifest(definition);
        var canonical = CreateCanonicalDefinition(definition, manifest);
        return new(
            Hash(canonical),
            manifest,
            Hash(manifest),
            canonical);
    }

    public static string AlgorithmId(DiagnosticStringCasePolicy policy) => policy switch
    {
        DiagnosticStringCasePolicy.Ordinal => DiagnosticStringComparisonKey.OrdinalAlgorithmId,
        DiagnosticStringCasePolicy.AsciiIgnoreCase => DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId,
        DiagnosticStringCasePolicy.UnicodeOrdinalIgnoreCase => DiagnosticStringComparisonKey.UnicodeOrdinalIgnoreCaseAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    private static string CreateAlgorithmManifest(DiagnosticRecordStreamDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("lookupHashAlgorithm", DiagnosticStringComparisonKey.LookupHashAlgorithmId);
            writer.WriteString("searchKeyAlgorithm", DiagnosticStringComparisonKey.SearchKeyAlgorithmId);
            writer.WriteNumber("boundedPrefixLength", DiagnosticStringComparisonKey.BoundedPrefixLength);
            writer.WritePropertyName("comparisonAlgorithms");
            writer.WriteStartArray();
            foreach (var policy in definition.Fields
                         .Where(field => field.Type == DiagnosticFieldType.String)
                         .Select(field => field.CasePolicy)
                         .Distinct()
                         .Order())
            {
                writer.WriteStartObject();
                writer.WriteNumber("casePolicy", (int)policy);
                writer.WriteString("algorithm", AlgorithmId(policy));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateCanonicalDefinition(
        DiagnosticRecordStreamDefinition definition,
        string algorithmManifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("stream", definition.Stream.Value);
            writer.WriteNumber("schemaVersion", definition.SchemaVersion);
            writer.WriteString("logicalStorageName", definition.LogicalStorageName);
            writer.WriteNumber("maxClockSkewTicks", definition.MaxOperationClockSkew.Ticks);
            writer.WriteNumber("appendWindowTicks", definition.AppendIdempotencyWindow.Ticks);
            writer.WriteNumber("trimWindowTicks", definition.TrimIdempotencyWindow.Ticks);
            if (definition.LogicalHighWaterField is null)
                writer.WriteNull("logicalHighWaterField");
            else
                writer.WriteString("logicalHighWaterField", definition.LogicalHighWaterField);
            writer.WritePropertyName("limits");
            writer.WriteStartObject();
            writer.WriteNumber("maxBatchRecords", definition.Limits.MaxBatchRecords);
            writer.WriteNumber("maxPayloadBytes", definition.Limits.MaxPayloadBytes);
            writer.WriteNumber("maxRecordIdBytes", definition.Limits.MaxRecordIdBytes);
            writer.WriteNumber("maxFieldsPerRecord", definition.Limits.MaxFieldsPerRecord);
            writer.WriteNumber("maxQueryLimit", definition.Limits.MaxQueryLimit);
            writer.WriteNumber("maxPredicateNodes", definition.Limits.MaxPredicateNodes);
            writer.WriteNumber("maxPredicateValues", definition.Limits.MaxPredicateValues);
            writer.WriteNumber("maxJsonDepth", definition.Limits.MaxJsonDepth);
            writer.WriteEndObject();
            writer.WritePropertyName("fields");
            writer.WriteStartArray();
            foreach (var field in definition.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteNumber("type", (int)field.Type);
                writer.WriteNumber("cardinality", (int)field.Cardinality);
                writer.WriteBoolean("required", field.IsRequired);
                writer.WriteBoolean("orderable", field.IsOrderable);
                writer.WriteBoolean("latest", field.SupportsLatestPerKey);
                writer.WriteNumber("casePolicy", (int)field.CasePolicy);
                writer.WriteNumber("maxValues", field.MaxValues);
                if (field.MaxStringBytes is null)
                    writer.WriteNull("maxStringBytes");
                else
                    writer.WriteNumber("maxStringBytes", field.MaxStringBytes.Value);
                writer.WriteNumber("missing", (int)field.MissingValueBehavior);
                writer.WritePropertyName("predicates");
                writer.WriteStartArray();
                foreach (var predicate in field.SupportedPredicates.Order())
                    writer.WriteNumberValue((int)predicate);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("comparisonAlgorithms", algorithmManifest);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
