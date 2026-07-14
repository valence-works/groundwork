using System.Data.Common;
using Groundwork.Relational.Documents;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

internal sealed class SqlServerDocumentStoreDialect : RelationalDocumentStoreDialect
{
    public override string ExactEqualityPredicate(string columnExpression, string parameterReference) =>
        $"{columnExpression}_key = {HashParameter(parameterReference)} AND {columnExpression} = {parameterReference}";

    public override string ExactJoinPredicate(string leftColumnExpression, string rightColumnExpression) =>
        $"{leftColumnExpression}_key = {rightColumnExpression}_key AND {leftColumnExpression} = {rightColumnExpression}";

    public override string ExactInPredicate(string columnExpression, IReadOnlyList<string> parameterReferences) =>
        $"({string.Join(" OR ", parameterReferences.Select(parameter => $"({ExactEqualityPredicate(columnExpression, parameter)})"))})";

    public override string PaginationClause(int skip, int? take)
    {
        if (take is { } limit)
            return $"OFFSET {skip} ROWS FETCH NEXT {limit} ROWS ONLY";

        return skip > 0 ? $"OFFSET {skip} ROWS" : string.Empty;
    }

    public override string UpdateDocumentSql => $$"""
        UPDATE groundwork_documents
        SET schema_version = {{Parameter("schemaVersion")}},
            version = {{Parameter("version")}},
            content_json = {{Parameter("content")}},
            updated_utc = {{Parameter("updatedUtc")}}
        WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
          AND id_lookup_key = {{Parameter("idLookupKey")}}
          AND id_comparison_key = {{Parameter("idComparisonKey")}}
          AND {{ExactEqualityPredicate("id", Parameter("authoritativeId"))}};
        """;

    public override string UpdateDocumentCommandSql(bool checkVersion) =>
        checkVersion
            ? $$"""
              UPDATE groundwork_documents
              SET schema_version = {{Parameter("schemaVersion")}},
                  version = {{Parameter("version")}},
                  content_json = {{Parameter("content")}},
                  updated_utc = {{Parameter("updatedUtc")}}
              WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
                AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
                AND id_lookup_key = {{Parameter("idLookupKey")}}
                AND id_comparison_key = {{Parameter("idComparisonKey")}}
                AND {{ExactEqualityPredicate("id", Parameter("authoritativeId"))}}
                AND version = {{Parameter("expectedVersion")}};
              """
            : UpdateDocumentSql;

    public override string LoadDocumentSql => $$"""
        SELECT document_kind, storage_scope, id, schema_version, version, content_json, created_utc, updated_utc,
               id_comparison_key, id_lookup_key
        FROM groundwork_documents
        WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
          AND id_lookup_key = {{Parameter("idLookupKey")}};
        """;

    public override string DeleteDocumentSql => $$"""
        DELETE FROM groundwork_documents
        WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
          AND id_lookup_key = {{Parameter("idLookupKey")}}
          AND id_comparison_key = {{Parameter("idComparisonKey")}}
          AND {{ExactEqualityPredicate("id", Parameter("authoritativeId"))}};
        """;

    public override string DeleteDocumentCommandSql(bool checkVersion) =>
        checkVersion
            ? $$"""
              DELETE FROM groundwork_documents
              WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
                AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
                AND id_lookup_key = {{Parameter("idLookupKey")}}
                AND id_comparison_key = {{Parameter("idComparisonKey")}}
                AND {{ExactEqualityPredicate("id", Parameter("authoritativeId"))}}
                AND version = {{Parameter("expectedVersion")}};
              """
            : DeleteDocumentSql;

    public override string DeleteIndexesSql => $$"""
        DELETE FROM groundwork_document_indexes
        WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
          AND {{ExactEqualityPredicate("document_id", Parameter("id"))}};
        """;

    public override string QueryByIndexSql => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN groundwork_document_indexes i
            ON {{ExactJoinPredicate("i.document_kind", "d.document_kind")}}
           AND {{ExactJoinPredicate("i.storage_scope", "d.storage_scope")}}
           AND {{ExactJoinPredicate("i.document_id", "d.id")}}
        WHERE {{ExactEqualityPredicate("i.document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("i.storage_scope", Parameter("scope"))}}
          AND {{ExactEqualityPredicate("i.index_name", Parameter("index"))}}
          AND {{ExactEqualityPredicate("i.index_value", Parameter("value"))}}
        ORDER BY d.id
        OFFSET {{Parameter("skip")}} ROWS FETCH NEXT {{Parameter("take")}} ROWS ONLY;
        """;

    public override string QueryByIndexAcrossScopesSql => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN groundwork_document_indexes i
            ON {{ExactJoinPredicate("i.document_kind", "d.document_kind")}}
           AND {{ExactJoinPredicate("i.storage_scope", "d.storage_scope")}}
           AND {{ExactJoinPredicate("i.document_id", "d.id")}}
        WHERE {{ExactEqualityPredicate("i.document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("i.index_name", Parameter("index"))}}
          AND {{ExactEqualityPredicate("i.index_value", Parameter("value"))}}
        ORDER BY d.storage_scope, d.id
        OFFSET {{Parameter("skip")}} ROWS FETCH NEXT {{Parameter("take")}} ROWS ONLY;
        """;

    public override string QueryByPhysicalizedSql(string tableName, string columnName) => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN {{tableName}} p
            ON {{ExactJoinPredicate("p.document_kind", "d.document_kind")}}
           AND {{ExactJoinPredicate("p.storage_scope", "d.storage_scope")}}
           AND {{ExactJoinPredicate("p.document_id", "d.id")}}
        WHERE {{ExactEqualityPredicate("p.document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("p.storage_scope", Parameter("scope"))}}
          AND {{ExactEqualityPredicate($"p.{columnName}", Parameter("value"))}}
        ORDER BY d.id
        OFFSET {{Parameter("skip")}} ROWS FETCH NEXT {{Parameter("take")}} ROWS ONLY;
        """;

    public override string QueryByPhysicalizedAcrossScopesSql(string tableName, string columnName) => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN {{tableName}} p
            ON {{ExactJoinPredicate("p.document_kind", "d.document_kind")}}
           AND {{ExactJoinPredicate("p.storage_scope", "d.storage_scope")}}
           AND {{ExactJoinPredicate("p.document_id", "d.id")}}
        WHERE {{ExactEqualityPredicate("p.document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate($"p.{columnName}", Parameter("value"))}}
        ORDER BY d.storage_scope, d.id
        OFFSET {{Parameter("skip")}} ROWS FETCH NEXT {{Parameter("take")}} ROWS ONLY;
        """;

    public override string DeletePhysicalizedSql(string tableName) => $$"""
        DELETE FROM {{tableName}}
        WHERE {{ExactEqualityPredicate("document_kind", Parameter("kind"))}}
          AND {{ExactEqualityPredicate("storage_scope", Parameter("scope"))}}
          AND {{ExactEqualityPredicate("document_id", Parameter("id"))}};
        """;

    public override bool IsDuplicateDocumentKeyException(DbException exception) =>
        exception is SqlException { Number: 2627 or 2601 } sqlException &&
        (sqlException.Message.Contains("pk_groundwork_documents", StringComparison.OrdinalIgnoreCase) ||
         sqlException.Message.Contains("ux_groundwork_documents_identity_lookup", StringComparison.OrdinalIgnoreCase));

    public override bool IsUniqueIndexException(DbException exception) =>
        exception is SqlException { Number: 2627 or 2601 };

    public override bool IsWriteDependencyException(DbException exception) =>
        exception is SqlException { Number: 547 };

    private static string HashParameter(string parameterReference) =>
        $"CONVERT(BINARY(32), HASHBYTES('SHA2_256', CONVERT(VARBINARY(900), {parameterReference})))";
}
