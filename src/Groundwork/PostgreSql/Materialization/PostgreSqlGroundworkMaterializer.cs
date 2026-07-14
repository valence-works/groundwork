using System.Data.Common;
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

    protected override Task AcquireIdentitySchemaLockAsync(DbTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('groundwork.document-store.identity-schema', 0));",
            transaction,
            cancellationToken);

    protected override async Task<IReadOnlySet<string>> ReadDocumentColumnsAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'groundwork_documents';
            """,
            transaction);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    protected override async Task<IdentityLookupIndexShape> ReadIdentityLookupIndexShapeAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT i.indisunique,
                   i.indpred IS NULL,
                   i.indisvalid AND i.indisready,
                   attribute.attname
            FROM pg_class table_class
            JOIN pg_index i ON i.indrelid = table_class.oid
            JOIN pg_class index_class ON index_class.oid = i.indexrelid
            JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS key(attnum, ordinal) ON TRUE
            LEFT JOIN pg_attribute attribute ON attribute.attrelid = table_class.oid AND attribute.attnum = key.attnum
            WHERE table_class.relname = 'groundwork_documents'
              AND index_class.relname = 'ux_groundwork_documents_identity_lookup'
            ORDER BY key.ordinal;
            """,
            transaction);
        var columns = new List<string>();
        var exists = false;
        var unique = false;
        var coversAllRows = true;
        var usable = true;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            exists = true;
            unique = reader.GetBoolean(0);
            coversAllRows = reader.GetBoolean(1);
            usable = reader.GetBoolean(2);
            if (!reader.IsDBNull(3))
                columns.Add(reader.GetString(3));
        }
        return new(exists, unique, columns, coversAllRows, usable);
    }

    protected override async Task AddIdentityColumnsAsync(
        IReadOnlySet<string> existingColumns,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!existingColumns.Contains("id_comparison_key"))
            await ExecuteAsync("ALTER TABLE groundwork_documents ADD COLUMN id_comparison_key TEXT NULL;", transaction, cancellationToken);
        if (!existingColumns.Contains("id_lookup_key"))
            await ExecuteAsync("ALTER TABLE groundwork_documents ADD COLUMN id_lookup_key TEXT NULL;", transaction, cancellationToken);
    }

    protected override async Task FinalizeIdentityColumnsAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync("ALTER TABLE groundwork_documents ALTER COLUMN id_comparison_key SET NOT NULL;", transaction, cancellationToken);
        await ExecuteAsync("ALTER TABLE groundwork_documents ALTER COLUMN id_lookup_key SET NOT NULL;", transaction, cancellationToken);
    }

    protected override Task EnsureIdentityLookupIndexAsync(DbTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_groundwork_documents_identity_lookup
            ON groundwork_documents(document_kind, storage_scope, id_lookup_key);
            """,
            transaction,
            cancellationToken);

    protected override IReadOnlyList<string> RequiredIdentityLookupIndexColumns { get; } =
        ["document_kind", "storage_scope", "id_lookup_key"];

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
            PRIMARY KEY (document_kind, storage_scope, id)
        );
        """;

    private const string IdentitySchemaSql = """
        CREATE TABLE IF NOT EXISTS groundwork_document_identity_schema (
            storage_unit TEXT NOT NULL PRIMARY KEY,
            state_json TEXT NOT NULL
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
