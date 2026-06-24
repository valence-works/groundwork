using System.Data.Common;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Relational.Materialization;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Materialization;

public sealed class SqliteGroundworkMaterializer(SqliteConnection connection) : RelationalMaterializerBase(connection)
{
    protected override IReadOnlyList<string> SchemaStatements { get; } = [DocumentTableSql, IndexTableSql, SchemaHistorySql];

    protected override string InsertSchemaHistorySql => """
        INSERT OR IGNORE INTO groundwork_schema_history
        (manifest_id, manifest_version, provider_name, provider_version, applied_utc)
        VALUES (@manifestId, @manifestVersion, @providerName, @providerVersion, @appliedUtc);
        """;

    protected override IReadOnlyList<string> CreateOptimizedProjectionStatements(StorageUnit unit, IReadOnlyList<PhysicalizedFieldPlan> fields)
    {
        var table = RelationalPhysicalizationNames.TableName(unit);
        var columns = string.Join(",\n    ", fields.Select(field => $"{RelationalPhysicalizationNames.ColumnName(field)} TEXT NULL"));
        var statements = new List<string>
        {
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
                document_kind TEXT NOT NULL,
                document_id TEXT NOT NULL,
                document_version INTEGER NOT NULL{(fields.Count == 0 ? "" : $",\n    {columns}")},
                PRIMARY KEY (document_kind, document_id),
                FOREIGN KEY (document_kind, document_id)
                    REFERENCES groundwork_documents(document_kind, id)
                    ON DELETE CASCADE
            );
            """
        };

        statements.AddRange(fields.Select(field =>
        {
            var column = RelationalPhysicalizationNames.ColumnName(field);
            var indexName = RelationalPhysicalizationNames.IndexName(unit, field, field.IsUnique);
            var unique = field.IsUnique ? "UNIQUE " : "";
            return $"""
                CREATE {unique}INDEX IF NOT EXISTS {indexName}
                ON {table}({column})
                WHERE {column} IS NOT NULL;
                """;
        }));

        return statements;
    }

    protected override async Task MaterializeOptimizedProjectionAsync(
        StorageUnit unit,
        IReadOnlyList<PhysicalizedFieldPlan> fields,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var table = RelationalPhysicalizationNames.TableName(unit);
        var columns = string.Join(",\n    ", fields.Select(field => $"{RelationalPhysicalizationNames.ColumnName(field)} TEXT NULL"));
        await ExecuteAsync(
            $"""
            CREATE TABLE IF NOT EXISTS {table} (
                document_kind TEXT NOT NULL,
                document_id TEXT NOT NULL,
                document_version INTEGER NOT NULL{(fields.Count == 0 ? "" : $",\n    {columns}")},
                PRIMARY KEY (document_kind, document_id),
                FOREIGN KEY (document_kind, document_id)
                    REFERENCES groundwork_documents(document_kind, id)
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
            var indexName = RelationalPhysicalizationNames.IndexName(unit, field, field.IsUnique);
            var unique = field.IsUnique ? "UNIQUE " : "";
            await ExecuteAsync(
                $"""
                CREATE {unique}INDEX IF NOT EXISTS {indexName}
                ON {table}({column})
                WHERE {column} IS NOT NULL;
                """,
                transaction,
                cancellationToken);
        }

        await BackfillPhysicalizedAsync(unit, fields, table, transaction, cancellationToken);
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
        StorageUnit unit,
        IReadOnlyList<PhysicalizedFieldPlan> fields,
        string table,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var documents = await LoadDocumentsAsync(unit, transaction, cancellationToken);
        var columnNames = fields.Select(RelationalPhysicalizationNames.ColumnName).ToList();
        foreach (var document in documents)
        {
            await DeletePhysicalizedAsync(table, unit.Identity.Value, document.Id, transaction, cancellationToken);
            await InsertPhysicalizedAsync(table, columnNames, fields, unit.Identity.Value, document, transaction, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<(string Id, long Version, string ContentJson)>> LoadDocumentsAsync(
        StorageUnit unit,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, version, content_json
            FROM groundwork_documents
            WHERE document_kind = @kind;
            """,
            transaction);
        AddParameter(command, "kind", unit.Identity.Value);

        var documents = new List<(string Id, long Version, string ContentJson)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            documents.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));

        return documents;
    }

    private async Task DeletePhysicalizedAsync(
        string table,
        string documentKind,
        string documentId,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            $"""
            DELETE FROM {table}
            WHERE document_kind = @kind AND document_id = @id;
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertPhysicalizedAsync(
        string table,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<PhysicalizedFieldPlan> fields,
        string documentKind,
        (string Id, long Version, string ContentJson) document,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", columnNames);
        var parameters = string.Join(", ", columnNames.Select((_, index) => $"@physicalized{index}"));
        await using var command = CreateCommand(
            $"""
            INSERT INTO {table}
            (document_kind, document_id, document_version{(columnNames.Count == 0 ? "" : $", {columns}")})
            VALUES (@kind, @id, @version{(columnNames.Count == 0 ? "" : $", {parameters}")});
            """,
            transaction);
        AddParameter(command, "kind", documentKind);
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
            id TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            version INTEGER NOT NULL,
            content_json TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (document_kind, id)
        );
        """;

    private const string IndexTableSql = """
        CREATE TABLE IF NOT EXISTS groundwork_document_indexes (
            document_kind TEXT NOT NULL,
            index_name TEXT NOT NULL,
            index_value TEXT NOT NULL,
            document_id TEXT NOT NULL,
            is_unique INTEGER NOT NULL,
            PRIMARY KEY (document_kind, index_name, index_value, document_id),
            FOREIGN KEY (document_kind, document_id)
                REFERENCES groundwork_documents(document_kind, id)
                ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_groundwork_document_indexes_unique
        ON groundwork_document_indexes(document_kind, index_name, index_value)
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
