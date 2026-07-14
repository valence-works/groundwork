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
        Func<string, string> quote) =>
        RelationalMutationOperationIdentity.ExactPredicate(
            parts,
            quote,
            PostgreSqlUnboundedIdentityHash.Expression);
}
