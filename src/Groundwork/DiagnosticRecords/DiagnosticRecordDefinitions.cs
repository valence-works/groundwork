using System.Collections.Frozen;

namespace Groundwork.DiagnosticRecords;

public enum DiagnosticFieldType
{
    String,
    Int64,
    Decimal,
    Boolean,
    Timestamp
}

public enum DiagnosticFieldCardinality
{
    Scalar,
    Multiple
}

public enum DiagnosticStringCasePolicy
{
    Ordinal,
    AsciiIgnoreCase
}

public enum DiagnosticMissingValueBehavior
{
    /// <summary>Records without this field do not participate in field-ordered queries.</summary>
    Excluded
}

public sealed record DiagnosticFieldDefinition(
    string Name,
    DiagnosticFieldType Type,
    DiagnosticFieldCardinality Cardinality,
    IReadOnlySet<DiagnosticPredicateOperator> SupportedPredicates,
    bool IsRequired = false,
    bool IsOrderable = false,
    bool SupportsLatestPerKey = false,
    DiagnosticStringCasePolicy CasePolicy = DiagnosticStringCasePolicy.Ordinal,
    int MaxValues = 1,
    int? MaxStringBytes = null,
    DiagnosticMissingValueBehavior MissingValueBehavior = DiagnosticMissingValueBehavior.Excluded);

public sealed record DiagnosticRecordLimits(
    int MaxBatchRecords = 1_000,
    int MaxPayloadBytes = 1_048_576,
    int MaxRecordIdBytes = 256,
    int MaxFieldsPerRecord = 64,
    int MaxQueryLimit = 1_000,
    int MaxPredicateNodes = 64,
    int MaxJsonDepth = 64);

public sealed record DiagnosticRecordStreamDefinition(
    DiagnosticStreamId Stream,
    int SchemaVersion,
    string LogicalStorageName,
    IReadOnlyList<DiagnosticFieldDefinition> Fields,
    DiagnosticRecordLimits Limits,
    TimeSpan MaxOperationClockSkew,
    TimeSpan AppendIdempotencyWindow,
    TimeSpan TrimIdempotencyWindow,
    string? LogicalHighWaterField = null);

public static class DiagnosticRecordFieldNames
{
    /// <summary>The built-in occurrence timestamp field available to every diagnostic stream query.</summary>
    public const string OccurredAt = "$occurredAt";
}

public static class DiagnosticRecordFieldResolver
{
    private static readonly DiagnosticFieldDefinition OccurredAtDefinition = new(
        DiagnosticRecordFieldNames.OccurredAt,
        DiagnosticFieldType.Timestamp,
        DiagnosticFieldCardinality.Scalar,
        new[]
        {
            DiagnosticPredicateOperator.Equal,
            DiagnosticPredicateOperator.In,
            DiagnosticPredicateOperator.RangeInclusive
        }.ToFrozenSet(),
        IsRequired: true,
        IsOrderable: true);

    public static DiagnosticFieldDefinition? Resolve(DiagnosticRecordStreamDefinition definition, string name) =>
        StringComparer.Ordinal.Equals(name, DiagnosticRecordFieldNames.OccurredAt)
            ? OccurredAtDefinition
            : definition.Fields.SingleOrDefault(x => StringComparer.Ordinal.Equals(x.Name, name));
}

public static class DiagnosticRecordStreamDefinitionSnapshot
{
    public static DiagnosticRecordStreamDefinition Capture(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Fields is null)
            return definition;
        var fields = definition.Fields.Select(field => field with
        {
            SupportedPredicates = field.SupportedPredicates?.ToFrozenSet()!
        }).ToArray();
        return definition with { Fields = Array.AsReadOnly(fields) };
    }
}

public static class DiagnosticRecordStreamDefinitionValidator
{
    public static void ValidateAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<DiagnosticValidationError>();
        if (string.IsNullOrWhiteSpace(definition.Stream.Value))
            errors.Add(new("definition.stream.required", "Stream identity is required.", "stream"));
        if (definition.SchemaVersion <= 0)
            errors.Add(new("definition.schema_version.invalid", "Schema version must be positive.", "schemaVersion"));
        if (string.IsNullOrWhiteSpace(definition.LogicalStorageName))
            errors.Add(new("definition.storage_name.required", "Logical storage name is required.", "logicalStorageName"));
        if (definition.AppendIdempotencyWindow <= TimeSpan.Zero)
            errors.Add(new("definition.append_window.invalid", "Append idempotency window must be positive.", "appendIdempotencyWindow"));
        if (definition.TrimIdempotencyWindow <= TimeSpan.Zero)
            errors.Add(new("definition.trim_window.invalid", "Trim idempotency window must be positive.", "trimIdempotencyWindow"));
        if (definition.MaxOperationClockSkew < TimeSpan.Zero)
            errors.Add(new("definition.clock_skew.invalid", "Maximum operation clock skew cannot be negative.", "maxOperationClockSkew"));
        if (definition.Limits is null)
            errors.Add(new("definition.limits.required", "Record-store limits are required.", "limits"));
        else if (definition.Limits.MaxBatchRecords <= 0 || definition.Limits.MaxPayloadBytes <= 0 ||
                 definition.Limits.MaxRecordIdBytes <= 0 || definition.Limits.MaxFieldsPerRecord <= 0 ||
                 definition.Limits.MaxQueryLimit <= 0 || definition.Limits.MaxPredicateNodes <= 0 ||
                 definition.Limits.MaxJsonDepth <= 0)
            errors.Add(new("definition.limit.invalid", "Every record-store limit must be positive.", "limits"));

        var fields = definition.Fields ?? [];
        if (definition.Fields is null)
            errors.Add(new("definition.fields.required", "A field declaration collection is required.", "fields"));
        var duplicateFields = fields.GroupBy(x => x.Name, StringComparer.Ordinal).Where(x => x.Count() > 1);
        foreach (var duplicate in duplicateFields)
            errors.Add(new("definition.field.duplicate", $"Field '{duplicate.Key}' is declared more than once.", "fields"));

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                errors.Add(new("definition.field.name.required", "Field names are required.", "fields"));
            if (StringComparer.Ordinal.Equals(field.Name, DiagnosticRecordFieldNames.OccurredAt))
                errors.Add(new("definition.field.reserved", $"Field name '{field.Name}' is reserved by the record envelope.", $"fields.{field.Name}"));
            if (field.MaxValues <= 0 || field.Cardinality == DiagnosticFieldCardinality.Scalar && field.MaxValues != 1)
                errors.Add(new("definition.field.max_values.invalid", $"Field '{field.Name}' has an invalid maximum value count.", $"fields.{field.Name}.maxValues"));
            if (field.Type == DiagnosticFieldType.String && field.MaxStringBytes is not > 0)
                errors.Add(new("definition.field.string_bound.required", $"String field '{field.Name}' requires a positive byte bound.", $"fields.{field.Name}.maxStringBytes"));
            if (field.Type != DiagnosticFieldType.String && field.MaxStringBytes is not null)
                errors.Add(new("definition.field.string_bound.not_applicable", $"Non-string field '{field.Name}' cannot declare a string byte bound.", $"fields.{field.Name}.maxStringBytes"));
            if (field.SupportedPredicates is null || field.SupportedPredicates.Count == 0)
                errors.Add(new("definition.field.predicates.required", $"Field '{field.Name}' must declare its bounded predicate surface.", $"fields.{field.Name}.supportedPredicates"));
            else if (field.Type != DiagnosticFieldType.String && field.SupportedPredicates.Contains(DiagnosticPredicateOperator.Contains))
                errors.Add(new("definition.field.contains.invalid", $"Non-string field '{field.Name}' cannot declare Contains.", $"fields.{field.Name}.supportedPredicates"));
            if (field.Type != DiagnosticFieldType.String && field.CasePolicy != DiagnosticStringCasePolicy.Ordinal)
                errors.Add(new("definition.field.case_policy.invalid", $"Non-string field '{field.Name}' must use ordinal case policy.", $"fields.{field.Name}.casePolicy"));
            if (field.Cardinality == DiagnosticFieldCardinality.Multiple && field.IsOrderable)
                errors.Add(new("definition.field.multi_order.invalid", $"Multi-value field '{field.Name}' cannot define a single field order.", $"fields.{field.Name}.isOrderable"));
            if (field.Cardinality == DiagnosticFieldCardinality.Multiple && field.SupportsLatestPerKey)
                errors.Add(new("definition.field.multi_latest.invalid", $"Multi-value field '{field.Name}' cannot be a latest-per-key identity.", $"fields.{field.Name}.supportsLatestPerKey"));
        }

        if (definition.LogicalHighWaterField is { } highWater)
        {
            var field = fields.SingleOrDefault(x => StringComparer.Ordinal.Equals(x.Name, highWater));
            if (field is null || field.Cardinality != DiagnosticFieldCardinality.Scalar || field.Type != DiagnosticFieldType.Int64)
                errors.Add(new("definition.high_water.invalid", "The logical high-water field must be a declared scalar Int64 field.", "logicalHighWaterField"));
        }

        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }
}
