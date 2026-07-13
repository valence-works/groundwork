using System.Data.Common;
using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

/// <summary>SQL Server document store that executes compiled physical storage routes.</summary>
public sealed class SqlServerPhysicalDocumentStore : RelationalPhysicalDocumentStore
{
    public SqlServerPhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(
            connectionString,
            manifest,
            routes,
            access,
            new SqlServerPhysicalIdentityHash(),
            scopeObserver)
    {
    }

    internal SqlServerPhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        SqlServerPhysicalIdentityHash hash,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            manifest,
            routes,
            new SqlServerPhysicalDocumentDialect(hash),
            access,
            scopeObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal SqlServerPhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(sessions, manifest, routes, access, new SqlServerPhysicalIdentityHash(), scopeObserver)
    {
    }

    internal SqlServerPhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        SqlServerPhysicalIdentityHash hash,
        IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, routes, new SqlServerPhysicalDocumentDialect(hash), access, scopeObserver)
    {
    }
}

internal sealed class SqlServerPhysicalDocumentDialect : RelationalPhysicalDocumentDialect
{
    private readonly SqlServerPhysicalIdentity identity;

    public SqlServerPhysicalDocumentDialect()
        : this(new SqlServerPhysicalIdentityHash())
    {
    }

    internal SqlServerPhysicalDocumentDialect(SqlServerPhysicalIdentityHash hash) =>
        identity = new SqlServerPhysicalIdentity(hash);

    public override void ValidateRoute(ExecutableStorageRoute route) => identity.ValidateRoute(route);
    public override string ExactIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        identity.ExactPredicate(parts, QuoteIdentifier, includeOriginal: true);
    public override string? HashOnlyIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        identity.ExactPredicate(parts, QuoteIdentifier, includeOriginal: false);
    public override string ExactIdentityJoin(IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts) =>
        SqlServerPhysicalIdentity.ExactJoin(parts, QuoteIdentifier);
    public override int MaxParameters => 2100;
    public override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    public override bool IsUniqueConstraintException(DbException exception) =>
        exception is SqlException { Number: 2601 or 2627 };

    public override object? ConvertProjectionValue(object? value, ProjectedColumnDefinition definition) => value switch
    {
        DateTimeOffset dateTime => dateTime.ToUniversalTime(),
        _ => value
    };

    public override object ConvertQueryValue(
        string value,
        IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        ConvertProjectionValue(base.ConvertQueryValue(value, valueKind, definition), definition)!;

    public override string JsonValue(string canonicalJsonExpression, string stablePath) =>
        $"JSON_VALUE({canonicalJsonExpression}, {SqlLiteral(JsonPath(stablePath))})";

    public override string Contains(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string StartsWith(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter) =>
        $"{selectSql} OFFSET {skipParameter} ROWS FETCH NEXT {takeParameter} ROWS ONLY;";

    public override string ApplyFirst(string selectSql) =>
        selectSql.StartsWith("SELECT ", StringComparison.Ordinal)
            ? $"SELECT TOP (1) {selectSql[7..]};"
            : throw new InvalidOperationException("A SQL Server first query must begin with SELECT.");

    private static string JsonPath(string stablePath)
    {
        var result = new StringBuilder("$");
        foreach (var segment in stablePath.Split('.'))
            result.Append(".\"").Append(segment
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        return result.ToString();
    }

    private static string SqlLiteral(string value) => $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
