using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using Groundwork.Relational.Materialization;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Materialization;

public sealed class SqlServerGroundworkMaterializer(SqlConnection connection) : RelationalMaterializerBase(connection)
{
    protected override IReadOnlyList<string> SchemaStatements { get; } = [DocumentTableSql, IndexTableSql, SchemaHistorySql];

    protected override string InsertSchemaHistorySql => $$"""
        IF NOT EXISTS (
            SELECT 1
            FROM groundwork_schema_history WITH (UPDLOCK, HOLDLOCK)
            WHERE {{ExactEqualityPredicate("manifest_id", "@manifestId")}}
              AND {{ExactEqualityPredicate("manifest_version", "@manifestVersion")}}
              AND {{ExactEqualityPredicate("provider_name", "@providerName")}}
              AND {{ExactEqualityPredicate("provider_version", "@providerVersion")}}
        )
        BEGIN
            INSERT INTO groundwork_schema_history
            (manifest_id, manifest_version, provider_name, provider_version, applied_utc)
            VALUES (@manifestId, @manifestVersion, @providerName, @providerVersion, @appliedUtc);
        END;
        """;

    protected override string ExactEqualityPredicate(string columnExpression, string parameterReference) =>
        $"{columnExpression}_key = {HashParameter(parameterReference)} AND {columnExpression} = {parameterReference}";

    protected override IReadOnlyList<string> CreateOptimizedProjectionStatements(MaterializedProjection projection)
    {
        var table = RelationalPhysicalizationNames.TableName(projection.UnitIdentity);
        var fields = projection.Fields;
        var columns = string.Join(",\n        ", fields.Select(field =>
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            return $"{column} NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NULL,\n        {HashKey(column)}";
        }));
        var statements = new List<string>
        {
            $"""
            IF OBJECT_ID(N'{table}', N'U') IS NULL
            BEGIN
                CREATE TABLE {table} (
                    document_kind NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    storage_scope NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    document_id NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    {RequiredHashKey("document_kind")},
                    {RequiredHashKey("storage_scope", 256)},
                    {RequiredHashKey("document_id")},
                    document_version BIGINT NOT NULL{(fields.Count == 0 ? "" : $",\n        {columns}")},
                    CONSTRAINT pk_{table} PRIMARY KEY (document_kind_key, storage_scope_key, document_id_key),
                    CONSTRAINT fk_{table}_documents FOREIGN KEY (document_kind_key, storage_scope_key, document_id_key)
                        REFERENCES groundwork_documents(document_kind_key, storage_scope_key, id_key)
                        ON DELETE CASCADE
                );
            END;
            """
        };

        statements.AddRange(fields.Select(field =>
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            var indexName = RelationalPhysicalizationNames.IndexName(projection.UnitIdentity, field, field.IsUnique);
            var unique = field.IsUnique ? "UNIQUE " : "";
            return $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'{table}'))
                BEGIN
                    CREATE {unique}INDEX {indexName}
                    ON {table}(storage_scope_key, {column}_key)
                    WHERE {column} IS NOT NULL;
                END;
                """;
        }));

        return statements;
    }

    private static readonly string DocumentTableSql = $"""
        IF OBJECT_ID(N'groundwork_documents', N'U') IS NULL
        BEGIN
            CREATE TABLE groundwork_documents (
                document_kind NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                storage_scope NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                id NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                {RequiredHashKey("document_kind")},
                {RequiredHashKey("storage_scope", 256)},
                {RequiredHashKey("id")},
                schema_version NVARCHAR(128) NOT NULL,
                version BIGINT NOT NULL,
                content_json NVARCHAR(MAX) NOT NULL,
                created_utc NVARCHAR(64) NOT NULL,
                updated_utc NVARCHAR(64) NOT NULL,
                CONSTRAINT pk_groundwork_documents PRIMARY KEY (document_kind_key, storage_scope_key, id_key)
            );
        END;
        """;

    private static readonly string IndexTableSql = $"""
        IF OBJECT_ID(N'groundwork_document_indexes', N'U') IS NULL
        BEGIN
            CREATE TABLE groundwork_document_indexes (
                document_kind NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                storage_scope NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                index_name NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                index_value NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                document_id NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                {RequiredHashKey("document_kind")},
                {RequiredHashKey("storage_scope", 256)},
                {RequiredHashKey("index_name")},
                {RequiredHashKey("index_value")},
                {RequiredHashKey("document_id")},
                is_unique BIT NOT NULL,
                CONSTRAINT pk_groundwork_document_indexes PRIMARY KEY (document_kind_key, storage_scope_key, index_name_key, index_value_key, document_id_key),
                CONSTRAINT fk_groundwork_document_indexes_documents FOREIGN KEY (document_kind_key, storage_scope_key, document_id_key)
                    REFERENCES groundwork_documents(document_kind_key, storage_scope_key, id_key)
                    ON DELETE CASCADE
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_groundwork_document_indexes_unique' AND object_id = OBJECT_ID(N'groundwork_document_indexes'))
        BEGIN
            CREATE UNIQUE INDEX ux_groundwork_document_indexes_unique
            ON groundwork_document_indexes(document_kind_key, storage_scope_key, index_name_key, index_value_key)
            WHERE is_unique = 1;
        END;
        """;

    private static readonly string SchemaHistorySql = $"""
        IF OBJECT_ID(N'groundwork_schema_history', N'U') IS NULL
        BEGIN
            CREATE TABLE groundwork_schema_history (
                manifest_id NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                manifest_version NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_name NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_version NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                {RequiredHashKey("manifest_id")},
                {RequiredHashKey("manifest_version", 256)},
                {RequiredHashKey("provider_name", 512)},
                {RequiredHashKey("provider_version", 256)},
                applied_utc NVARCHAR(64) NOT NULL,
                CONSTRAINT pk_groundwork_schema_history PRIMARY KEY (manifest_id_key, manifest_version_key, provider_name_key, provider_version_key)
            );
        END;
        """;

    private static string RequiredHashKey(string column, int inputBytes = 900) =>
        $"{column}_key AS CONVERT(BINARY(32), HASHBYTES('SHA2_256', CONVERT(VARBINARY({inputBytes}), {column}))) PERSISTED NOT NULL";

    private static string HashKey(string column, int inputBytes = 900) =>
        $"{column}_key AS CONVERT(BINARY(32), HASHBYTES('SHA2_256', CONVERT(VARBINARY({inputBytes}), {column}))) PERSISTED";

    private static string HashParameter(string parameterReference) =>
        $"CONVERT(BINARY(32), HASHBYTES('SHA2_256', CONVERT(VARBINARY(900), {parameterReference})))";
}
