using System.Data.Common;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Provider.Relational;
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

    public override string Contains(string fieldExpression, string parameterExpression) =>
        $"{fieldExpression} ILIKE {parameterExpression} ESCAPE '\\'";

    public override string StartsWith(string fieldExpression, string parameterExpression) =>
        $"{fieldExpression} ILIKE {parameterExpression} ESCAPE '\\'";

    public override string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter) =>
        $"{selectSql} LIMIT {takeParameter} OFFSET {skipParameter};";

    public override string ApplyFirst(string selectSql) => $"{selectSql} LIMIT 1;";
}
