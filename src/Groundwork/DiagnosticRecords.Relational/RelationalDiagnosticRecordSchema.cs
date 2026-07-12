namespace Groundwork.DiagnosticRecords.Relational;

public enum RelationalDiagnosticColumnType
{
    Text,
    Int64
}

public sealed record RelationalDiagnosticColumnDefinition(
    string Name,
    RelationalDiagnosticColumnType Type,
    bool IsNullable = false,
    bool UsesBinaryTextSemantics = false);

public sealed record RelationalDiagnosticTableDefinition(
    string Name,
    IReadOnlyList<RelationalDiagnosticColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey);

public sealed record RelationalDiagnosticIndexDefinition(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    bool IsUnique = false);

/// <summary>
/// Provider-neutral physical model consumed by relational diagnostic-record materializers.
/// Providers translate these portable text/integer and binary-text requirements to native DDL.
/// </summary>
public sealed record RelationalDiagnosticRecordSchema(
    IReadOnlyList<RelationalDiagnosticTableDefinition> Tables,
    IReadOnlyList<RelationalDiagnosticIndexDefinition> Indexes)
{
    public const string ProviderStateTable = "groundwork_diagnostic_provider_state";
    public const string StreamsTable = "groundwork_diagnostic_streams";
    public const string RecordsTable = "groundwork_diagnostic_records";
    public const string FieldsTable = "groundwork_diagnostic_fields";
    public const string AppendOperationsTable = "groundwork_diagnostic_append_operations";
    public const string TrimOperationsTable = "groundwork_diagnostic_trim_operations";

    public static RelationalDiagnosticRecordSchema Standard { get; } = CreateStandard();

    private static RelationalDiagnosticRecordSchema CreateStandard()
    {
        static RelationalDiagnosticColumnDefinition Text(string name, bool nullable = false, bool binary = true) =>
            new(name, RelationalDiagnosticColumnType.Text, nullable, binary);
        static RelationalDiagnosticColumnDefinition Int64(string name, bool nullable = false) =>
            new(name, RelationalDiagnosticColumnType.Int64, nullable);
        var scope = new[] { Text("tenant_id"), Text("scope_id"), Text("stream_id") };
        var operation = scope.Concat([Int64("issued_at_ticks"), Text("nonce")]).ToArray();

        return new(
            [
                new(ProviderStateTable, [Int64("id"), Int64("clock_high_water_ticks")], ["id"]),
                new(StreamsTable,
                    scope.Concat([
                        Int64("next_cursor"), Int64("logical_high_water_type", true),
                        Text("logical_high_water_value", true)
                    ]).ToArray(),
                    ["tenant_id", "scope_id", "stream_id"]),
                new(RecordsTable,
                    scope.Concat([
                        Int64("cursor"), Text("record_id"), Int64("occurred_at_ticks"), Text("payload_json")
                    ]).ToArray(),
                    ["tenant_id", "scope_id", "stream_id", "cursor"]),
                new(FieldsTable,
                    scope.Concat([
                        Int64("cursor"), Text("field_name"), Int64("value_ordinal"), Int64("field_type"),
                        Text("canonical_value"), Text("comparison_key")
                    ]).ToArray(),
                    ["tenant_id", "scope_id", "stream_id", "cursor", "field_name", "value_ordinal"]),
                new(AppendOperationsTable,
                    operation.Concat([
                        Text("fingerprint", true), Int64("committed_at_ticks"), Int64("outcome_expires_at_ticks"),
                        Int64("tombstone_until_ticks"), Text("result_json", true), Int64("is_tombstone")
                    ]).ToArray(),
                    ["tenant_id", "scope_id", "stream_id", "issued_at_ticks", "nonce"]),
                new(TrimOperationsTable,
                    operation.Concat([
                        Text("fingerprint", true), Int64("committed_at_ticks"), Int64("outcome_expires_at_ticks"),
                        Int64("tombstone_until_ticks"), Text("result_json", true), Int64("is_tombstone")
                    ]).ToArray(),
                    ["tenant_id", "scope_id", "stream_id", "issued_at_ticks", "nonce"])
            ],
            [
                new("ux_groundwork_diagnostic_records_scope_id", RecordsTable,
                    ["tenant_id", "scope_id", "stream_id", "record_id"], true),
                new("ix_groundwork_diagnostic_records_scope_cursor", RecordsTable,
                    ["tenant_id", "scope_id", "stream_id", "cursor"]),
                new("ix_groundwork_diagnostic_fields_scope_value", FieldsTable,
                    ["tenant_id", "scope_id", "stream_id", "field_name", "comparison_key", "field_type", "cursor"]),
                new("ix_groundwork_diagnostic_fields_scope_latest", FieldsTable,
                    ["tenant_id", "scope_id", "stream_id", "field_name", "field_type", "value_ordinal", "comparison_key", "cursor"]),
                new("ix_groundwork_diagnostic_append_tombstone", AppendOperationsTable, ["tombstone_until_ticks"]),
                new("ix_groundwork_diagnostic_trim_tombstone", TrimOperationsTable, ["tombstone_until_ticks"])
            ]);
    }
}

public static class RelationalDiagnosticRecordSchemaValidator
{
    public static void ValidateAndThrow(RelationalDiagnosticRecordSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(schema.Tables);
        ArgumentNullException.ThrowIfNull(schema.Indexes);
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in schema.Tables)
        {
            ArgumentNullException.ThrowIfNull(table);
            ValidateIdentifier(table.Name, "table");
            if (!tableNames.Add(table.Name))
                throw new ArgumentException($"Relational diagnostic table '{table.Name}' is declared more than once.", nameof(schema));
            if (table.Columns is null || table.Columns.Count == 0)
                throw new ArgumentException($"Relational diagnostic table '{table.Name}' must declare columns.", nameof(schema));
            if (table.PrimaryKey is null || table.PrimaryKey.Count == 0)
                throw new ArgumentException($"Relational diagnostic table '{table.Name}' must declare a primary key.", nameof(schema));
            var columnNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                ArgumentNullException.ThrowIfNull(column);
                ValidateIdentifier(column.Name, "column");
                if (!columnNames.Add(column.Name))
                    throw new ArgumentException($"Column '{column.Name}' is declared more than once on table '{table.Name}'.", nameof(schema));
            }
            foreach (var primaryKeyColumn in table.PrimaryKey)
            {
                ValidateIdentifier(primaryKeyColumn, "primary-key column");
                if (!columnNames.Contains(primaryKeyColumn))
                    throw new ArgumentException($"Primary-key column '{primaryKeyColumn}' is not declared on table '{table.Name}'.", nameof(schema));
            }
        }

        if (!tableNames.Contains(RelationalDiagnosticRecordSchema.ProviderStateTable))
            throw new ArgumentException($"The portable schema must declare '{RelationalDiagnosticRecordSchema.ProviderStateTable}'.", nameof(schema));

        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in schema.Indexes)
        {
            ArgumentNullException.ThrowIfNull(index);
            ValidateIdentifier(index.Name, "index");
            ValidateIdentifier(index.Table, "index table");
            if (!indexNames.Add(index.Name))
                throw new ArgumentException($"Relational diagnostic index '{index.Name}' is declared more than once.", nameof(schema));
            var table = schema.Tables.SingleOrDefault(table => StringComparer.Ordinal.Equals(table.Name, index.Table))
                ?? throw new ArgumentException($"Index '{index.Name}' targets undeclared table '{index.Table}'.", nameof(schema));
            if (index.Columns is null || index.Columns.Count == 0)
                throw new ArgumentException($"Index '{index.Name}' must declare columns.", nameof(schema));
            var columns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var column in index.Columns)
            {
                ValidateIdentifier(column, "index column");
                if (!columns.Contains(column))
                    throw new ArgumentException($"Index column '{column}' is not declared on table '{table.Name}'.", nameof(schema));
            }
        }
    }

    private static void ValidateIdentifier(string identifier, string kind)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            identifier[0] is not ('_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z') ||
            identifier.Skip(1).Any(character => character is not ('_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')))
        {
            throw new ArgumentException($"Relational diagnostic {kind} identifier '{identifier}' is not portable.", nameof(identifier));
        }
    }
}
