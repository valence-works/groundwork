using System.Data.Common;
using System.Globalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Provider.Relational;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.Relational.Physicalization;
using Npgsql;

namespace Groundwork.PostgreSql.PhysicalStorage;

/// <summary>PostgreSQL physical-schema executor using pooled operation sessions and advisory locks.</summary>
public sealed class PostgreSqlPhysicalSchemaExecutor : RelationalServerPhysicalSchemaExecutor
{
    public PostgreSqlPhysicalSchemaExecutor(string connectionString)
        : this(connectionString, null, null)
    {
    }

    internal PostgreSqlPhysicalSchemaExecutor(
        string connectionString,
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence,
        Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence)
        : base(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            () => new NpgsqlConnection(LockConnectionString(connectionString)),
            new PostgreSqlPhysicalSchemaDialect(),
            beforeOperationEvidence,
            beforeAppliedStateFence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal PostgreSqlPhysicalSchemaExecutor(
        RelationalSessionFactory sessions,
        Func<DbConnection> createLockConnection)
        : base(sessions, createLockConnection, new PostgreSqlPhysicalSchemaDialect())
    {
    }

    internal static long LockSessionId(IPhysicalSchemaApplicationLock applicationLock) =>
        ReadLockSessionId(applicationLock);

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

    public override string? EnvelopeCollation(RelationalEnvelopeColumnKind kind) => kind switch
    {
        RelationalEnvelopeColumnKind.DocumentKind or RelationalEnvelopeColumnKind.StorageScope or
            RelationalEnvelopeColumnKind.Id or RelationalEnvelopeColumnKind.SchemaVersion or
            RelationalEnvelopeColumnKind.Timestamp => "C",
        _ => null
    };

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
        null or "" => "C",
        var value when value.Equals("ordinal", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("binary", StringComparison.OrdinalIgnoreCase) => "C",
        var value => value
    };

    public override string? NormalizeCollationIdentity(string? collation)
    {
        if (collation is null || string.Equals(collation, "C", StringComparison.Ordinal))
            return collation;
        var separator = collation.IndexOf(':');
        if (separator < 0)
            return collation;
        var schema = collation[..separator];
        var name = collation[(separator + 1)..];
        return string.Equals(name, "C", StringComparison.Ordinal)
            ? string.Equals(schema, "pg_catalog", StringComparison.Ordinal) ? "C" : collation
            : name;
    }

    public override string? NormalizeDefault(ProjectedColumnDefinition definition) =>
        definition.DefaultValue is null ? null : DefaultSql(definition);

    public override string EnvelopeColumn(string name, RelationalEnvelopeColumnKind kind) =>
        $"{Q(name)} {EnvelopeType(kind)}" +
        (EnvelopeCollation(kind) is { } collation ? $" COLLATE {CollationToken(collation)}" : string.Empty) +
        " NOT NULL";

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

    public override async Task<bool> VerifyApplicationLockAsync(
        DbConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_locks
                WHERE locktype = 'advisory'
                  AND pid = pg_catalog.pg_backend_pid()
                  AND granted
                  AND objsubid = 1
                  AND classid::bigint = ((pg_catalog.hashtextextended(@resource, 0) >> 32) & 4294967295)
                  AND objid::bigint = (pg_catalog.hashtextextended(@resource, 0) & 4294967295)
            );
            """;
        Add(command, "resource", resource);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public override async Task<long> ReadServerSessionIdAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_backend_pid();";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public override async Task<long> AcquireFenceAsync(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO groundwork_physical_schema_locks
                (manifest_id, provider_name, owner_id, fence)
            VALUES (@manifestId, @providerName, @owner, 1)
            ON CONFLICT (manifest_id, provider_name) DO UPDATE
            SET owner_id = EXCLUDED.owner_id,
                fence = groundwork_physical_schema_locks.fence + 1
            WHERE groundwork_physical_schema_locks.fence < 9223372036854775807
            RETURNING fence;
            """;
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "owner", owner);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException($"PostgreSQL physical-schema fence is exhausted for target '{target}'.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public override async Task AssertFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        string owner,
        long fence,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT 1
            FROM groundwork_physical_schema_locks
            WHERE manifest_id = @manifestId AND provider_name = @providerName
              AND owner_id = @owner AND fence = @fence
            FOR UPDATE;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "owner", owner);
        Add(command, "fence", fence);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException($"PostgreSQL physical-schema fence {fence} is no longer owned for target '{target}'.");
    }

    public override async Task EnsureInfrastructureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_locks (
                manifest_id text COLLATE pg_catalog."C" NOT NULL,
                provider_name text COLLATE pg_catalog."C" NOT NULL,
                owner_id text COLLATE pg_catalog."C" NOT NULL,
                fence bigint NOT NULL,
                PRIMARY KEY (manifest_id, provider_name)
            );
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_operations (
                manifest_id text COLLATE pg_catalog."C" NOT NULL,
                provider_name text COLLATE pg_catalog."C" NOT NULL,
                operation_id text COLLATE pg_catalog."C" NOT NULL,
                operation_fingerprint text COLLATE pg_catalog."C" NOT NULL,
                applied_utc timestamp with time zone NOT NULL,
                PRIMARY KEY (manifest_id, provider_name, operation_id)
            );
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_state (
                manifest_id text COLLATE pg_catalog."C" NOT NULL,
                provider_name text COLLATE pg_catalog."C" NOT NULL,
                target_fingerprint text COLLATE pg_catalog."C" NOT NULL,
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
                   CASE WHEN a.attcollation = typ.typcollation THEN NULL ELSE coll_ns.nspname || ':' || coll.collname END,
                   COALESCE(pk.ordinality, 0)::integer
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            JOIN pg_catalog.pg_type typ ON typ.oid = a.atttypid
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation AND a.attcollation <> 0
            LEFT JOIN pg_catalog.pg_namespace coll_ns ON coll_ns.oid = coll.collnamespace
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
        ProjectedCollation(definition) is { } value ? $" COLLATE {CollationToken(value)}" : string.Empty;

    private string CollationToken(string value) =>
        string.Equals(value, "C", StringComparison.Ordinal)
            ? $"{Q("pg_catalog")}.{Q("C")}"
            : Q(value);

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
