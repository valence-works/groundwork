using System.Data.Common;
using Groundwork.Core.Manifests;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

/// <summary>SQLite document store that executes compiled physical storage routes.</summary>
public sealed class SqlitePhysicalDocumentStore : RelationalPhysicalDocumentStore
{
    public SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, routes, new SqlitePhysicalDocumentDialect(), access, scopeObserver)
    {
    }

    internal SqlitePhysicalDocumentStore(
        SqliteConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction> createMutationTransaction,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            connection,
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            createMutationTransaction,
            scopeObserver)
    {
    }

    internal SqlitePhysicalDocumentStore(
        RelationalSessionFactory sessions,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, routes, new SqlitePhysicalDocumentDialect(), access, scopeObserver)
    {
    }

    public SqlitePhysicalDocumentStore(
        string connectionString,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : base(
            SqliteRelationalSessions.CreateSerializedImmediate(connectionString),
            manifest,
            routes,
            new SqlitePhysicalDocumentDialect(),
            access,
            scopeObserver)
    {
    }
}

internal sealed class SqlitePhysicalDocumentDialect : RelationalPhysicalDocumentDialect
{
    private const int ConstraintPrimaryKey = 1555;
    private const int ConstraintUnique = 2067;

    public override int MaxParameters => 999;

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override bool IsUniqueConstraintException(DbException exception) =>
        exception is SqliteException
        {
            SqliteExtendedErrorCode: ConstraintPrimaryKey or ConstraintUnique
        };

    public override string JsonValue(string canonicalJsonExpression, string stablePath)
    {
        var path = "$." + string.Join('.', stablePath.Split('.').Select(segment => $"\"{segment.Replace("\"", "\\\"")}\""));
        return $"json_extract({canonicalJsonExpression}, '{path.Replace("'", "''")}')";
    }

    public override string SetJsonValue(
        string canonicalJsonExpression,
        string jsonPathParameter,
        string jsonValueParameter) =>
        $"json_set({canonicalJsonExpression}, {jsonPathParameter}, json({jsonValueParameter}))";

    public override string NormalizeQueryExpression(
        string expression,
        PhysicalQueryFieldSource source,
        IndexValueKind valueKind) => valueKind switch
        {
            IndexValueKind.Boolean when source == PhysicalQueryFieldSource.CanonicalJsonPath =>
                $"CAST({expression} AS INTEGER)",
            _ => expression
        };

    public override object? ConvertProjectionValue(object? value, ProjectedColumnDefinition definition) =>
        SqlitePhysicalValueConverter.ToStorage(value, definition);

    public override object ConvertQueryValue(
        string value,
        IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        SqlitePhysicalValueConverter.FromQuery(value, valueKind, definition);

    public override string Contains(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string StartsWith(string fieldExpression, string parameterExpression) =>
        $"LOWER({fieldExpression}) LIKE LOWER({parameterExpression}) ESCAPE '\\'";

    public override string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter) =>
        $"{selectSql} LIMIT {takeParameter} OFFSET {skipParameter};";

    public override string ApplyFirst(string selectSql) => $"{selectSql} LIMIT 1;";

    public override string QuerySource(string tableIdentifier, string alias, string? indexIdentifier) =>
        indexIdentifier is null
            ? $"{QuoteIdentifier(tableIdentifier)} {alias}"
            : $"{QuoteIdentifier(tableIdentifier)} AS {alias} INDEXED BY {QuoteIdentifier(indexIdentifier)}";

    public override string CreateMutationSelectionTable(
        string tableExpression,
        string documentKindColumn,
        string storageScopeColumn,
        string documentIdColumn) =>
        $"CREATE TEMP TABLE {tableExpression} (" +
        $"{QuoteIdentifier(documentKindColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(storageScopeColumn)} TEXT NOT NULL, " +
        $"{QuoteIdentifier(documentIdColumn)} TEXT NOT NULL, " +
        $"PRIMARY KEY ({QuoteIdentifier(documentKindColumn)}, {QuoteIdentifier(storageScopeColumn)}, {QuoteIdentifier(documentIdColumn)})) WITHOUT ROWID;";

    public override ValueTask<DbTransaction> BeginMutationTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DbTransaction>(
            ((SqliteConnection)connection).BeginTransaction(deferred: false));
    }
}
