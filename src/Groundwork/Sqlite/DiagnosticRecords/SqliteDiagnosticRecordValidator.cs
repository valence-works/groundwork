using Groundwork.DiagnosticRecords;

namespace Groundwork.Sqlite.DiagnosticRecords;

internal static class SqliteDiagnosticRecordValidator
{
    internal const int MaxStringBytes = DiagnosticStringProjectionLimits.MaxInputUtf8Bytes;

    public static void ValidateDefinitionAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = (definition.Fields ?? [])
            .Where(field => field.Type == DiagnosticFieldType.String && field.MaxStringBytes > MaxStringBytes)
            .Select(field => new DiagnosticValidationError(
                "provider.sqlite.string_bound.too_large",
                $"SQLite diagnostic string fields are limited to {MaxStringBytes} UTF-8 bytes by the bounded comparison-projection contract.",
                $"fields.{field.Name}.maxStringBytes"))
            .ToArray();
        if (errors.Length > 0)
            throw new DiagnosticRecordValidationException(errors);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
    }
}
