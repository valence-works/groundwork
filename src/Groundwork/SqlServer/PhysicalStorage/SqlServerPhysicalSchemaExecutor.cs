using System.Data.Common;
using System.Globalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.PhysicalStorage;

/// <summary>SQL Server physical-schema executor using one dedicated session for locking and schema transactions.</summary>
public sealed class SqlServerPhysicalSchemaExecutor : RelationalServerPhysicalSchemaExecutor
{
    public SqlServerPhysicalSchemaExecutor(string connectionString)
        : this(connectionString, new SqlServerPhysicalIdentityHash())
    {
    }

    internal SqlServerPhysicalSchemaExecutor(
        string connectionString,
        SqlServerPhysicalIdentityHash hash)
        : this(connectionString, hash, null, null)
    {
    }

    internal SqlServerPhysicalSchemaExecutor(
        string connectionString,
        SqlServerPhysicalIdentityHash hash,
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence,
        Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence)
        : base(
            () => new SqlConnection(LockConnectionString(connectionString)),
            new SqlServerPhysicalSchemaDialect(hash),
            beforeOperationEvidence,
            beforeAppliedStateFence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal static long LockSessionId(IPhysicalSchemaApplicationLock applicationLock) =>
        ReadLockSessionId(applicationLock);

    private static string LockConnectionString(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}

internal class SqlServerPhysicalSchemaDialect : RelationalServerPhysicalSchemaDialect
{
    private readonly SqlServerPhysicalIdentity identity;

    public SqlServerPhysicalSchemaDialect()
        : this(new SqlServerPhysicalIdentityHash())
    {
    }

    internal SqlServerPhysicalSchemaDialect(SqlServerPhysicalIdentityHash hash) =>
        identity = new SqlServerPhysicalIdentity(hash);

    public override void ValidateRoute(ExecutableStorageRoute route) => identity.ValidateRoute(route);
    public override string ExactIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        identity.ExactPredicate(parts, Q, includeOriginal: true);
    public override string? HashOnlyIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        identity.ExactPredicate(parts, Q, includeOriginal: false);
    public override bool IsUniqueConstraintException(DbException exception) =>
        exception is SqlException { Number: 2601 or 2627 };

    public override string ProviderDisplayName => "SQL Server";
    public override string Q(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    public override string EnvelopeType(RelationalEnvelopeColumnKind kind) => kind switch
    {
        RelationalEnvelopeColumnKind.DocumentKind or RelationalEnvelopeColumnKind.Id => "nvarchar(450)",
        RelationalEnvelopeColumnKind.IdentityComparison => "nvarchar(max)",
        RelationalEnvelopeColumnKind.IdentityLookup => "nvarchar(450)",
        RelationalEnvelopeColumnKind.StorageScope => "nvarchar(128)",
        RelationalEnvelopeColumnKind.SchemaVersion => "nvarchar(100)",
        RelationalEnvelopeColumnKind.Version => "bigint",
        RelationalEnvelopeColumnKind.CanonicalJson => "nvarchar(max)",
        RelationalEnvelopeColumnKind.Timestamp => "nvarchar(40)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public override string? EnvelopeCollation(RelationalEnvelopeColumnKind kind) => kind switch
    {
        RelationalEnvelopeColumnKind.Version => null,
        _ => "Latin1_General_100_BIN2"
    };

    public override string ProjectedType(ProjectedColumnDefinition definition) => definition.Type switch
    {
        PortablePhysicalType.String => definition.Length is { } length ? $"nvarchar({length})" : "nvarchar(max)",
        PortablePhysicalType.Int32 => "int",
        PortablePhysicalType.Int64 => "bigint",
        PortablePhysicalType.Decimal => $"decimal({definition.Precision},{definition.Scale})",
        PortablePhysicalType.Boolean => "bit",
        PortablePhysicalType.DateTime => "datetimeoffset(7)",
        PortablePhysicalType.Guid => "uniqueidentifier",
        PortablePhysicalType.Binary => definition.Length is { } length ? $"varbinary({length})" : "varbinary(max)",
        PortablePhysicalType.Json => "nvarchar(max)",
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
    };

    public override string? Collation(string? portableCollation) => portableCollation?.Trim() switch
    {
        null or "" => "Latin1_General_100_BIN2",
        var value when value.Equals("ordinal", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("binary", StringComparison.OrdinalIgnoreCase) => "Latin1_General_100_BIN2",
        var value when value.Equals("nocase", StringComparison.OrdinalIgnoreCase) => "Latin1_General_100_CI_AS",
        var value => value
    };

    public override string? NormalizeDefault(ProjectedColumnDefinition definition) =>
        definition.DefaultValue is null ? null : DefaultSql(definition);

    public override string? NormalizeComputedDefinition(string? definition)
    {
        if (definition is null)
            return null;
        var normalized = string.Concat(definition.Where(character => !char.IsWhiteSpace(character)))
            .Replace("[binary]", "binary", StringComparison.OrdinalIgnoreCase)
            .Replace("[varbinary]", "varbinary", StringComparison.OrdinalIgnoreCase)
            .Replace("[nvarchar]", "nvarchar", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        while (HasRedundantOuterParentheses(normalized))
            normalized = normalized[1..^1];
        return normalized;
    }

    public override string EnvelopeColumn(string name, RelationalEnvelopeColumnKind kind) =>
        $"{Q(name)} {EnvelopeType(kind)}" +
        (EnvelopeCollation(kind) is { } collation ? $" COLLATE {CollationToken(collation)}" : string.Empty) +
        " NOT NULL";

    public override RelationalPhysicalIdentityLayout IdentityLayout(
        IReadOnlyList<RelationalPhysicalIdentityColumn> identityColumns,
        IReadOnlyList<string> logicalPrimaryKey) =>
        identity.Layout(identityColumns, logicalPrimaryKey, Q);

    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey)
    {
        var constraint = Q(SqlServerPhysicalName.Normalize($"PK_{table}"));
        return $"CREATE TABLE {Q(table)} ({string.Join(", ", columns)}, CONSTRAINT {constraint} PRIMARY KEY NONCLUSTERED ({string.Join(", ", primaryKey.Select(Q))}));";
    }

    public override string AddColumnSql(string table, string column, ProjectedColumnDefinition definition) =>
        $"ALTER TABLE {Q(table)} ADD {ProjectedColumn(column, definition)};";

    public override string FinalizeColumnSql(string table, string column, ProjectedColumnDefinition definition) =>
        $"ALTER TABLE {Q(table)} ALTER COLUMN {Q(column)} {ProjectedType(definition)}{CollationSql(definition)} NOT NULL;";

    public override string? IndexFilter(ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns) =>
        index.IsUnique && nullableColumns.Count > 0
            ? $"({string.Join(" AND ", nullableColumns.Select(column => $"{Q(column)} IS NOT NULL"))})"
            : null;

    public override string CreateIndexSql(string table, ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns) =>
        $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {Q(index.Name.Identifier)} ON {Q(table)} " +
        $"({string.Join(", ", index.Columns.Select(column => $"{Q(column.Column.Identifier)} {(column.Direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}"))})" +
        (IndexFilter(index, nullableColumns) is { } filter ? $" WHERE {filter}" : string.Empty) + ";";

    public override string UpsertLinkedSql(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> updateColumns)
    {
        var parameterByColumn = columns.Select((column, index) => (column, parameter: $"@v{index}"))
            .ToDictionary(item => item.column, item => item.parameter, StringComparer.Ordinal);
        var predicate = identity.ExactPredicate(
            keyColumns.Select(column => new RelationalPhysicalIdentityPredicatePart(
                column,
                null,
                parameterByColumn[column])).ToArray(),
            Q,
            includeOriginal: true);
        var insert = $"INSERT INTO {Q(table)} ({string.Join(", ", columns.Select(Q))}) VALUES ({string.Join(", ", columns.Select((_, index) => $"@v{index}"))});";
        return updateColumns.Count == 0
            ? $"IF NOT EXISTS (SELECT 1 FROM {Q(table)} WITH (UPDLOCK, HOLDLOCK) WHERE {predicate}) {insert}"
            : $"UPDATE {Q(table)} WITH (UPDLOCK, HOLDLOCK) SET {string.Join(", ", updateColumns.Select(column => $"{Q(column)} = {parameterByColumn[column]}"))} WHERE {predicate}; IF @@ROWCOUNT = 0 {insert}";
    }

    public override string SelectCanonicalBatchSql(ExecutableStorageRoute route, int batchSize, bool hasCursor)
    {
        var cursor = hasCursor
            ? $" AND ({Q(route.ScopeKey.Column.Identifier)} > @afterScope OR ({Q(route.ScopeKey.Column.Identifier)} = @afterScope AND {Q(route.Envelope.Id.Identifier)} > @afterId))"
            : string.Empty;
        var kind = identity.ExactPredicate(
            [new(route.Discriminator.Column.Identifier, null, "@kind")],
            Q,
            includeOriginal: true);
        return $"SELECT TOP ({batchSize}) {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)}, {Q(route.Envelope.CanonicalJson.Identifier)} " +
               $"FROM {Q(route.PrimaryStorage.Name.Identifier)} WHERE {kind}{cursor} " +
               $"ORDER BY {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)};";
    }

    public override object? ConvertStorageValue(object? value, ProjectedColumnDefinition definition) => value switch
    {
        DateTimeOffset dateTime => dateTime.ToUniversalTime(),
        _ => value
    };

    public override void Validate(ProjectedColumnDefinition definition)
    {
        if (definition.Type == PortablePhysicalType.Decimal &&
            (definition.Precision is null or < 1 or > 28 || definition.Scale is null or < 0 || definition.Scale > definition.Precision))
            throw new InvalidOperationException($"SQL Server Decimal projection '{definition.LogicalName}' requires portable precision 1-28 and scale 0-precision.");
        if (definition.Type == PortablePhysicalType.String && definition.Length is <= 0 or > 4000)
            throw new InvalidOperationException($"SQL Server String projection '{definition.LogicalName}' length must be 1-4000.");
        if (definition.Type == PortablePhysicalType.Binary && definition.Length is <= 0 or > 8000)
            throw new InvalidOperationException($"SQL Server Binary projection '{definition.LogicalName}' length must be 1-8000.");
        if (definition.Collation is not null && definition.Type is not (PortablePhysicalType.String or PortablePhysicalType.Json))
            throw new InvalidOperationException($"SQL Server projection '{definition.LogicalName}' can declare collation only for String or Json values.");
        _ = ProjectedCollation(definition);
        if (definition.DefaultValue is not null)
            _ = RelationalPhysicalProjectionValues.ConvertScalar(definition.DefaultValue, definition);
    }

    public override async Task AcquireApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1; SELECT @result;";
        Add(command, "resource", resource);
        int result;
        try
        {
            result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("SQL Server physical-schema lock acquisition was canceled.", exception, cancellationToken);
        }
        if (result < 0)
            throw new InvalidOperationException($"SQL Server failed to acquire physical-schema application lock (result {result}).");
    }

    public override async Task ReleaseApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        Add(command, "resource", resource);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async Task<bool> VerifyApplicationLockAsync(
        DbConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Session');";
        Add(command, "resource", resource);
        return string.Equals(
            Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture),
            "Exclusive",
            StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<long> ReadServerSessionIdAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";
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
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @next bigint;
            SELECT @next = fence
            FROM groundwork_physical_schema_locks WITH (UPDLOCK, HOLDLOCK)
            WHERE manifest_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @manifestId)))
              AND provider_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @providerName)))
              AND manifest_id = @manifestId AND provider_name = @providerName;
            IF @next IS NULL
            BEGIN
                SET @next = 1;
                INSERT INTO groundwork_physical_schema_locks
                    (manifest_id, provider_name, owner_id, fence)
                VALUES (@manifestId, @providerName, @owner, @next);
            END
            ELSE
            BEGIN
                IF @next = 9223372036854775807
                BEGIN
                    ROLLBACK TRANSACTION;
                    SELECT CAST(NULL AS bigint);
                    RETURN;
                END;
                SET @next = @next + 1;
                UPDATE groundwork_physical_schema_locks
                SET owner_id = @owner, fence = @next
                WHERE manifest_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @manifestId)))
                  AND provider_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @providerName)))
                  AND manifest_id = @manifestId AND provider_name = @providerName;
            END;
            COMMIT TRANSACTION;
            SELECT @next;
            """;
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "owner", owner);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException($"SQL Server physical-schema fence is exhausted for target '{target}'.");
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
            FROM groundwork_physical_schema_locks WITH (UPDLOCK, HOLDLOCK)
            WHERE manifest_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @manifestId)))
              AND provider_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), @providerName)))
              AND manifest_id = @manifestId AND provider_name = @providerName
              AND owner_id = @owner AND fence = @fence;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "owner", owner);
        Add(command, "fence", fence);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException($"SQL Server physical-schema fence {fence} is no longer owned for target '{target}'.");
    }

    public override async Task EnsureInfrastructureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_locks", """
            CREATE TABLE groundwork_physical_schema_locks (
                manifest_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_name nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                owner_id nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                fence bigint NOT NULL,
                manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), manifest_id))) PERSISTED NOT NULL,
                provider_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), provider_name))) PERSISTED NOT NULL,
                CONSTRAINT PK_groundwork_physical_schema_locks PRIMARY KEY NONCLUSTERED (manifest_key, provider_key)
            );
            """, cancellationToken);
        await EnsureInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_operations", """
            CREATE TABLE groundwork_physical_schema_operations (
                manifest_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_name nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                operation_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                operation_fingerprint nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                applied_utc datetimeoffset(7) NOT NULL,
                manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), manifest_id))) PERSISTED NOT NULL,
                provider_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), provider_name))) PERSISTED NOT NULL,
                operation_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), operation_id))) PERSISTED NOT NULL,
                CONSTRAINT PK_groundwork_physical_schema_operations PRIMARY KEY NONCLUSTERED (manifest_key, provider_key, operation_key)
            );
            """, cancellationToken);
        await EnsureInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_state", """
            CREATE TABLE groundwork_physical_schema_state (
                manifest_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_name nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                target_fingerprint nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                applied_state_json nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), manifest_id))) PERSISTED NOT NULL,
                provider_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), provider_name))) PERSISTED NOT NULL,
                CONSTRAINT PK_groundwork_physical_schema_state PRIMARY KEY NONCLUSTERED (manifest_key, provider_key)
            );
            """, cancellationToken);
        await EnsureInfrastructureTableAsync(connection, transaction, RelationalPhysicalStorageColumns.MutationOperationsTable, """
            CREATE TABLE groundwork_document_mutation_operations (
                manifest_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                provider_name nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                completed_provider_version nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                storage_unit nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                storage_scope nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                operation_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
                request_fingerprint nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                affected_count bigint NOT NULL,
                completed_utc datetimeoffset(7) NOT NULL,
                manifest_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), manifest_id))) PERSISTED NOT NULL,
                provider_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), provider_name))) PERSISTED NOT NULL,
                storage_unit_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), storage_unit))) PERSISTED NOT NULL,
                storage_scope_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), storage_scope))) PERSISTED NOT NULL,
                operation_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), operation_id))) PERSISTED NOT NULL,
                CONSTRAINT PK_groundwork_document_mutation_operations PRIMARY KEY NONCLUSTERED (
                    manifest_key, provider_key, storage_unit_key, storage_scope_key, operation_key)
            );
            """, cancellationToken);

        await ValidateInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_locks",
        [
            new("manifest_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("provider_name", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("owner_id", "nvarchar(32)", false, "Latin1_General_100_BIN2"),
            new("fence", "bigint", false, null),
            HashColumn("manifest_key", "manifest_id", 1),
            HashColumn("provider_key", "provider_name", 2)
        ], cancellationToken);
        await ValidateInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_operations",
        [
            new("manifest_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("provider_name", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("operation_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("operation_fingerprint", "nvarchar(128)", false, "Latin1_General_100_BIN2"),
            new("applied_utc", "datetimeoffset(7)", false, null),
            HashColumn("manifest_key", "manifest_id", 1),
            HashColumn("provider_key", "provider_name", 2),
            HashColumn("operation_key", "operation_id", 3)
        ], cancellationToken);
        await ValidateInfrastructureTableAsync(connection, transaction, "groundwork_physical_schema_state",
        [
            new("manifest_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("provider_name", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("target_fingerprint", "nvarchar(128)", false, "Latin1_General_100_BIN2"),
            new("applied_state_json", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            HashColumn("manifest_key", "manifest_id", 1),
            HashColumn("provider_key", "provider_name", 2)
        ], cancellationToken);
        await ValidateInfrastructureTableAsync(connection, transaction, RelationalPhysicalStorageColumns.MutationOperationsTable,
        [
            new("manifest_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("provider_name", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("completed_provider_version", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("storage_unit", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("storage_scope", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("operation_id", "nvarchar(max)", false, "Latin1_General_100_BIN2"),
            new("request_fingerprint", "nvarchar(128)", false, "Latin1_General_100_BIN2"),
            new("affected_count", "bigint", false, null),
            new("completed_utc", "datetimeoffset(7)", false, null),
            HashColumn("manifest_key", "manifest_id", 1),
            HashColumn("provider_key", "provider_name", 2),
            HashColumn("storage_unit_key", "storage_unit", 3),
            HashColumn("storage_scope_key", "storage_scope", 4),
            HashColumn("operation_key", "operation_id", 5)
        ], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureInfrastructureTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string createSql,
        CancellationToken cancellationToken)
    {
        await using var inspect = Command(connection, transaction,
            "SELECT type FROM sys.objects WHERE object_id = OBJECT_ID(@table);");
        Add(inspect, "table", table);
        var kind = await inspect.ExecuteScalarAsync(cancellationToken) as string;
        if (kind is not null && !string.Equals(kind.Trim(), "U", StringComparison.Ordinal))
            throw new InvalidOperationException($"Physical-schema infrastructure object '{table}' must be an ordinary table.");
        if (kind is not null)
            return;
        await using var create = Command(connection, transaction, createSql);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    private InfrastructureColumn HashColumn(string name, string retainedColumn, int primaryKeyOrder) =>
        new(
            name,
            "binary(32)",
            false,
            null,
            primaryKeyOrder,
            IsComputed: true,
            IsPersisted: true,
            ComputedDefinition: SqlServerUnboundedIdentityHash.Expression(Q(retainedColumn)));

    public override async Task<bool> TableExistsAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name = @table;");
        Add(command, "table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public override async Task<IReadOnlyDictionary<string, RelationalPhysicalColumnMetadata>> ReadColumnsAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable,
                   dc.definition,
                   c.collation_name,
                   COALESCE(ic.key_ordinal, 0), c.is_computed, COALESCE(cc.is_persisted, 0), cc.definition
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.indexes i ON i.object_id = c.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic ON ic.object_id = c.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id;
            """);
        Add(command, "table", table);
        var result = new Dictionary<string, RelationalPhysicalColumnMetadata>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var type = FormatType(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4));
            result.Add(name, new RelationalPhysicalColumnMetadata(
                name,
                type,
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : NormalizeDatabaseDefault(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetBoolean(9),
                Convert.ToBoolean(reader.GetValue(10), CultureInfo.InvariantCulture),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return result;
    }

    public override async Task<RelationalPhysicalIndexMetadata?> ReadIndexAsync(
        DbConnection connection, DbTransaction transaction, string table, string index, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT i.is_unique, c.name, ic.is_descending_key, i.filter_definition
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @index AND ic.key_ordinal > 0
            ORDER BY ic.key_ordinal;
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

    private static string CollationToken(string value)
    {
        if (value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException($"SQL Server collation '{value}' is not a valid provider collation identifier.");
        return value;
    }

    private static string? DefaultSql(ProjectedColumnDefinition definition)
    {
        if (definition.DefaultValue is null)
            return null;
        var value = RelationalPhysicalProjectionValues.ConvertScalar(definition.DefaultValue, definition);
        return value switch
        {
            string text => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            bool boolean => boolean ? "1" : "0",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            DateTimeOffset dateTime => $"'{dateTime.ToUniversalTime():O}'",
            Guid guid => $"'{guid:D}'",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported SQL Server default value type '{value.GetType().Name}'.")
        };
    }

    private static string FormatType(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "nvarchar" => maxLength == -1 ? "nvarchar(max)" : $"nvarchar({maxLength / 2})",
        "binary" => $"binary({maxLength})",
        "varbinary" => maxLength == -1 ? "varbinary(max)" : $"varbinary({maxLength})",
        "decimal" or "numeric" => $"decimal({precision},{scale})",
        "datetimeoffset" => $"datetimeoffset({scale})",
        _ => type
    };

    private static string NormalizeDatabaseDefault(string value)
    {
        var result = value.Trim();
        while (result.Length > 1 && result[0] == '(' && result[^1] == ')')
            result = result[1..^1].Trim();
        return result;
    }

    private static bool HasRedundantOuterParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
            return false;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += value[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0
            };
            if (depth == 0 && index < value.Length - 1)
                return false;
        }
        return depth == 0;
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
