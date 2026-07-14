namespace Groundwork.Relational.Documents;

internal static class RelationalMutationOperationIdentity
{
    public static string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote,
        Func<string, string> hashExpression)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(hashExpression);
        return string.Join(" AND ", parts.SelectMany(part => new[]
        {
            $"{quote(KeyColumn(part.ColumnIdentifier))} = {hashExpression(part.ValueExpression)}",
            $"{quote(part.ColumnIdentifier)} = {part.ValueExpression}"
        }));
    }

    private static string KeyColumn(string retainedColumn) => retainedColumn switch
    {
        "manifest_id" => "manifest_key",
        "provider_name" => "provider_key",
        "storage_unit" => "storage_unit_key",
        "storage_scope" => "storage_scope_key",
        "operation_id" => "operation_key",
        _ => throw new ArgumentOutOfRangeException(
            nameof(retainedColumn),
            retainedColumn,
            "A mutation-ledger identity column is not recognized.")
    };
}
