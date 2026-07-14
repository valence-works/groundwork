using System.Text;

namespace Groundwork.DiagnosticRecords;

public static class DiagnosticStringProjectionLimits
{
    public const int MaxInputUtf8Bytes = 65_536;
    public const long MaxProjectedBytesPerRequest = 32L * 1024 * 1024;
}

internal static class DiagnosticStringProjectionBudget
{
    // A Unicode string projection retains a six-character comparison key and a seven-character
    // search key, and temporarily encodes the comparison key for hashing. The factor is expressed
    // against the input UTF-8 byte count and conservatively covers UTF-16 storage and hash input.
    internal const long MaxProjectedBytesPerRequest = DiagnosticStringProjectionLimits.MaxProjectedBytesPerRequest;
    internal const int WorstCaseManagedByteExpansion = 48;

    public static void AddDefinitionErrors(
        DiagnosticRecordStreamDefinition definition,
        List<DiagnosticValidationError> errors)
    {
        if (definition.Fields is null || definition.Limits is null)
            return;

        var validStrings = definition.Fields
            .Where(field => field.Type == DiagnosticFieldType.String &&
                            field.MaxStringBytes is > 0 &&
                            field.MaxValues > 0)
            .ToArray();
        var recordBytes = validStrings
            .Select(field => SaturatingMultiply(field.MaxStringBytes!.Value, field.MaxValues))
            .OrderDescending()
            .Take(Math.Max(0, definition.Limits.MaxFieldsPerRecord))
            .Aggregate(0L, SaturatingAdd);
        if (ProjectedBytes(recordBytes) > MaxProjectedBytesPerRequest)
            errors.Add(new(
                "definition.projection.record_budget.exceeded",
                $"The declared string and cardinality bounds can project one record beyond the {MaxProjectedBytesPerRequest}-byte comparison-memory budget.",
                "fields"));

        var maximumStringBytes = validStrings
            .Select(field => (long)field.MaxStringBytes!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var queryValues = SaturatingAdd(Math.Max(0, definition.Limits.MaxPredicateValues), 1);
        if (ProjectedBytes(SaturatingMultiply(maximumStringBytes, queryValues)) > MaxProjectedBytesPerRequest)
            errors.Add(new(
                "definition.projection.query_budget.exceeded",
                $"The declared string and predicate-value bounds can project one query beyond the {MaxProjectedBytesPerRequest}-byte comparison-memory budget.",
                "limits.maxPredicateValues"));
    }

    public static void AddAppendError(
        DiagnosticRecordBatch batch,
        List<DiagnosticValidationError> errors)
    {
        if (batch.Records is null)
            return;
        var inputBytes = 0L;
        foreach (var value in batch.Records
                     .Where(record => record.Fields is not null)
                     .SelectMany(record => record.Fields!.Values)
                     .Where(values => values is not null)
                     .SelectMany(values => values)
                     .Where(value => value.IsInitialized && value.Type == DiagnosticFieldType.String))
        {
            inputBytes = SaturatingAdd(inputBytes, Encoding.UTF8.GetByteCount(value.CanonicalValue));
        }
        if (ProjectedBytes(inputBytes) > MaxProjectedBytesPerRequest)
            errors.Add(new(
                "append.projection_budget.exceeded",
                $"The append batch can project beyond the {MaxProjectedBytesPerRequest}-byte comparison-memory budget.",
                "records"));
    }

    public static bool AddQueryError(
        DiagnosticRecordQuery query,
        IReadOnlyList<DiagnosticRecordPredicate> predicateNodes,
        List<DiagnosticValidationError> errors)
    {
        var inputBytes = predicateNodes
            .OfType<DiagnosticRecordPredicate.Comparison>()
            .Where(comparison => comparison.Values is not null)
            .SelectMany(comparison => comparison.Values)
            .Where(value => value.IsInitialized && value.Type == DiagnosticFieldType.String)
            .Aggregate(
                0L,
                (total, value) => SaturatingAdd(
                    total,
                    Encoding.UTF8.GetByteCount(value.CanonicalValue)));
        if (query.Continuation?.LastOrderValue is { IsInitialized: true, Type: DiagnosticFieldType.String } continuation)
            inputBytes = SaturatingAdd(inputBytes, Encoding.UTF8.GetByteCount(continuation.CanonicalValue));
        if (ProjectedBytes(inputBytes) <= MaxProjectedBytesPerRequest)
            return false;
        errors.Add(new(
            "query.projection_budget.exceeded",
            $"The query can project beyond the {MaxProjectedBytesPerRequest}-byte comparison-memory budget.",
            "predicate"));
        return true;
    }

    private static long ProjectedBytes(long inputBytes) =>
        SaturatingMultiply(inputBytes, WorstCaseManagedByteExpansion);

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0
            ? 0
            : left > long.MaxValue / right
                ? long.MaxValue
                : left * right;
}
