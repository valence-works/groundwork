using System.Data.Common;
using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using Groundwork.Relational.Materialization;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Materialization;

public sealed class SqliteGroundworkMaterializer(SqliteConnection connection) : RelationalMaterializerBase(connection)
{
    protected override IReadOnlyList<string> SchemaStatements { get; } = [DocumentTableSql, IndexTableSql, IdentitySchemaSql, SchemaHistorySql];

    protected override string InsertSchemaHistorySql => """
        INSERT OR IGNORE INTO groundwork_schema_history
        (manifest_id, manifest_version, provider_name, provider_version, applied_utc)
        VALUES (@manifestId, @manifestVersion, @providerName, @providerVersion, @appliedUtc);
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
                document_version INTEGER NOT NULL{(fields.Count == 0 ? "" : $",\n    {columns}")},
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

    protected override async Task MaterializeOptimizedProjectionAsync(
        MaterializedProjection projection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var table = RelationalPhysicalizationNames.TableName(projection.UnitIdentity);
        var fields = projection.Fields;
        var columns = string.Join(",\n    ", fields.Select(field => $"{RelationalPhysicalizationNames.ColumnName(field)} TEXT NULL"));
        await ExecuteAsync(
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
                document_kind TEXT NOT NULL,
                storage_scope TEXT NOT NULL,
                document_id TEXT NOT NULL,
                document_version INTEGER NOT NULL{(fields.Count == 0 ? "" : $",\n    {columns}")},
                PRIMARY KEY (document_kind, storage_scope, document_id),
                FOREIGN KEY (document_kind, storage_scope, document_id)
                    REFERENCES groundwork_documents(document_kind, storage_scope, id)
                    ON DELETE CASCADE
            );
            """,
            transaction,
            cancellationToken);

        var existingColumns = await ReadColumnsAsync(table, transaction, cancellationToken);
        foreach (var field in fields)
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            if (!existingColumns.Contains(column))
                await ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;", transaction, cancellationToken);
        }

        foreach (var field in fields)
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            var indexName = RelationalPhysicalizationNames.IndexName(projection.UnitIdentity, field, field.IsUnique);
            var unique = field.IsUnique ? "UNIQUE " : "";
            await ExecuteAsync(
                $"""
                CREATE {unique}INDEX IF NOT EXISTS {indexName}
                ON {table}(storage_scope, {column})
                WHERE {column} IS NOT NULL;
                """,
                transaction,
                cancellationToken);
        }

        await BackfillPhysicalizedAsync(projection.UnitIdentity, fields, table, transaction, cancellationToken);
    }

    private async Task<HashSet<string>> ReadColumnsAsync(
        string table,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand($"PRAGMA table_info({table});", transaction);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));

        return columns;
    }

    private async Task BackfillPhysicalizedAsync(
        string unitIdentity,
        IReadOnlyList<PhysicalizedFieldPlan> fields,
        string table,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var documents = await LoadDocumentsAsync(unitIdentity, transaction, cancellationToken);
        var columnNames = fields.Select(RelationalPhysicalizationNames.ColumnName).ToList();
        foreach (var document in documents)
        {
            await DeletePhysicalizedAsync(table, unitIdentity, document.StorageScope, document.Id, transaction, cancellationToken);
            await InsertPhysicalizedAsync(table, columnNames, fields, unitIdentity, document, transaction, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<(string StorageScope, string Id, long Version, string ContentJson)>> LoadDocumentsAsync(
        string unitIdentity,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT storage_scope, id, version, content_json
            FROM groundwork_documents
            WHERE document_kind = @kind;
            """,
            transaction);
        AddParameter(command, "kind", unitIdentity);

        var documents = new List<(string StorageScope, string Id, long Version, string ContentJson)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            documents.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));

        return documents;
    }

    private async Task DeletePhysicalizedAsync(
        string table,
        string documentKind,
        string storageScope,
        string documentId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            DELETE FROM {table}
            WHERE document_kind = @kind AND storage_scope = @scope AND document_id = @id;
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "scope", storageScope);
        AddParameter(command, "id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertPhysicalizedAsync(
        string table,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<PhysicalizedFieldPlan> fields,
        string documentKind,
        (string StorageScope, string Id, long Version, string ContentJson) document,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", columnNames);
        var parameters = string.Join(", ", columnNames.Select((_, index) => $"@physicalized{index}"));
        await using var command = CreateCommand(
            $"""
            INSERT INTO {table}
            (document_kind, storage_scope, document_id, document_version{(columnNames.Count == 0 ? "" : $", {columns}")})
            VALUES (@kind, @scope, @id, @version{(columnNames.Count == 0 ? "" : $", {parameters}")});
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "scope", document.StorageScope);
        AddParameter(command, "id", document.Id);
        AddParameter(command, "version", document.Version);

        for (var index = 0; index < fields.Count; index++)
        {
            var value = RelationalPhysicalizationValues.TryRead(document.ContentJson, fields[index].Path, out var physicalizedValue)
                ? physicalizedValue
                : null;
            AddParameter(command, $"physicalized{index}", value is null ? DBNull.Value : value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string DocumentTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_documents (
            document_kind TEXT NOT NULL,
            storage_scope TEXT NOT NULL,
            id TEXT NOT NULL,
            id_comparison_key TEXT NOT NULL,
            id_lookup_key TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            version INTEGER NOT NULL,
            content_json TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (document_kind, storage_scope, id_lookup_key),
            UNIQUE (document_kind, storage_scope, id)
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

    private const string IdentitySchemaSql = """
        CREATE TABLE IF NOT EXISTS groundwork_document_identity_schema (
            document_kind TEXT NOT NULL PRIMARY KEY,
            string_case_policy TEXT NOT NULL,
            comparison_algorithm TEXT NOT NULL,
            lookup_algorithm TEXT NOT NULL
        );
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
