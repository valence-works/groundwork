using System.Data.Common;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

internal sealed class PostgreSqlDocumentStoreDialect : RelationalDocumentStoreDialect
{
    public override string PaginationClause(int skip, int? take)
    {
        if (take is { } limit)
            return $"LIMIT {limit} OFFSET {skip}";

        return skip > 0 ? $"OFFSET {skip}" : string.Empty;
    }

    public override string ContainsPredicate(string columnExpression, string patternParameterName) =>
        $@"{columnExpression} ILIKE {Parameter(patternParameterName)} ESCAPE '\'";

    public override bool IsDuplicateDocumentKeyException(DbException exception) =>
        exception is PostgresException { SqlState: "23505", ConstraintName: "groundwork_documents_pkey" };

    public override bool IsUniqueIndexException(DbException exception) =>
        exception is PostgresException { SqlState: "23505" };

    public override bool IsWriteDependencyException(DbException exception) =>
        exception is PostgresException { SqlState: "23503" };
}
