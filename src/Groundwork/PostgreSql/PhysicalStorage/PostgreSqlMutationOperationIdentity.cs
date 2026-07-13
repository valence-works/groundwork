using Groundwork.Relational.Documents;

namespace Groundwork.PostgreSql.PhysicalStorage;

internal static class PostgreSqlUnboundedIdentityHash
{
    public const string FunctionName = "groundwork_utf8_sha256";

    public static string Expression(string valueExpression) =>
        $"{FunctionName}({valueExpression})";
}

internal static class PostgreSqlMutationOperationIdentity
{
    public static string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part => new[]
        {
            $"{quote(KeyColumn(part.ColumnIdentifier))} = {PostgreSqlUnboundedIdentityHash.Expression(part.ValueExpression)}",
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
