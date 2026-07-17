using System.Data.Common;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Provider.Relational;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

/// <summary>PostgreSQL document store that executes compiled physical storage routes.</summary>
public sealed class PostgreSqlPhysicalDocumentStore : RelationalPhysicalDocumentStore
{
    public PostgreSqlPhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            manifest,
            routes,
            new PostgreSqlPhysicalDocumentDialect(),
            access,
            scopeObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal PostgreSqlPhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, routes, new PostgreSqlPhysicalDocumentDialect(), access, scopeObserver)
    {
    }
}

internal sealed class PostgreSqlPhysicalDocumentDialect : RelationalPhysicalDocumentDialect
{
    public override int MaxParameters => 65535;
    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    public override bool IsUniqueConstraintException(DbException exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    public override bool CanInspectIdentityAfterUniqueConstraintException => false;
    public override string InsertPrimaryIfAbsent(
        string tableIdentifier,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> valueExpressions,
        IReadOnlyList<string> logicalPrimaryKey,
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> lookupIdentity) =>
        $"INSERT INTO {QuoteIdentifier(tableIdentifier)} ({string.Join(", ", columns.Select(QuoteIdentifier))}) " +
        $"VALUES ({string.Join(", ", valueExpressions)}) " +
        $"ON CONFLICT ({string.Join(", ", logicalPrimaryKey.Select(QuoteIdentifier))}) DO NOTHING;";
    public override string MutationOperationIdentityPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        PostgreSqlMutationOperationIdentity.ExactPredicate(parts, QuoteIdentifier);

    public override object? ConvertProjectionValue(object? value, ProjectedColumnDefinition definition) => value switch
    {
        DateTimeOffset dateTime => dateTime.UtcDateTime.Ticks,
        _ => value
    };

    public override object ConvertQueryValue(
        string value,
        IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        ConvertProjectionValue(base.ConvertQueryValue(value, valueKind, definition), definition)!;

    public override string JsonValue(string canonicalJsonExpression, string stablePath)
    {
        var segments = string.Join(", ", stablePath.Split('.').Select(segment =>
            $"'{segment.Replace("'", "''", StringComparison.Ordinal)}'"));
        return $"jsonb_extract_path_text(({canonicalJsonExpression})::jsonb, {segments})";
    }

    public override string SetJsonValue(
        string canonicalJsonExpression,
        string jsonPathParameter,
        string jsonValueParameter) =>
        $"jsonb_set(({canonicalJsonExpression})::jsonb, {jsonPathParameter}, " +
        $"({jsonValueParameter})::jsonb, true)::text";

    public override object ConvertMutationJsonPath(string stablePath) => stablePath.Split('.');

    public override string Contains(string fieldExpression, string parameterExpression) =>
        $"{fieldExpression} ILIKE {parameterExpression} ESCAPE '\\'";

    public override string StartsWith(string fieldExpression, string parameterExpression) =>
        $"{fieldExpression} ILIKE {parameterExpression} ESCAPE '\\'";

    public override string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter) =>
        $"{selectSql} LIMIT {takeParameter} OFFSET {skipParameter};";

    public override string ApplyFirst(string selectSql) => $"{selectSql} LIMIT 1;";

    public override string OrderExpression(string fieldExpression, PhysicalSortDirection direction) =>
        base.OrderExpression(fieldExpression, direction) +
        (direction == PhysicalSortDirection.Descending ? " NULLS LAST" : " NULLS FIRST");

    public override string CompleteMutationSelection(string selectSql, bool includesLinkedStorage) =>
        $"{selectSql} FOR UPDATE OF p{(includesLinkedStorage ? ", l" : string.Empty)}";

    public override string CreateMutationSelectionTable(
        string tableExpression,
        string documentKindColumn,
        string storageScopeColumn,
        string documentIdColumn,
        string documentIdComparisonColumn,
        string documentIdLookupColumn,
        string documentVersionColumn,
        string documentIncarnationColumn) =>
        $"CREATE TEMP TABLE {tableExpression} (" +
        $"{QuoteIdentifier(documentKindColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"{QuoteIdentifier(storageScopeColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"{QuoteIdentifier(documentIdColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"{QuoteIdentifier(documentIdComparisonColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"{QuoteIdentifier(documentIdLookupColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"{QuoteIdentifier(documentVersionColumn)} bigint NOT NULL, " +
        $"{QuoteIdentifier(documentIncarnationColumn)} text COLLATE pg_catalog.\"C\" NOT NULL, " +
        $"PRIMARY KEY ({QuoteIdentifier(documentKindColumn)}, {QuoteIdentifier(storageScopeColumn)}, {QuoteIdentifier(documentIdLookupColumn)})) " +
        "ON COMMIT DROP;";

    public override async Task AcquireMutationOperationLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string operationLock,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@operationLock, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@operationLock";
        parameter.Value = operationLock;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
