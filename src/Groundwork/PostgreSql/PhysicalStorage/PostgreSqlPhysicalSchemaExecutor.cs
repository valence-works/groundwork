using System.Data.Common;
using System.Globalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Provider.Relational;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.Relational.Physicalization;
using Npgsql;

namespace Groundwork.PostgreSql.PhysicalStorage;

/// <summary>PostgreSQL physical-schema executor using pooled operation sessions and advisory locks.</summary>
public sealed class PostgreSqlPhysicalSchemaExecutor : RelationalServerPhysicalSchemaExecutor
{
    public PostgreSqlPhysicalSchemaExecutor(string connectionString)
        : base(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            () => new NpgsqlConnection(LockConnectionString(connectionString)),
            new PostgreSqlPhysicalSchemaDialect())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal PostgreSqlPhysicalSchemaExecutor(
        RelationalSessionFactory sessions,
        Func<DbConnection> createLockConnection)
        : base(sessions, createLockConnection, new PostgreSqlPhysicalSchemaDialect())
    {
    }

    private static string LockConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}

internal sealed class PostgreSqlPhysicalSchemaDialect : RelationalServerPhysicalSchemaDialect
{
    public override string ProviderDisplayName => "PostgreSQL";
    public override string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public override string EnvelopeType(RelationalEnvelopeColumnKind kind) => kind switch
    {
        RelationalEnvelopeColumnKind.DocumentKind or RelationalEnvelopeColumnKind.StorageScope or
            RelationalEnvelopeColumnKind.Id or RelationalEnvelopeColumnKind.SchemaVersion or
            RelationalEnvelopeColumnKind.CanonicalJson or RelationalEnvelopeColumnKind.Timestamp => "text",
        RelationalEnvelopeColumnKind.Version => "bigint",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public override string? EnvelopeCollation(RelationalEnvelopeColumnKind kind) => null;

    public override string ProjectedType(ProjectedColumnDefinition definition) => definition.Type switch
    {
        PortablePhysicalType.String => definition.Length is { } length ? $"character varying({length})" : "text",
        PortablePhysicalType.Int32 => "integer",
        PortablePhysicalType.Int64 => "bigint",
        PortablePhysicalType.Decimal => $"numeric({definition.Precision},{definition.Scale})",
        PortablePhysicalType.Boolean => "boolean",
        // PostgreSQL timestamps are microsecond precision; UTC ticks preserve Groundwork's 100ns contract.
        PortablePhysicalType.DateTime => "bigint",
        PortablePhysicalType.Guid => "uuid",
        PortablePhysicalType.Binary => "bytea",
        PortablePhysicalType.Json => "text",
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
    };

    public override string? Collation(string? portableCollation) => portableCollation?.Trim() switch
    {
        null or "" => null,
        var value when value.Equals("ordinal", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("binary", StringComparison.OrdinalIgnoreCase) => "C",
        var value => value
    };

    public override string? NormalizeDefault(ProjectedColumnDefinition definition) =>
        definition.DefaultValue is null ? null : DefaultSql(definition);

    public override string EnvelopeColumn(string name, RelationalEnvelopeColumnKind kind) =>
        $"{Q(name)} {EnvelopeType(kind)} NOT NULL";

    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) =>
        $"CREATE TABLE {Q(table)} ({string.Join(", ", columns)}, PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))}));";

    public override string AddColumnSql(string table, string column, ProjectedColumnDefinition definition) =>
        $"ALTER TABLE {Q(table)} ADD COLUMN {ProjectedColumn(column, definition)};";

    public override string FinalizeColumnSql(string table, string column, ProjectedColumnDefinition definition) =>
        $"ALTER TABLE {Q(table)} ALTER COLUMN {Q(column)} SET NOT NULL;";

    public override string? IndexFilter(ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns) => null;

    public override string CreateIndexSql(string table, ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns) =>
        $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {Q(index.Name.Identifier)} ON {Q(table)} " +
        $"({string.Join(", ", index.Columns.Select(column => $"{Q(column.Column.Identifier)} {(column.Direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}"))});";

    public override string UpsertLinkedSql(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> updateColumns)
    {
        var conflict = updateColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(column => $"{Q(column)} = EXCLUDED.{Q(column)}"));
        return $"INSERT INTO {Q(table)} ({string.Join(", ", columns.Select(Q))}) VALUES ({string.Join(", ", columns.Select((_, index) => $"@v{index}"))}) " +
               $"ON CONFLICT ({string.Join(", ", keyColumns.Select(Q))}) {conflict};";
    }

    public override string SelectCanonicalBatchSql(ExecutableStorageRoute route, int batchSize, bool hasCursor)
    {
        var cursor = hasCursor
            ? $" AND ({Q(route.ScopeKey.Column.Identifier)} > @afterScope OR ({Q(route.ScopeKey.Column.Identifier)} = @afterScope AND {Q(route.Envelope.Id.Identifier)} > @afterId))"
            : string.Empty;
        return $"SELECT {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)}, {Q(route.Envelope.CanonicalJson.Identifier)} " +
               $"FROM {Q(route.PrimaryStorage.Name.Identifier)} WHERE {Q(route.Discriminator.Column.Identifier)} = @kind{cursor} " +
               $"ORDER BY {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)} LIMIT {batchSize};";
    }

    public override object? ConvertStorageValue(object? value, ProjectedColumnDefinition definition) => value switch
    {
        DateTimeOffset dateTime => dateTime.UtcDateTime.Ticks,
        _ => value
    };

    public override void Validate(ProjectedColumnDefinition definition)
    {
        if (definition.Type == PortablePhysicalType.Decimal &&
            (definition.Precision is null or < 1 or > 28 || definition.Scale is null or < 0 || definition.Scale > definition.Precision))
            throw new InvalidOperationException($"PostgreSQL Decimal projection '{definition.LogicalName}' requires portable precision 1-28 and scale 0-precision.");
        if (definition.Length is <= 0)
            throw new InvalidOperationException($"PostgreSQL projection '{definition.LogicalName}' length must be positive.");
        if (definition.Collation is not null && definition.Type is not (PortablePhysicalType.String or PortablePhysicalType.Json))
            throw new InvalidOperationException($"PostgreSQL projection '{definition.LogicalName}' can declare collation only for String or Json values.");
        _ = ProjectedCollation(definition);
        if (definition.DefaultValue is not null)
            _ = RelationalPhysicalProjectionValues.ConvertScalar(definition.DefaultValue, definition);
    }

    public override async Task AcquireApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@resource, 0));";
        Add(command, "resource", resource);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async Task ReleaseApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@resource, 0));";
        Add(command, "resource", resource);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async Task EnsureInfrastructureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_operations (
                manifest_id text NOT NULL,
                provider_name text NOT NULL,
                operation_id text NOT NULL,
                operation_fingerprint text NOT NULL,
                applied_utc timestamp with time zone NOT NULL,
                PRIMARY KEY (manifest_id, provider_name, operation_id)
            );
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_state (
                manifest_id text NOT NULL,
                provider_name text NOT NULL,
                target_fingerprint text NOT NULL,
                applied_state_json text NOT NULL,
                PRIMARY KEY (manifest_id, provider_name)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async Task<bool> TableExistsAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relkind = 'r' AND c.relname = @table;");
        Add(command, "table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public override async Task<IReadOnlyDictionary<string, RelationalPhysicalColumnMetadata>> ReadColumnsAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull,
                   pg_get_expr(ad.adbin, ad.adrelid),
                   CASE WHEN a.attcollation = typ.typcollation THEN NULL ELSE coll.collname END,
                   COALESCE(pk.ordinality, 0)::integer
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            JOIN pg_catalog.pg_type typ ON typ.oid = a.atttypid
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation AND a.attcollation <> 0
            LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = c.oid AND con.contype = 'p'
            LEFT JOIN LATERAL unnest(con.conkey) WITH ORDINALITY pk(attnum, ordinality) ON pk.attnum = a.attnum
            WHERE n.nspname = current_schema() AND c.relname = @table
            ORDER BY a.attnum;
            """);
        Add(command, "table", table);
        var result = new Dictionary<string, RelationalPhysicalColumnMetadata>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            result.Add(name, new RelationalPhysicalColumnMetadata(
                name,
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : NormalizeDatabaseDefault(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }
        return result;
    }

    public override async Task<RelationalPhysicalIndexMetadata?> ReadIndexAsync(
        DbConnection connection, DbTransaction transaction, string table, string index, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT i.indisunique, a.attname,
                   pg_index_column_has_property(ix.oid, key.ordinality::integer, 'desc'),
                   pg_get_expr(i.indpred, i.indrelid)
            FROM pg_catalog.pg_class t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_catalog.pg_index i ON i.indrelid = t.oid
            JOIN pg_catalog.pg_class ix ON ix.oid = i.indexrelid
            JOIN LATERAL unnest(i.indkey) WITH ORDINALITY key(attnum, ordinality) ON key.ordinality <= i.indnkeyatts
            JOIN pg_catalog.pg_attribute a ON a.attrelid = t.oid AND a.attnum = key.attnum
            WHERE n.nspname = current_schema() AND t.relname = @table AND ix.relname = @index
            ORDER BY key.ordinality;
            """);
        Add(command, "table", table);
        Add(command, "index", index);
        bool? unique = null;
        string? filter = null;
        var columns = new List<RelationalPhysicalIndexColumnMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            unique ??= reader.GetBoolean(0);
            filter ??= reader.IsDBNull(3) ? null : reader.GetString(3);
            columns.Add(new RelationalPhysicalIndexColumnMetadata(
                reader.GetString(1),
                reader.GetBoolean(2) ? PhysicalSortDirection.Descending : PhysicalSortDirection.Ascending));
        }
        return unique is null ? null : new RelationalPhysicalIndexMetadata(unique.Value, columns, filter);
    }

    private string ProjectedColumn(string column, ProjectedColumnDefinition definition) =>
        $"{Q(column)} {ProjectedType(definition)}{CollationSql(definition)} {(definition.IsNullable ? "NULL" : "NOT NULL")}" +
        (DefaultSql(definition) is { } value ? $" DEFAULT {value}" : string.Empty);

    private string CollationSql(ProjectedColumnDefinition definition) =>
        ProjectedCollation(definition) is { } value ? $" COLLATE {Q(value)}" : string.Empty;

    private static string? DefaultSql(ProjectedColumnDefinition definition)
    {
        if (definition.DefaultValue is null)
            return null;
        var value = RelationalPhysicalProjectionValues.ConvertScalar(definition.DefaultValue, definition);
        return value switch
        {
            string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            bool boolean => boolean ? "true" : "false",
            byte[] bytes => $"'\\x{Convert.ToHexString(bytes).ToLowerInvariant()}'::bytea",
            DateTimeOffset dateTime => dateTime.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            Guid guid => $"'{guid:D}'::uuid",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported PostgreSQL default value type '{value.GetType().Name}'.")
        };
    }

    private static string NormalizeDatabaseDefault(string value)
    {
        var normalized = value.Trim();
        foreach (var suffix in new[] { "::character varying", "::text" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return normalized[..^suffix.Length];
        }
        return normalized;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
