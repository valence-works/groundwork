using Groundwork.DiagnosticRecords;

namespace Groundwork.PostgreSql.DiagnosticRecords;

internal static class PostgreSqlDiagnosticRecordValidator
{
    private const int FixedQueryParameterBudget = 11;
    private const int FieldParametersPerPredicateNode = 2;
    private const int MaxParametersPerCommand = 65_535;

    public static void ValidateDefinitionAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
        if ((long)definition.Limits.MaxPredicateValues +
            (long)definition.Limits.MaxPredicateNodes * FieldParametersPerPredicateNode +
            FixedQueryParameterBudget <= MaxParametersPerCommand)
            return;

        throw new DiagnosticRecordValidationException(
        [
            new(
                "provider.postgresql.parameter_budget.exceeded",
                "The declared predicate node and value bounds can exceed PostgreSQL's 65,535-parameter command limit.",
                "limits")
        ]);
    }
}
