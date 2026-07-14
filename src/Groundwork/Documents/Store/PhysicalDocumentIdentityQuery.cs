using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>
/// One provider-neutral identity comparison projected from a runtime document query. Adapters use
/// the plan-owned physical fields and these canonical values without interpreting identity policy.
/// </summary>
public sealed class PhysicalDocumentIdentityComparison
{
    internal PhysicalDocumentIdentityComparison(
        PhysicalQueryDocumentIdentityBinding identity,
        QueryComparisonOperator @operator,
        IReadOnlyList<PhysicalQueryIdentityValue> values)
    {
        Identity = identity;
        Operator = @operator;
        Values = Array.AsReadOnly(values.ToArray());
    }

    public PhysicalQueryDocumentIdentityBinding Identity { get; }

    public QueryComparisonOperator Operator { get; }

    public IReadOnlyList<PhysicalQueryIdentityValue> Values { get; }
}

public static class PhysicalDocumentIdentityQuery
{
    public static PhysicalDocumentIdentityComparison Bind(
        PhysicalQueryPlan plan,
        DocumentQueryComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.Path != PhysicalDocumentFieldPaths.Id)
        {
            throw new ArgumentException(
                $"Comparison path '{comparison.Path}' is not document identity.",
                nameof(comparison));
        }

        var operation = DocumentQueryOperations.ToPortable(comparison.Operator);
        if (comparison.Values.Any(value => value is null))
            throw new ArgumentException("Document identity query values cannot be null.", nameof(comparison));
        IEnumerable<PhysicalQueryIdentityValue> values = comparison.Values
            .Select(value => plan.DocumentIdentity.Bind(operation, value!));
        if (comparison.Operator == QueryComparisonOperator.In)
        {
            values = values
                .Distinct()
                .OrderBy(value => value.ComparisonKey, StringComparer.Ordinal)
                .ThenBy(
                    value => value is PhysicalQueryIdentityValue.Exact exact ? exact.LookupKey : null,
                    StringComparer.Ordinal);
        }
        return new PhysicalDocumentIdentityComparison(
            plan.DocumentIdentity,
            comparison.Operator,
            values.ToArray());
    }
}
