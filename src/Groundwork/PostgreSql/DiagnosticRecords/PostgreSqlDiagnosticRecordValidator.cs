using Groundwork.DiagnosticRecords;

namespace Groundwork.PostgreSql.DiagnosticRecords;

internal static class PostgreSqlDiagnosticRecordValidator
{
    internal const int MaxStringBytes = DiagnosticStringProjectionLimits.MaxInputUtf8Bytes;
    private const int FixedQueryParameterBudget = 12;
    private const int FieldParametersPerPredicateNode = 2;
    private const int ParametersPerPredicateValue = 2;
    private const int MaxParametersPerCommand = 65_535;

    public static void ValidateDefinitionAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var stringErrors = (definition.Fields ?? [])
            .Where(field => field.Type == DiagnosticFieldType.String && field.MaxStringBytes > MaxStringBytes)
            .Select(field => new DiagnosticValidationError(
                "provider.postgresql.string_bound.too_large",
                $"PostgreSQL diagnostic string fields are limited to {MaxStringBytes} UTF-8 bytes by the bounded comparison-projection contract.",
                $"fields.{field.Name}.maxStringBytes"))
            .ToArray();
        if (stringErrors.Length > 0)
            throw new DiagnosticRecordValidationException(stringErrors);
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
        var errors = new List<DiagnosticValidationError>();
        if ((long)definition.Limits.MaxPredicateValues * ParametersPerPredicateValue +
            (long)definition.Limits.MaxPredicateNodes * FieldParametersPerPredicateNode +
            FixedQueryParameterBudget > MaxParametersPerCommand)
            errors.Add(new(
                "provider.postgresql.parameter_budget.exceeded",
                "The declared predicate node and value bounds can exceed PostgreSQL's 65,535-parameter command limit.",
                "limits"));
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }
}
