using System.Text;
using Groundwork.DiagnosticRecords;

namespace Groundwork.SqlServer.DiagnosticRecords;

internal static class SqlServerDiagnosticRecordValidator
{
    internal const int MaxIdentifierBytes = 64;
    internal const int MaxRecordIdBytes = 128;
    internal const int MaxOrdinalStringBytes = 128;
    private const int FixedQueryParameterBudget = 11;
    private const int FieldParametersPerPredicateNode = 2;

    public static void ValidateDefinitionAndThrow(DiagnosticRecordStreamDefinition definition)
    {
        DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(definition);
        var errors = new List<DiagnosticValidationError>();
        ValidateBound(definition.Stream.Value, MaxIdentifierBytes, "provider.sql_server.stream.too_large", "stream", errors);
        if (definition.Limits.MaxRecordIdBytes > MaxRecordIdBytes)
            errors.Add(new("provider.sql_server.record_id_bound.too_large", $"SQL Server diagnostic record ids are limited to {MaxRecordIdBytes} UTF-8 bytes.", "limits.maxRecordIdBytes"));
        if ((long)definition.Limits.MaxPredicateValues +
            (long)definition.Limits.MaxPredicateNodes * FieldParametersPerPredicateNode +
            FixedQueryParameterBudget > 2_100)
            errors.Add(new("provider.sql_server.parameter_budget.exceeded", "The declared predicate node and value bounds can exceed SQL Server's 2,100-parameter command limit.", "limits"));
        foreach (var field in definition.Fields)
        {
            ValidateBound(field.Name, MaxIdentifierBytes, "provider.sql_server.field_name.too_large", $"fields.{field.Name}.name", errors);
            if (field.Type == DiagnosticFieldType.String && field.MaxStringBytes > MaxOrdinalStringBytes)
                errors.Add(new("provider.sql_server.string_bound.too_large", $"SQL Server ordinal string fields are limited to {MaxOrdinalStringBytes} UTF-8 bytes.", $"fields.{field.Name}.maxStringBytes"));
        }
        ThrowIfAny(errors);
    }

    public static void ValidateScopeAndThrow(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        var errors = new List<DiagnosticValidationError>();
        ValidateBound(scope.TenantId, MaxIdentifierBytes, "provider.sql_server.tenant.too_large", "scope.tenantId", errors);
        ValidateBound(scope.ScopeId, MaxIdentifierBytes, "provider.sql_server.scope.too_large", "scope.scopeId", errors);
        ValidateBound(stream.Value, MaxIdentifierBytes, "provider.sql_server.stream.too_large", "stream", errors);
        ThrowIfAny(errors);
    }

    public static void ValidateAppendAndThrow(DiagnosticRecordBatch batch)
    {
        ValidateOperationAndThrow(batch.Scope, batch.Stream, batch.OperationId);
        var errors = new List<DiagnosticValidationError>();
        if (batch.Records is not null)
            foreach (var record in batch.Records)
                ValidateBound(record.RecordId, MaxRecordIdBytes, "provider.sql_server.record_id.invalid", "records.recordId", errors);
        ThrowIfAny(errors);
    }

    public static void ValidateOperationAndThrow(DiagnosticStorageScope scope, DiagnosticStreamId stream, DiagnosticOperationId operationId)
    {
        ValidateScopeAndThrow(scope, stream);
        var errors = new List<DiagnosticValidationError>();
        ValidateBound(operationId.Nonce, MaxIdentifierBytes, "provider.sql_server.nonce.too_large", "operationId.nonce", errors);
        ThrowIfAny(errors);
    }

    private static void ValidateBound(string? value, int maxBytes, string code, string path, List<DiagnosticValidationError> errors)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (!DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(value))
            errors.Add(new(code, "SQL Server diagnostic identifiers use the portable U+0020 through U+007E domain.", path));
        else if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            errors.Add(new(code, "SQL Server diagnostic identifiers cannot start or end with whitespace.", path));
        else if (Encoding.UTF8.GetByteCount(value) > maxBytes)
            errors.Add(new(code, $"The value is limited to {maxBytes} UTF-8 bytes by the SQL Server diagnostic schema.", path));
    }

    private static void ThrowIfAny(List<DiagnosticValidationError> errors)
    {
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }
}
