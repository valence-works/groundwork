using System.Data.Common;

namespace Groundwork.Relational.Documents;

public class RelationalDocumentStoreDialect
{
    public virtual string ParameterPrefix => "@";

    public string Parameter(string name) => $"{ParameterPrefix}{name}";

    public virtual object Boolean(bool value) => value ? 1 : 0;

    public virtual void ValidateDocumentIdentity(string value) =>
        ArgumentNullException.ThrowIfNull(value);

    /// <summary>Builds the offset-paging clause appended to a closed query. Skip/take are validated non-negative integers.</summary>
    public virtual string PaginationClause(int skip, int? take)
    {
        if (take is { } limit)
            return $"LIMIT {limit} OFFSET {skip}";

        return skip > 0 ? $"LIMIT -1 OFFSET {skip}" : string.Empty;
    }

    /// <summary>Builds a case-insensitive substring predicate for the Contains operator.</summary>
    public virtual string ContainsPredicate(string columnExpression, string patternParameterName) =>
        $@"LOWER({columnExpression}) LIKE LOWER({Parameter(patternParameterName)}) ESCAPE '\'";

    public virtual string ExactEqualityPredicate(string columnExpression, string parameterReference) =>
        $"{columnExpression} = {parameterReference}";

    public virtual string ExactJoinPredicate(string leftColumnExpression, string rightColumnExpression) =>
        $"{leftColumnExpression} = {rightColumnExpression}";

    public virtual string ExactInPredicate(string columnExpression, IReadOnlyList<string> parameterReferences) =>
        $"{columnExpression} IN ({string.Join(", ", parameterReferences)})";

    public virtual bool IsDuplicateDocumentKeyException(DbException exception) => false;

    public virtual bool IsUniqueIndexException(DbException exception) => false;

    public virtual bool IsWriteDependencyException(DbException exception) => false;

    public virtual string InsertDocumentSql => $$"""
        INSERT INTO groundwork_documents
        (document_kind, storage_scope, id, id_comparison_key, id_lookup_key, schema_version, version, content_json, created_utc, updated_utc)
        VALUES ({{Parameter("kind")}}, {{Parameter("scope")}}, {{Parameter("id")}}, {{Parameter("idComparisonKey")}}, {{Parameter("idLookupKey")}}, {{Parameter("schemaVersion")}}, {{Parameter("version")}}, {{Parameter("content")}}, {{Parameter("createdUtc")}}, {{Parameter("updatedUtc")}});
        """;

    public virtual string UpdateDocumentSql => $$"""
        UPDATE groundwork_documents
        SET schema_version = {{Parameter("schemaVersion")}},
            version = {{Parameter("version")}},
            content_json = {{Parameter("content")}},
            updated_utc = {{Parameter("updatedUtc")}}
        WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}}
          AND id_lookup_key = {{Parameter("idLookupKey")}} AND id_comparison_key = {{Parameter("idComparisonKey")}}
          AND id = {{Parameter("authoritativeId")}};
        """;

    public virtual string UpdateDocumentCommandSql(bool checkVersion) =>
        checkVersion
            ? $$"""
              UPDATE groundwork_documents
              SET schema_version = {{Parameter("schemaVersion")}},
                  version = {{Parameter("version")}},
                  content_json = {{Parameter("content")}},
                  updated_utc = {{Parameter("updatedUtc")}}
              WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}}
                AND id_lookup_key = {{Parameter("idLookupKey")}} AND id_comparison_key = {{Parameter("idComparisonKey")}}
                AND id = {{Parameter("authoritativeId")}} AND version = {{Parameter("expectedVersion")}};
              """
            : UpdateDocumentSql;

    public virtual string LoadDocumentSql => $$"""
        SELECT document_kind, storage_scope, id, schema_version, version, content_json, created_utc, updated_utc,
               id_comparison_key, id_lookup_key
        FROM groundwork_documents
        WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}}
          AND id_lookup_key = {{Parameter("idLookupKey")}};
        """;

    public virtual string DeleteDocumentSql => $$"""
        DELETE FROM groundwork_documents
        WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}}
          AND id_lookup_key = {{Parameter("idLookupKey")}} AND id_comparison_key = {{Parameter("idComparisonKey")}}
          AND id = {{Parameter("authoritativeId")}};
        """;

    public virtual string DeleteDocumentCommandSql(bool checkVersion) =>
        checkVersion
            ? $$"""
              DELETE FROM groundwork_documents
              WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}}
                AND id_lookup_key = {{Parameter("idLookupKey")}} AND id_comparison_key = {{Parameter("idComparisonKey")}}
                AND id = {{Parameter("authoritativeId")}} AND version = {{Parameter("expectedVersion")}};
              """
            : DeleteDocumentSql;

    public virtual string DeleteIndexesSql => $$"""
        DELETE FROM groundwork_document_indexes
        WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}} AND document_id = {{Parameter("id")}};
        """;

    public virtual string InsertIndexSql => $$"""
        INSERT INTO groundwork_document_indexes
        (document_kind, storage_scope, index_name, index_value, document_id, is_unique)
        VALUES ({{Parameter("kind")}}, {{Parameter("scope")}}, {{Parameter("index")}}, {{Parameter("value")}}, {{Parameter("documentId")}}, {{Parameter("isUnique")}});
        """;

    public virtual string QueryByIndexSql => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN groundwork_document_indexes i
            ON i.document_kind = d.document_kind AND i.storage_scope = d.storage_scope AND i.document_id = d.id
        WHERE i.document_kind = {{Parameter("kind")}} AND i.storage_scope = {{Parameter("scope")}} AND i.index_name = {{Parameter("index")}} AND i.index_value = {{Parameter("value")}}
        ORDER BY d.id
        LIMIT {{Parameter("take")}} OFFSET {{Parameter("skip")}};
        """;

    public virtual string QueryByIndexAcrossScopesSql => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN groundwork_document_indexes i
            ON i.document_kind = d.document_kind AND i.storage_scope = d.storage_scope AND i.document_id = d.id
        WHERE i.document_kind = {{Parameter("kind")}} AND i.index_name = {{Parameter("index")}} AND i.index_value = {{Parameter("value")}}
        ORDER BY d.storage_scope, d.id
        LIMIT {{Parameter("take")}} OFFSET {{Parameter("skip")}};
        """;

    public virtual string DeletePhysicalizedSql(string tableName) => $$"""
        DELETE FROM {{tableName}}
        WHERE document_kind = {{Parameter("kind")}} AND storage_scope = {{Parameter("scope")}} AND document_id = {{Parameter("id")}};
        """;

    public virtual string InsertPhysicalizedSql(string tableName, IReadOnlyList<string> columnNames)
    {
        var columns = string.Join(", ", columnNames);
        var parameters = string.Join(", ", columnNames.Select((_, index) => Parameter($"physicalized{index}")));
        return $$"""
            INSERT INTO {{tableName}}
            (document_kind, storage_scope, document_id, document_version{{(columnNames.Count == 0 ? "" : $", {columns}")}})
            VALUES ({{Parameter("kind")}}, {{Parameter("scope")}}, {{Parameter("id")}}, {{Parameter("version")}}{{(columnNames.Count == 0 ? "" : $", {parameters}")}});
            """;
    }

    public virtual string QueryByPhysicalizedSql(string tableName, string columnName) => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN {{tableName}} p
            ON p.document_kind = d.document_kind AND p.storage_scope = d.storage_scope AND p.document_id = d.id
        WHERE p.document_kind = {{Parameter("kind")}} AND p.storage_scope = {{Parameter("scope")}} AND p.{{columnName}} = {{Parameter("value")}}
        ORDER BY d.id
        LIMIT {{Parameter("take")}} OFFSET {{Parameter("skip")}};
        """;

    public virtual string QueryByPhysicalizedAcrossScopesSql(string tableName, string columnName) => $$"""
        SELECT d.document_kind, d.storage_scope, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc
        FROM groundwork_documents d
        INNER JOIN {{tableName}} p
            ON p.document_kind = d.document_kind AND p.storage_scope = d.storage_scope AND p.document_id = d.id
        WHERE p.document_kind = {{Parameter("kind")}} AND p.{{columnName}} = {{Parameter("value")}}
        ORDER BY d.storage_scope, d.id
        LIMIT {{Parameter("take")}} OFFSET {{Parameter("skip")}};
        """;
}
