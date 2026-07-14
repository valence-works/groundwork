using Groundwork.DiagnosticRecords;

namespace Groundwork.MongoDb.DiagnosticRecords;

internal static class MongoDbDiagnosticRecordValidator
{
    internal const int MaxStringBytes = DiagnosticStringProjectionLimits.MaxInputUtf8Bytes;
    internal const int MaxBsonDocumentBytes = 16_000_000;
    private const int FixedDocumentOverhead = 4_096;
    private const int StoredFieldOverhead = 256;
    private const int QueryValueOverhead = 512;
    private const int StoredStringExpansion = 16;
    private const int ScalarSortExpansion = 7;
    private const int QueryComparisonKeyExpansion = 6;
    private const int QuerySearchKeyExpansion = 7;

    public static void ValidateDefinitionAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var stringErrors = (definition.Fields ?? [])
            .Where(field => field.Type == DiagnosticFieldType.String && field.MaxStringBytes > MaxStringBytes)
            .Select(field => new DiagnosticValidationError(
                "provider.mongodb.string_bound.too_large",
                $"MongoDB diagnostic string fields are limited to {MaxStringBytes} UTF-8 bytes by the bounded comparison-projection contract.",
                $"fields.{field.Name}.maxStringBytes"))
            .ToArray();
        if (stringErrors.Length > 0)
            throw new DiagnosticRecordValidationException(stringErrors);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
        var errors = new List<DiagnosticValidationError>();
        var fields = definition.Fields ?? [];
        var strings = fields.Where(field => field.Type == DiagnosticFieldType.String).ToArray();
        var recordBudget = (long)FixedDocumentOverhead + definition.Limits.MaxPayloadBytes +
                           definition.Limits.MaxRecordIdBytes + fields
                               .Select(field => field.Type == DiagnosticFieldType.String
                                   ? (long)field.MaxStringBytes!.Value *
                                     ((long)field.MaxValues * StoredStringExpansion +
                                      (field.Cardinality == DiagnosticFieldCardinality.Scalar ? ScalarSortExpansion : 0)) +
                                     (long)field.MaxValues * StoredFieldOverhead
                                   : (long)field.MaxValues * StoredFieldOverhead)
                               .OrderDescending()
                               .Take(definition.Limits.MaxFieldsPerRecord)
                               .Sum();
        if (recordBudget >= MaxBsonDocumentBytes)
            errors.Add(new(
                "provider.mongodb.record_document_budget.exceeded",
                "The declared payload, string, and cardinality bounds can exceed MongoDB's 16 MB record-document limit after canonical, search, and bounded-key projections.",
                "fields"));

        var maximumStringBound = strings.Select(field => field.MaxStringBytes!.Value).DefaultIfEmpty(0).Max();
        var projectedStringBudget = (long)maximumStringBound * Math.Max(
            (long)definition.Limits.MaxPredicateValues * QueryComparisonKeyExpansion,
            QuerySearchKeyExpansion);
        var queryBudget = (long)FixedDocumentOverhead +
                          (long)definition.Limits.MaxPredicateNodes * StoredFieldOverhead +
                          (long)definition.Limits.MaxPredicateValues * QueryValueOverhead +
                          projectedStringBudget;
        if (queryBudget >= MaxBsonDocumentBytes)
            errors.Add(new(
                "provider.mongodb.query_document_budget.exceeded",
                "The declared string and predicate-value bounds can exceed MongoDB's 16 MB query-command limit after comparison-key projection.",
                "limits.maxPredicateValues"));
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }
}
