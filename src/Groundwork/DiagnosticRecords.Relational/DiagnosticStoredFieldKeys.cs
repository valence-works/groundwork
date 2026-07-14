using System.Text;

namespace Groundwork.DiagnosticRecords.Relational;

internal sealed record DiagnosticStoredFieldKeys(
    string CanonicalValue,
    string ComparisonKey,
    string ComparisonKeyPrefix,
    string ComparisonKeyHash,
    string SearchKey)
{
    public static DiagnosticStoredFieldKeys Create(
        DiagnosticFieldValue value,
        DiagnosticFieldDefinition definition)
    {
        DiagnosticStringComparisonProjection projection;
        if (value.Type == DiagnosticFieldType.String)
        {
            projection = DiagnosticStringComparisonKey.Project(value.CanonicalValue, definition.CasePolicy);
        }
        else
        {
            var comparison = DiagnosticComparisonKeys.Create(value, definition.CasePolicy);
            projection = new(
                comparison,
                DiagnosticStringComparisonKey.CreateBoundedPrefix(comparison),
                DiagnosticStringComparisonKey.CreateHash(comparison),
                comparison);
        }
        return new(
            EncodeCanonical(value),
            projection.ComparisonKey,
            projection.ComparisonKeyPrefix,
            projection.ComparisonKeyHash,
            projection.SearchKey);
    }

    public static string EncodeCanonical(DiagnosticFieldValue value) =>
        value.Type == DiagnosticFieldType.String
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(value.CanonicalValue))
            : value.CanonicalValue;

    public static string DecodeCanonical(DiagnosticFieldType type, string storedValue) =>
        type == DiagnosticFieldType.String
            ? Encoding.UTF8.GetString(Convert.FromBase64String(storedValue))
            : storedValue;
}
