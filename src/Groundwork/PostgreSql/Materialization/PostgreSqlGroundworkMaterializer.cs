using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using Groundwork.Relational.Materialization;
using Groundwork.Relational.Physicalization;
using Npgsql;

namespace Groundwork.PostgreSql.Materialization;

public sealed class PostgreSqlGroundworkMaterializer(NpgsqlConnection connection) : RelationalMaterializerBase(connection)
{
    protected override IReadOnlyList<string> SchemaStatements { get; } = [DocumentTableSql, IndexTableSql, IdentitySchemaSql, SchemaHistorySql];

    protected override string InsertSchemaHistorySql => """
        INSERT INTO groundwork_schema_history
        (manifest_id, manifest_version, provider_name, provider_version, applied_utc)
        VALUES (@manifestId, @manifestVersion, @providerName, @providerVersion, @appliedUtc)
        ON CONFLICT (manifest_id, manifest_version, provider_name, provider_version) DO NOTHING;
        """;

    protected override IReadOnlyList<string> CreateOptimizedProjectionStatements(MaterializedProjection projection)
    {
        var table = RelationalPhysicalizationNames.TableName(projection.UnitIdentity);
        var fields = projection.Fields;
        var columns = string.Join(",\n    ", fields.Select(field => $"{RelationalPhysicalizationNames.ColumnName(field)} TEXT NULL"));
        var statements = new List<string>
        {
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
                document_kind TEXT NOT NULL,
                storage_scope TEXT NOT NULL,
                document_id TEXT NOT NULL,
                document_version BIGINT NOT NULL{(fields.Count == 0 ? "" : $",\n    {columns}")},
                PRIMARY KEY (document_kind, storage_scope, document_id),
                FOREIGN KEY (document_kind, storage_scope, document_id)
                    REFERENCES groundwork_documents(document_kind, storage_scope, id)
                    ON DELETE CASCADE
            );
            """
        };

        statements.AddRange(fields.Select(field =>
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            var indexName = RelationalPhysicalizationNames.IndexName(projection.UnitIdentity, field, field.IsUnique);
            var unique = field.IsUnique ? "UNIQUE " : "";
            return $"""
                CREATE {unique}INDEX IF NOT EXISTS {indexName}
                ON {table}(storage_scope, {column})
                WHERE {column} IS NOT NULL;
                """;
        }));

        return statements;
    }

    private const string DocumentTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_documents (
            document_kind TEXT NOT NULL,
            storage_scope TEXT NOT NULL,
            id TEXT NOT NULL,
            id_comparison_key TEXT NOT NULL,
            id_lookup_key TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            version BIGINT NOT NULL,
            content_json TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (document_kind, storage_scope, id_lookup_key),
            UNIQUE (document_kind, storage_scope, id)
        );
        """;

    private const string IdentitySchemaSql = """
        CREATE TABLE IF NOT EXISTS groundwork_document_identity_schema (
            document_kind TEXT NOT NULL PRIMARY KEY,
            string_case_policy TEXT NOT NULL,
            comparison_algorithm TEXT NOT NULL,
            lookup_algorithm TEXT NOT NULL
        );
        """;

    private const string IndexTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_document_indexes (
            document_kind TEXT NOT NULL,
            storage_scope TEXT NOT NULL,
            index_name TEXT NOT NULL,
            index_value TEXT NOT NULL,
            document_id TEXT NOT NULL,
            is_unique INTEGER NOT NULL,
            PRIMARY KEY (document_kind, storage_scope, index_name, index_value, document_id),
            FOREIGN KEY (document_kind, storage_scope, document_id)
                REFERENCES groundwork_documents(document_kind, storage_scope, id)
                ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_groundwork_document_indexes_unique
        ON groundwork_document_indexes(document_kind, storage_scope, index_name, index_value)
        WHERE is_unique = 1;
        """;

    private const string SchemaHistorySql = """
        CREATE TABLE IF NOT EXISTS groundwork_schema_history (
            manifest_id TEXT NOT NULL,
            manifest_version TEXT NOT NULL,
            provider_name TEXT NOT NULL,
            provider_version TEXT NOT NULL,
            applied_utc TEXT NOT NULL,
            PRIMARY KEY (manifest_id, manifest_version, provider_name, provider_version)
        );
        """;
}
