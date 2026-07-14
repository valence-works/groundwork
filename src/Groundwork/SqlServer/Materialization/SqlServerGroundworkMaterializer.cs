using System.Data.Common;
using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using Groundwork.Relational.Materialization;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Materialization;

public sealed class SqlServerGroundworkMaterializer(SqlConnection connection) : RelationalMaterializerBase(connection)
{
    protected override IReadOnlyList<string> SchemaStatements { get; } = [DocumentTableSql, IndexTableSql, IdentitySchemaSql, SchemaHistorySql];

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

    protected override async Task AcquireIdentitySchemaLockAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DECLARE @result INT;
            EXEC @result = sys.sp_getapplock
                @Resource = N'groundwork.document-store.identity-schema',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 30000;
            SELECT @result;
            """,
            transaction);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
            throw new InvalidOperationException("Could not acquire the Document Store identity schema evolution lock.");
    }

    protected override async Task<IReadOnlySet<string>> ReadDocumentColumnsAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'groundwork_documents');",
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
            SELECT i.is_unique, i.has_filter, i.is_disabled, i.is_hypothetical, c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'groundwork_documents')
              AND i.name = N'ux_groundwork_documents_identity_lookup'
              AND ic.key_ordinal > 0
            ORDER BY ic.key_ordinal;
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
            coversAllRows = !reader.GetBoolean(1);
            usable = !reader.GetBoolean(2) && !reader.GetBoolean(3);
            columns.Add(reader.GetString(4));
        }
        return new(exists, unique, columns, coversAllRows, usable);
    }

    protected override async Task AddIdentityColumnsAsync(
        IReadOnlySet<string> existingColumns,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!existingColumns.Contains("id_comparison_key"))
            await ExecuteAsync("ALTER TABLE groundwork_documents ADD id_comparison_key VARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NULL;", transaction, cancellationToken);
        if (!existingColumns.Contains("id_lookup_key"))
            await ExecuteAsync("ALTER TABLE groundwork_documents ADD id_lookup_key VARCHAR(64) COLLATE Latin1_General_100_BIN2 NULL;", transaction, cancellationToken);
    }

    protected override async Task FinalizeIdentityColumnsAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync("ALTER TABLE groundwork_documents ALTER COLUMN id_comparison_key VARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL;", transaction, cancellationToken);
        await ExecuteAsync("ALTER TABLE groundwork_documents ALTER COLUMN id_lookup_key VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;", transaction, cancellationToken);
    }

    protected override Task EnsureIdentityLookupIndexAsync(DbTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_groundwork_documents_identity_lookup' AND object_id = OBJECT_ID(N'groundwork_documents'))
            BEGIN
                CREATE UNIQUE INDEX ux_groundwork_documents_identity_lookup
                ON groundwork_documents(document_kind_key, storage_scope_key, id_lookup_key);
            END;
            """,
            transaction,
            cancellationToken);

    protected override IReadOnlyList<string> RequiredIdentityLookupIndexColumns { get; } =
        ["document_kind_key", "storage_scope_key", "id_lookup_key"];

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
                id_comparison_key VARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
                id_lookup_key VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
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

            CREATE UNIQUE INDEX ux_groundwork_documents_identity_lookup
            ON groundwork_documents(document_kind_key, storage_scope_key, id_lookup_key);
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

    private static readonly string IdentitySchemaSql = $"""
        IF OBJECT_ID(N'groundwork_document_identity_schema', N'U') IS NULL
        BEGIN
            CREATE TABLE groundwork_document_identity_schema (
                storage_unit NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NOT NULL,
                {RequiredHashKey("storage_unit")},
                state_json NVARCHAR(MAX) NOT NULL,
                CONSTRAINT pk_groundwork_document_identity_schema PRIMARY KEY (storage_unit_key)
            );
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
