using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

public sealed record RelationalPhysicalIdentityPredicatePart(
    string ColumnIdentifier,
    string? Alias,
    string ValueExpression);

public sealed record RelationalPhysicalIdentityJoinPart(
    string LeftColumnIdentifier,
    string? LeftAlias,
    string RightColumnIdentifier,
    string? RightAlias);

public sealed record RelationalPhysicalIdentityPrefixRange(object Lower, object? Upper);

internal enum RelationalPhysicalWriteExecutionPoint
{
    BeforePrimaryLock,
    AfterPrimaryLock
}

internal enum RelationalPhysicalWriteOperation
{
    Save,
    Delete
}

internal delegate ValueTask RelationalPhysicalWriteInterceptor(
    RelationalPhysicalWriteExecutionPoint point,
    RelationalPhysicalWriteOperation operation,
    DbConnection connection,
    DbTransaction transaction,
    CancellationToken cancellationToken);

internal interface IRelationalPhysicalMutationTransaction : IAsyncDisposable
{
    DbTransaction Transaction { get; }
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

internal sealed class RelationalPhysicalMutationTransaction(DbTransaction transaction) :
    IRelationalPhysicalMutationTransaction
{
    public DbTransaction Transaction { get; } = transaction;

    public Task CommitAsync(CancellationToken cancellationToken) =>
        Transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        Transaction.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => Transaction.DisposeAsync();
}

/// <summary>Provider SQL primitives used by the reusable route-driven relational document store.</summary>
public abstract class RelationalPhysicalDocumentDialect
{
    public virtual void ValidateRoute(ExecutableStorageRoute route) => ArgumentNullException.ThrowIfNull(route);
    public abstract string QuoteIdentifier(string identifier);
    public virtual string ExactIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(" AND ", parts.Select(part =>
            $"{Qualified(part.Alias, part.ColumnIdentifier)} = {part.ValueExpression}"));
    }
    public virtual string? HashOnlyIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return null;
    }
    public virtual string MutationOperationIdentityPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(" AND ", parts.Select(part =>
            $"{Qualified(part.Alias, part.ColumnIdentifier)} = {part.ValueExpression}"));
    }
    public virtual string InsertPrimaryIfAbsent(
        string tableIdentifier,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> valueExpressions,
        IReadOnlyList<string> logicalPrimaryKey,
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> lookupIdentity) =>
        $"INSERT INTO {QuoteIdentifier(tableIdentifier)} ({string.Join(", ", columns.Select(QuoteIdentifier))}) " +
        $"VALUES ({string.Join(", ", valueExpressions)});";
    public virtual bool CanInspectIdentityAfterUniqueConstraintException => true;
    public virtual string ExactIdentityJoin(IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(" AND ", parts.Select(part =>
            $"{Qualified(part.LeftAlias, part.LeftColumnIdentifier)} = {Qualified(part.RightAlias, part.RightColumnIdentifier)}"));
    }
    public virtual string Parameter(string name) => $"@{name}";
    public abstract int MaxParameters { get; }
    public abstract bool IsUniqueConstraintException(DbException exception);
    public abstract string JsonValue(string canonicalJsonExpression, string stablePath);
    public virtual string SetJsonValue(
        string canonicalJsonExpression,
        string jsonPathParameter,
        string jsonValueParameter) =>
        throw new NotSupportedException("This relational provider does not support physical JSON transitions.");
    public virtual object ConvertMutationJsonPath(string stablePath) =>
        "$." + string.Join('.', stablePath.Split('.').Select(segment =>
            $"\"{segment.Replace("\"", "\\\"", StringComparison.Ordinal)}\""));
    public virtual object ConvertMutationJsonValue(
        string value,
        Groundwork.Core.Indexing.IndexValueKind valueKind) => valueKind switch
        {
            Groundwork.Core.Indexing.IndexValueKind.Boolean or Groundwork.Core.Indexing.IndexValueKind.Number =>
                JsonSerializer.Serialize(RelationalPhysicalProjectionValues.ConvertScalar(value, valueKind)),
            _ => JsonSerializer.Serialize(value)
        };
    public virtual string NormalizeQueryExpression(
        string expression,
        PhysicalQueryFieldSource source,
        Groundwork.Core.Indexing.IndexValueKind valueKind) => expression;
    public virtual object? ConvertProjectionValue(object? value, ProjectedColumnDefinition definition) => value;
    public virtual object ConvertQueryValue(
        string value,
        Groundwork.Core.Indexing.IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        RelationalPhysicalProjectionValues.ConvertScalar(value, valueKind, definition);
    public virtual object ConvertDocumentIdentityOriginal(string value)
    {
        PortableStringComparison.ValidateIdentity(value);
        return value;
    }
    public virtual object ConvertDocumentIdentityComparison(string value) => value;
    public virtual object ConvertDocumentIdentityLookup(string value) => value;
    public virtual RelationalPhysicalIdentityPrefixRange ConvertDocumentIdentityPrefix(string comparisonKey)
    {
        ArgumentNullException.ThrowIfNull(comparisonKey);
        var upper = comparisonKey.Length == 0
            ? null
            : comparisonKey[..^1] + (char)(comparisonKey[^1] + 1);
        return new RelationalPhysicalIdentityPrefixRange(
            ConvertDocumentIdentityComparison(comparisonKey),
            upper is null ? null : ConvertDocumentIdentityComparison(upper));
    }
    public virtual string ReadDocumentIdentityComparison(DbDataReader reader, int ordinal) =>
        reader.GetString(ordinal);
    public virtual string ReadDocumentIdentityLookup(DbDataReader reader, int ordinal) =>
        reader.GetString(ordinal);
    public virtual bool PhysicalIdentityValueEquals(object retained, object expected) =>
        Equals(retained, expected);
    public abstract string Contains(string fieldExpression, string parameterExpression);
    public abstract string StartsWith(string fieldExpression, string parameterExpression);
    public abstract string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter);
    public abstract string ApplyFirst(string selectSql);
    public virtual string OrderExpression(string fieldExpression, PhysicalSortDirection direction) =>
        $"{fieldExpression} {(direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}";
    public virtual string QuerySource(string tableIdentifier, string alias, string? indexIdentifier) =>
        $"{QuoteIdentifier(tableIdentifier)} {alias}";
    public virtual string MutationQuerySource(string tableIdentifier, string alias, string? indexIdentifier) =>
        QuerySource(tableIdentifier, alias, indexIdentifier);
    public virtual string CompleteMutationSelection(string selectSql, bool includesLinkedStorage) => selectSql;
    public virtual string MutationSelectionTable(string logicalName) => QuoteIdentifier(logicalName);
    public virtual string CreateMutationSelectionTable(
        string tableExpression,
        string documentKindColumn,
        string storageScopeColumn,
        string documentIdColumn,
        string documentIdComparisonColumn,
        string documentIdLookupColumn,
        string documentVersionColumn,
        string documentIncarnationColumn) =>
        throw new NotSupportedException("This relational provider does not support bounded mutation identity selection.");
    public virtual string LockByMutationSelection(
        string tableIdentifier,
        string selectionTableExpression,
        string exactIdentityJoin,
        string selectionKindColumn,
        string selectionScopeColumn,
        string selectionIdColumn)
    {
        var sql = $"SELECT 1 FROM {selectionTableExpression} AS s " +
                  $"JOIN {MutationQuerySource(tableIdentifier, "p", null)} ON {exactIdentityJoin} " +
                  $"ORDER BY s.{QuoteIdentifier(selectionKindColumn)}, " +
                  $"s.{QuoteIdentifier(selectionScopeColumn)}, s.{QuoteIdentifier(selectionIdColumn)}";
        return CompleteMutationSelection(sql, includesLinkedStorage: false) + ";";
    }
    public virtual string DropMutationSelectionTable(string tableExpression) =>
        $"DROP TABLE IF EXISTS {tableExpression};";
    public virtual string DeleteByMutationSelection(
        string tableExpression,
        string alias,
        string selectionTableExpression,
        string exactIdentityJoin) =>
        $"DELETE FROM {tableExpression} AS {alias} WHERE EXISTS (" +
        $"SELECT 1 FROM {selectionTableExpression} AS s WHERE {exactIdentityJoin});";
    public virtual string UpdateByMutationSelection(
        string tableExpression,
        string alias,
        IReadOnlyList<string> assignments,
        string selectionTableExpression,
        string exactIdentityJoin) =>
        $"UPDATE {tableExpression} AS {alias} SET {string.Join(", ", assignments)} " +
        $"WHERE EXISTS (SELECT 1 FROM {selectionTableExpression} AS s WHERE {exactIdentityJoin});";
    public virtual ValueTask<DbTransaction> BeginMutationTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        connection.BeginTransactionAsync(cancellationToken);
    public virtual Task AcquireMutationOperationLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string operationLock,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected string Qualified(string? alias, string identifier) =>
        alias is null ? QuoteIdentifier(identifier) : $"{alias}.{QuoteIdentifier(identifier)}";
}

/// <summary>
/// Relational execution of compiled storage routes. It never derives table, column, key, or
/// maintenance choices from a manifest a second time; the executable route is the authority.
/// </summary>
public class RelationalPhysicalDocumentStore : IDocumentStore
{
    private readonly DbConnection? connection;
    private readonly RelationalSessionFactory? sessionFactory;
    private readonly StorageManifest manifest;
    private readonly IReadOnlyDictionary<string, ExecutableStorageRoute> routes;
    private readonly RelationalPhysicalDocumentDialect dialect;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly Func<CancellationToken, ValueTask>? beforeNonSuccessAbort;
    private readonly Func<DbTransaction, IRelationalPhysicalMutationTransaction> createMutationTransaction;
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    internal RelationalPhysicalWriteInterceptor? WriteInterceptor { get; set; }

    public RelationalPhysicalDocumentStore(
        DbConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(connection ?? throw new ArgumentNullException(nameof(connection)), null, manifest, routes, dialect, access, scopeObserver, null)
    {
    }

    internal RelationalPhysicalDocumentStore(
        DbConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        Func<CancellationToken, ValueTask> beforeNonSuccessAbort,
        IStorageScopeObserver? scopeObserver = null)
        : this(connection, null, manifest, routes, dialect, access, scopeObserver, null)
    {
        this.beforeNonSuccessAbort = beforeNonSuccessAbort ??
            throw new ArgumentNullException(nameof(beforeNonSuccessAbort));
    }

    internal RelationalPhysicalDocumentStore(
        DbConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction> createMutationTransaction,
        IStorageScopeObserver? scopeObserver = null)
        : this(
            connection,
            null,
            manifest,
            routes,
            dialect,
            access,
            scopeObserver,
            createMutationTransaction ?? throw new ArgumentNullException(nameof(createMutationTransaction)))
    {
    }

    public RelationalPhysicalDocumentStore(
        RelationalSessionFactory sessionFactory,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(null, sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)), manifest, routes, dialect, access, scopeObserver, null)
    {
    }

    private RelationalPhysicalDocumentStore(
        DbConnection? connection,
        RelationalSessionFactory? sessionFactory,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        Func<DbTransaction, IRelationalPhysicalMutationTransaction>? createMutationTransaction)
    {
        this.connection = connection;
        this.sessionFactory = sessionFactory;
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        this.createMutationTransaction = createMutationTransaction ??
            (transaction => new RelationalPhysicalMutationTransaction(transaction));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
        this.routes = (routes ?? throw new ArgumentNullException(nameof(routes)))
            .ToDictionary(route => route.StorageUnit.Value, StringComparer.Ordinal);
        if (this.routes.Count != manifest.StorageUnits.Count ||
            manifest.StorageUnits.Any(unit => !this.routes.ContainsKey(unit.Identity.Value)))
            throw new ArgumentException("Every manifest storage unit requires exactly one executable route.", nameof(routes));
        foreach (var route in this.routes.Values)
        {
            RelationalPhysicalStorageColumns.Validate(route);
            dialect.ValidateRoute(route);
        }
    }

    public DocumentStoreAccess Access { get; }
    public TransactionBoundary TransactionBoundary => TransactionBoundary.CrossUnitAtomic;

    public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDocumentIdentity(request.Id);
        return await ExecuteAsync(async (current, ct) =>
        {
            await using var transaction = await current.BeginTransactionAsync(ct);
            var result = await SaveCoreAsync(request, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Saved)
                await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
    {
        ValidateDocumentIdentity(id);
        return await ExecuteAsync((current, ct) => LoadCoreAsync(current, documentKind, id, null, ct), cancellationToken);
    }

    public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDocumentIdentity(request.Id);
        return await ExecuteAsync(async (current, ct) =>
        {
            await using var transaction = await current.BeginTransactionAsync(ct);
            var result = await DeleteCoreAsync(request, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Deleted)
                await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    public async Task<IDocumentUnitOfWork> BeginAsync(DocumentCommitScope scope, CancellationToken cancellationToken = default)
    {
        var units = scope.Kinds.Select(GetUnit).ToArray();
        if (units.Select(unit => unit.Tenancy.Kind).Distinct().Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));
        var selections = units.Select(unit => ResolveScope(unit, StorageScopeOperation.BeginUnitOfWork)).ToArray();
        if (selections.Select(selection => selection.StorageKey).Distinct(StringComparer.Ordinal).Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));

        if (sessionFactory is not null)
            return new UnitOfWork(this, scope, await sessionFactory.BeginUnitOfWorkAsync(cancellationToken));

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);
            return new UnitOfWork(this, scope, await connection!.BeginTransactionAsync(cancellationToken));
        }
        catch
        {
            connectionGate.Release();
            throw;
        }
    }

    public Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(DocumentStoreQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven stores execute declared bounded DocumentQuery plans through IBoundedDocumentStore.");

#pragma warning disable GW0004
    public Task<DocumentQueryResult> QueryAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven stores execute declared bounded DocumentQuery plans through IBoundedDocumentStore.");

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven stores execute declared bounded DocumentQuery plans through IBoundedDocumentStore.");

    public Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven stores execute declared bounded DocumentQuery plans through IBoundedDocumentStore.");
#pragma warning restore GW0004

    internal ExecutableStorageRoute GetRoute(string documentKind) =>
        routes.TryGetValue(documentKind, out var route)
            ? route
            : throw new InvalidOperationException($"Document kind '{documentKind}' has no executable storage route.");

    internal DocumentScopeSelection ResolveQueryScope(string documentKind) =>
        DocumentStoreScopeResolver.Resolve(GetUnit(documentKind), Access, StorageScopeOperation.Query, scopeObserver, allowAcrossScopes: true);

    internal DocumentScopeSelection ResolveMutationScope(string documentKind) =>
        DocumentStoreScopeResolver.Resolve(GetUnit(documentKind), Access, StorageScopeOperation.Mutate, scopeObserver);

    internal string ManifestIdentity => manifest.Identity.Value;

    internal StorageManifest BoundManifest => manifest;

    internal bool IsBoundRoute(ExecutableStorageRoute route) =>
        routes.TryGetValue(route.StorageUnit.Value, out var bound) &&
        string.Equals(bound.Fingerprint, route.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(bound.DefinitionFingerprint, route.DefinitionFingerprint, StringComparison.Ordinal);

    internal Task<T> ExecutePhysicalQueryAsync<T>(Func<DbConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        ExecuteAsync(action, cancellationToken);

    internal async Task<T> ExecutePhysicalMutationAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action,
        Func<CancellationToken, ValueTask>? beforeCommit,
        Func<CancellationToken, ValueTask>? afterCommitBeforeAcknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (sessionFactory is not null)
        {
            var unitOfWork = await sessionFactory.BeginUnitOfWorkAsync(cancellationToken);
            return await CompletePhysicalMutationAsync(
                unitOfWork,
                ct => unitOfWork.Executor.ExecuteAsync(action, ct),
                unitOfWork.CommitAsync,
                unitOfWork.RollbackAsync,
                beforeCommit,
                afterCommitBeforeAcknowledgement,
                cancellationToken);
        }

        return await ExecuteAsync(async (current, ct) =>
        {
            var transaction = createMutationTransaction(
                await dialect.BeginMutationTransactionAsync(current, ct)) ??
                throw new InvalidOperationException("The physical mutation transaction factory returned null.");
            return await CompletePhysicalMutationAsync(
                transaction,
                token => action(current, transaction.Transaction, token),
                transaction.CommitAsync,
                transaction.RollbackAsync,
                beforeCommit,
                afterCommitBeforeAcknowledgement,
                ct);
        }, cancellationToken);
    }

    private static async Task<T> CompletePhysicalMutationAsync<T>(
        IAsyncDisposable transaction,
        Func<CancellationToken, Task<T>> execute,
        Func<CancellationToken, Task> commit,
        Func<CancellationToken, Task> rollback,
        Func<CancellationToken, ValueTask>? beforeCommit,
        Func<CancellationToken, ValueTask>? afterCommitBeforeAcknowledgement,
        CancellationToken cancellationToken)
    {
        var committed = false;
        Exception? primaryFailure = null;
        try
        {
            var result = await execute(cancellationToken);
            if (beforeCommit is not null)
                await beforeCommit(cancellationToken);
            await commit(cancellationToken);
            committed = true;
            if (afterCommitBeforeAcknowledgement is not null)
                await afterCommitBeforeAcknowledgement(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!committed)
            {
                try
                {
                    await rollback(CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(exception, cleanupFailure);
                }
            }
            throw;
        }
        finally
        {
            await DisposeMutationResourceAsync(transaction, primaryFailure);
        }
    }

    private static async ValueTask DisposeMutationResourceAsync(
        IAsyncDisposable resource,
        Exception? primaryFailure)
    {
        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
        }
    }

    internal static DbCommand CreatePhysicalCommand(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    internal void AddPhysicalParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = dialect.Parameter(name);
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    internal string Q(string identifier) => dialect.QuoteIdentifier(identifier);
    internal string JsonValue(string expression, string path) => dialect.JsonValue(expression, path);
    internal string SetJsonValue(string expression, string pathParameter, string valueParameter) =>
        dialect.SetJsonValue(expression, pathParameter, valueParameter);
    internal object ConvertMutationJsonPath(string stablePath) =>
        dialect.ConvertMutationJsonPath(stablePath);
    internal object ConvertMutationJsonValue(string value, Groundwork.Core.Indexing.IndexValueKind valueKind) =>
        dialect.ConvertMutationJsonValue(value, valueKind);
    internal string NormalizeQueryExpression(
        string expression,
        PhysicalQueryFieldSource source,
        Groundwork.Core.Indexing.IndexValueKind valueKind) =>
        dialect.NormalizeQueryExpression(expression, source, valueKind);
    internal object ConvertPhysicalQueryValue(
        string value,
        Groundwork.Core.Indexing.IndexValueKind valueKind,
        ProjectedColumnDefinition definition) =>
        dialect.ConvertQueryValue(value, valueKind, definition);
    internal void ValidateDocumentIdentity(string value) =>
        _ = dialect.ConvertDocumentIdentityOriginal(value);
    internal object ConvertDocumentIdentityComparison(string value) =>
        dialect.ConvertDocumentIdentityComparison(value);
    internal object ConvertDocumentIdentityLookup(string value) =>
        dialect.ConvertDocumentIdentityLookup(value);
    internal object ConvertDocumentIdentityQueryValue(PhysicalQueryField field, string value) =>
        field.Path switch
        {
            PhysicalDocumentIdentityFieldPaths.Original => dialect.ConvertDocumentIdentityOriginal(value),
            PhysicalDocumentIdentityFieldPaths.Comparison => dialect.ConvertDocumentIdentityComparison(value),
            PhysicalDocumentIdentityFieldPaths.Lookup => dialect.ConvertDocumentIdentityLookup(value),
            _ => throw new InvalidOperationException(
                $"Document identity field '{field.Path}' does not identify retained physical evidence.")
        };
    internal RelationalPhysicalIdentityPrefixRange ConvertDocumentIdentityPrefix(string comparisonKey) =>
        dialect.ConvertDocumentIdentityPrefix(comparisonKey);
    internal string ReadDocumentIdentityComparison(DbDataReader reader, int ordinal) =>
        dialect.ReadDocumentIdentityComparison(reader, ordinal);
    internal string ReadDocumentIdentityLookup(DbDataReader reader, int ordinal) =>
        dialect.ReadDocumentIdentityLookup(reader, ordinal);
    internal RelationalPhysicalEnvelopeRow ReadPhysicalEnvelope(DbDataReader reader) =>
        RelationalPhysicalEnvelopeRowLayout.Read(reader, dialect);
    internal string P(string name) => dialect.Parameter(name);
    internal int MaxPhysicalParameters => dialect.MaxParameters;
    internal string Contains(string field, string parameter) => dialect.Contains(field, parameter);
    internal string StartsWith(string field, string parameter) => dialect.StartsWith(field, parameter);
    internal string ApplyOffsetPage(string sql, string take, string skip) => dialect.ApplyOffsetPage(sql, take, skip);
    internal string ApplyFirst(string sql) => dialect.ApplyFirst(sql);
    internal string OrderPhysicalQueryExpression(string field, PhysicalSortDirection direction) =>
        dialect.OrderExpression(field, direction);
    internal string PhysicalQuerySource(string table, string alias, string? index) =>
        dialect.QuerySource(table, alias, index);
    internal string PhysicalMutationQuerySource(string table, string alias, string? index) =>
        dialect.MutationQuerySource(table, alias, index);
    internal string CompleteMutationSelection(string selectSql, bool includesLinkedStorage) =>
        dialect.CompleteMutationSelection(selectSql, includesLinkedStorage);
    internal string MutationSelectionTable(string logicalName) =>
        dialect.MutationSelectionTable(logicalName);
    internal string CreateMutationSelectionTable(
        string table,
        string kindColumn,
        string scopeColumn,
        string idColumn,
        string idComparisonColumn,
        string idLookupColumn,
        string versionColumn,
        string incarnationColumn) =>
        dialect.CreateMutationSelectionTable(
            table,
            kindColumn,
            scopeColumn,
            idColumn,
            idComparisonColumn,
            idLookupColumn,
            versionColumn,
            incarnationColumn);
    internal string DropMutationSelectionTable(string table) =>
        dialect.DropMutationSelectionTable(table);
    internal string LockByMutationSelection(
        string table,
        string selectionTable,
        string exactIdentityJoin,
        string kindColumn,
        string scopeColumn,
        string idColumn) =>
        dialect.LockByMutationSelection(
            table,
            selectionTable,
            exactIdentityJoin,
            kindColumn,
            scopeColumn,
            idColumn);
    internal string DeleteByMutationSelection(
        string table,
        string alias,
        string selectionTable,
        string exactIdentityJoin) =>
        dialect.DeleteByMutationSelection(table, alias, selectionTable, exactIdentityJoin);
    internal string UpdateByMutationSelection(
        string table,
        string alias,
        IReadOnlyList<string> assignments,
        string selectionTable,
        string exactIdentityJoin) =>
        dialect.UpdateByMutationSelection(table, alias, assignments, selectionTable, exactIdentityJoin);
    internal string ExactPhysicalIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        dialect.ExactIdentityPredicate(parts);
    internal string MutationOperationIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        dialect.MutationOperationIdentityPredicate(parts);
    internal string ExactPhysicalIdentityJoin(IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts) =>
        dialect.ExactIdentityJoin(parts);
    internal Task AcquireMutationOperationLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string operationLock,
        CancellationToken cancellationToken) =>
        dialect.AcquireMutationOperationLockAsync(connection, transaction, operationLock, cancellationToken);

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(SaveDocumentRequest request, DbTransaction transaction, CancellationToken ct)
    {
        var unit = GetUnit(request.DocumentKind);
        var route = GetRoute(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Save);
        var existing = await LoadForWriteAsync(
            transaction.Connection!,
            transaction,
            request.DocumentKind,
            request.Id,
            RelationalPhysicalWriteOperation.Save,
            ct);
        if (existing is not null && !string.Equals(existing.Id, request.Id, StringComparison.Ordinal))
            return DocumentStoreWriteResult.IdentityConflict(existing.Id);
        if (existing is not null && request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;
        if (existing is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var createdAt = existing?.CreatedAt ?? now;
        var version = existing is null ? 1 : existing.Version + 1;
        var projectedValues = RelationalPhysicalProjectionValues.Read(request.ContentJson, route.ProjectedColumns);
        try
        {
            var affected = existing is null
                ? await InsertPrimaryAsync(route, request, scope, version, createdAt, now, projectedValues, transaction, ct)
                : await UpdatePrimaryAsync(route, request, scope, version, now, projectedValues, transaction, ct);
            if (affected != 1)
            {
                if (existing is null)
                {
                    var concurrent = await LoadCoreAsync(
                        transaction.Connection!,
                        request.DocumentKind,
                        request.Id,
                        transaction,
                        ct,
                        lockForWrite: true);
                    if (concurrent is not null)
                    {
                        return string.Equals(concurrent.Id, request.Id, StringComparison.Ordinal)
                            ? DocumentStoreWriteResult.ConcurrencyConflict
                            : DocumentStoreWriteResult.IdentityConflict(concurrent.Id);
                    }
                }
                return request.ExpectedVersion is null ? DocumentStoreWriteResult.NotFound : DocumentStoreWriteResult.ConcurrencyConflict;
            }
            await MaintainLinkedAsync(route, request, scope, projectedValues, transaction, ct);
        }
        catch (DbException exception) when (dialect.IsUniqueConstraintException(exception))
        {
            return DocumentStoreWriteResult.ConcurrencyConflict;
        }

        return DocumentStoreWriteResult.Saved(new DocumentEnvelope(
            request.DocumentKind,
            request.Id,
            request.SchemaVersion,
            version,
            request.ContentJson,
            createdAt,
            now)
        { Scope = scope.Scope });
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(DeleteDocumentRequest request, DbTransaction transaction, CancellationToken ct)
    {
        var unit = GetUnit(request.DocumentKind);
        var route = GetRoute(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Delete);
        var existing = await LoadForWriteAsync(
            transaction.Connection!,
            transaction,
            request.DocumentKind,
            request.Id,
            RelationalPhysicalWriteOperation.Delete,
            ct);
        if (existing is null)
            return DocumentStoreWriteResult.NotFound;
        if (request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        if (route.LinkedIndexStorage is not null)
            await DeleteLinkedAsync(route, request.Id, scope, transaction, ct);
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"DELETE FROM {Q(route.PrimaryStorage.Name.Identifier)} WHERE {IdentityPredicate(route, request.ExpectedVersion is not null)};",
            transaction);
        AddIdentityParameters(command, route, request.Id, scope);
        if (request.ExpectedVersion is not null)
            AddPhysicalParameter(command, "expectedVersion", request.ExpectedVersion.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1
            ? DocumentStoreWriteResult.Deleted(existing.Id)
            : DocumentStoreWriteResult.ConcurrencyConflict;
    }

    private async Task<int> InsertPrimaryAsync(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IReadOnlyDictionary<string, object?> projectedValues,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var projections = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage).ToArray();
        var columns = RelationalPhysicalEnvelopeRowLayout.PersistedColumns(route)
            .Concat([RelationalPhysicalStorageColumns.CreatedUtc, RelationalPhysicalStorageColumns.UpdatedUtc])
            .Concat(projections.Select(column => column.Column.Identifier))
            .ToArray();
        var parameters = columns.Select((_, index) => P($"v{index}")).ToArray();
        var lookupIdentity = new RelationalPhysicalIdentityPredicatePart[]
        {
            new(route.Discriminator.Column.Identifier, null, P("kind")),
            new(route.ScopeKey.Column.Identifier, null, P("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, P("idLookup"))
        };
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            dialect.InsertPrimaryIfAbsent(
                route.PrimaryStorage.Name.Identifier,
                columns,
                parameters,
                route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray(),
                lookupIdentity),
            transaction);
        var values = EnvelopeValues(route, request, scope, version).Concat<object?>([createdAt.ToString("O"), updatedAt.ToString("O")])
            .Concat(ProjectedValues(projectedValues, projections));
        AddValues(command, values);
        AddIdentityParameters(command, route, request.Id, scope);
        try
        {
            return await command.ExecuteNonQueryAsync(ct);
        }
        catch (DbException exception) when (
            dialect.IsUniqueConstraintException(exception) &&
            dialect.CanInspectIdentityAfterUniqueConstraintException)
        {
            await ThrowIfIdentityHashCollisionAsync(
                route.PrimaryStorage.Name.Identifier,
                PrimaryIdentity(route, request.Id, scope),
                transaction,
                ct);
            throw;
        }
    }

    private async Task<int> UpdatePrimaryAsync(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset updatedAt,
        IReadOnlyDictionary<string, object?> projectedValues,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var projections = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage).ToArray();
        var assignments = new List<(string Column, object? Value)>
        {
            (route.Envelope.SchemaVersion.Identifier, request.SchemaVersion),
            (route.Envelope.Version.Identifier, version),
            (route.Envelope.CanonicalJson.Identifier, request.ContentJson),
            (RelationalPhysicalStorageColumns.UpdatedUtc, updatedAt.ToString("O"))
        };
        assignments.AddRange(projections.Zip(ProjectedValues(projectedValues, projections), (column, value) => (column.Column.Identifier, value)));
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"UPDATE {Q(route.PrimaryStorage.Name.Identifier)} SET " +
            string.Join(", ", assignments.Select((item, index) => $"{Q(item.Column)} = {P($"v{index}")}")) +
            $" WHERE {IdentityPredicate(route, request.ExpectedVersion is not null)};",
            transaction);
        for (var index = 0; index < assignments.Count; index++)
            AddPhysicalParameter(command, $"v{index}", assignments[index].Value);
        AddIdentityParameters(command, route, request.Id, scope);
        if (request.ExpectedVersion is not null)
            AddPhysicalParameter(command, "expectedVersion", request.ExpectedVersion.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task MaintainLinkedAsync(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        DocumentScopeSelection scope,
        IReadOnlyDictionary<string, object?> projectedValues,
        DbTransaction transaction,
        CancellationToken ct)
    {
        if (route.LinkedIndexStorage is null)
            return;
        await DeleteLinkedAsync(route, request.Id, scope, transaction, ct);
        var relationship = route.LinkedRelationship!;
        var identity = relationship.Identity.Project(request.Id);
        var projections = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage).ToArray();
        var columns = new[]
        {
            relationship.DocumentKind.Identifier,
            relationship.StorageScope.Identifier,
            relationship.Identity.OriginalId.Identifier,
            relationship.Identity.ComparisonKey.Identifier,
            relationship.Identity.LookupKey.Identifier
        }.Concat(projections.Select(column => column.Column.Identifier)).ToArray();
        var values = new object?[]
            {
                route.Discriminator.Value,
                scope.StorageKey!,
                dialect.ConvertDocumentIdentityOriginal(identity.OriginalValue),
                dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey),
                dialect.ConvertDocumentIdentityLookup(identity.LookupKey)
            }
            .Concat(ProjectedValues(projectedValues, projections));
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"INSERT INTO {Q(route.LinkedIndexStorage.Name.Identifier)} ({string.Join(", ", columns.Select(Q))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, index) => P($"v{index}")))});",
            transaction);
        AddValues(command, values);
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (DbException exception) when (dialect.IsUniqueConstraintException(exception))
        {
            await ThrowIfIdentityHashCollisionAsync(
                route.LinkedIndexStorage.Name.Identifier,
                LinkedIdentity(route, request.Id, scope),
                transaction,
                ct);
            throw;
        }
    }

    private async Task DeleteLinkedAsync(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        var identity = relationship.Identity.Project(id);
        await EnsureLinkedIdentityEvidenceAsync(route, identity, scope, transaction, ct);
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"DELETE FROM {Q(route.LinkedIndexStorage!.Name.Identifier)} WHERE " +
            dialect.ExactIdentityPredicate(
            [
                new(relationship.DocumentKind.Identifier, null, P("kind")),
                new(relationship.StorageScope.Identifier, null, P("scope")),
                new(relationship.Identity.LookupKey.Identifier, null, P("idLookup")),
                new(relationship.Identity.ComparisonKey.Identifier, null, P("idComparison"))
            ]) + ";",
            transaction);
        AddPhysicalParameter(command, "kind", route.Discriminator.Value);
        AddPhysicalParameter(command, "scope", scope.StorageKey!);
        AddPhysicalParameter(command, "idLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
        AddPhysicalParameter(command, "idComparison", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureLinkedIdentityEvidenceAsync(
        ExecutableStorageRoute route,
        PortableStringIdentityProjection identity,
        DocumentScopeSelection scope,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"SELECT {Q(relationship.DocumentId.Identifier)}, {Q(relationship.Identity.ComparisonKey.Identifier)} " +
            $"FROM {Q(route.LinkedIndexStorage!.Name.Identifier)} WHERE " +
            dialect.ExactIdentityPredicate(
            [
                new(relationship.DocumentKind.Identifier, null, P("kind")),
                new(relationship.StorageScope.Identifier, null, P("scope")),
                new(relationship.Identity.LookupKey.Identifier, null, P("idLookup"))
            ]) + ";",
            transaction);
        AddPhysicalParameter(command, "kind", route.Discriminator.Value);
        AddPhysicalParameter(command, "scope", scope.StorageKey!);
        AddPhysicalParameter(command, "idLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;
        var retainedId = reader.GetString(0);
        if (!string.Equals(
                dialect.ReadDocumentIdentityComparison(reader, 1),
                identity.ComparisonKey,
                StringComparison.Ordinal))
        {
            throw new DocumentIdentityLookupCollisionException(
                route.Discriminator.Value,
                identity.OriginalValue,
                retainedId,
                identity.LookupKey);
        }
    }

    private async Task<DocumentEnvelope?> LoadCoreAsync(
        DbConnection currentConnection,
        string documentKind,
        string id,
        DbTransaction? transaction,
        CancellationToken ct,
        bool lockForWrite = false)
    {
        var unit = GetUnit(documentKind);
        var route = GetRoute(documentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Load);
        var source = lockForWrite
            ? dialect.MutationQuerySource(
                route.PrimaryStorage.Name.Identifier,
                "p",
                indexIdentifier: null)
            : Q(route.PrimaryStorage.Name.Identifier);
        var sql =
            $"SELECT {string.Join(", ", RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route).Select(Q))} " +
            $"FROM {source} WHERE {IdentityLookupPredicate(route)}";
        if (lockForWrite)
            sql = dialect.CompleteMutationSelection(sql, includesLinkedStorage: false);
        await using var command = CreatePhysicalCommand(
            currentConnection,
            $"{sql};",
            transaction);
        AddIdentityParameters(command, route, id, scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var identity = route.Envelope.Identity.Project(id);
        var row = ReadPhysicalEnvelope(reader);
        if (!string.Equals(row.ComparisonKey, identity.ComparisonKey, StringComparison.Ordinal))
        {
            throw new DocumentIdentityLookupCollisionException(
                documentKind,
                id,
                row.Envelope.Id,
                identity.LookupKey);
        }
        return row.Envelope;
    }

    private async Task<DocumentEnvelope?> LoadForWriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string documentKind,
        string id,
        RelationalPhysicalWriteOperation operation,
        CancellationToken ct)
    {
        if (WriteInterceptor is not null)
            await WriteInterceptor(RelationalPhysicalWriteExecutionPoint.BeforePrimaryLock, operation, connection, transaction, ct);
        var document = await LoadCoreAsync(connection, documentKind, id, transaction, ct, lockForWrite: true);
        if (WriteInterceptor is not null)
            await WriteInterceptor(RelationalPhysicalWriteExecutionPoint.AfterPrimaryLock, operation, connection, transaction, ct);
        return document;
    }

    private string IdentityPredicate(ExecutableStorageRoute route, bool includeVersion) =>
        dialect.ExactIdentityPredicate(
        [
            new(route.Discriminator.Column.Identifier, null, P("kind")),
            new(route.ScopeKey.Column.Identifier, null, P("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, P("idLookup")),
            new(route.Envelope.Identity.ComparisonKey.Identifier, null, P("idComparison"))
        ]) +
        (includeVersion ? $" AND {Q(route.Envelope.Version.Identifier)} = {P("expectedVersion")}" : string.Empty);

    private string IdentityLookupPredicate(ExecutableStorageRoute route) =>
        dialect.ExactIdentityPredicate(
        [
            new(route.Discriminator.Column.Identifier, null, P("kind")),
            new(route.ScopeKey.Column.Identifier, null, P("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, P("idLookup"))
        ]);

    private void AddIdentityParameters(DbCommand command, ExecutableStorageRoute route, string id, DocumentScopeSelection scope)
    {
        var identity = route.Envelope.Identity.Project(id);
        AddPhysicalParameter(command, "kind", route.Discriminator.Value);
        AddPhysicalParameter(command, "scope", scope.StorageKey!);
        AddPhysicalParameter(command, "idLookup", dialect.ConvertDocumentIdentityLookup(identity.LookupKey));
        AddPhysicalParameter(command, "idComparison", dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey));
    }

    private object?[] EnvelopeValues(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        DocumentScopeSelection scope,
        long version)
    {
        var identity = route.Envelope.Identity.Project(request.Id);
        return
        [
            route.Discriminator.Value,
            scope.StorageKey!,
            dialect.ConvertDocumentIdentityOriginal(identity.OriginalValue),
            dialect.ConvertDocumentIdentityComparison(identity.ComparisonKey),
            dialect.ConvertDocumentIdentityLookup(identity.LookupKey),
            request.SchemaVersion,
            version,
            request.ContentJson
        ];
    }

    private IEnumerable<object?> ProjectedValues(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ExecutableProjectedColumnRoute> columns) =>
        columns.Select(column => dialect.ConvertProjectionValue(
            values[column.Definition.LogicalName],
            column.Definition));

    private void AddValues(DbCommand command, IEnumerable<object?> values)
    {
        var index = 0;
        foreach (var value in values)
            AddPhysicalParameter(command, $"v{index++}", value);
    }

    private async Task ThrowIfIdentityHashCollisionAsync(
        string table,
        IReadOnlyList<IdentityValue> identity,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var parts = identity.Select(item => new RelationalPhysicalIdentityPredicatePart(
            item.Column,
            null,
            P(item.Parameter))).ToArray();
        var hashOnly = dialect.HashOnlyIdentityPredicate(parts);
        if (hashOnly is null)
            return;

        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"SELECT {string.Join(", ", identity.Select(item => Q(item.Column)))} FROM {Q(table)} WHERE {hashOnly};",
            transaction);
        foreach (var item in identity)
            AddPhysicalParameter(command, item.Parameter, item.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;
        var matches = identity.Select((item, index) =>
            dialect.PhysicalIdentityValueEquals(reader.GetValue(index), item.Value));
        if (!matches.All(match => match))
            throw new PhysicalIdentityHashCollisionException(table, identity.Select(item => item.Column).ToArray());
    }

    private IdentityValue[] PrimaryIdentity(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope)
    {
        var identity = route.Envelope.Identity.Project(id);
        return route.PrimaryKey.Columns.Select((column, index) => new IdentityValue(
            column.Identifier,
            $"identity{index}",
            column.Identifier == route.Discriminator.Column.Identifier
                ? route.Discriminator.Value
                : column.Identifier == route.ScopeKey.Column.Identifier
                    ? scope.StorageKey!
                    : column.Identifier == route.Envelope.Identity.LookupKey.Identifier
                        ? dialect.ConvertDocumentIdentityLookup(identity.LookupKey)
                        : throw new InvalidOperationException($"Unsupported primary identity column '{column.Identifier}'."))).ToArray();
    }

    private IdentityValue[] LinkedIdentity(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope)
    {
        var relationship = route.LinkedRelationship!;
        var identity = relationship.Identity.Project(id);
        return route.AuxiliaryKey!.Columns.Select((column, index) => new IdentityValue(
            column.Identifier,
            $"identity{index}",
            column.Identifier == relationship.DocumentKind.Identifier
                ? route.Discriminator.Value
                : column.Identifier == relationship.StorageScope.Identifier
                    ? scope.StorageKey!
                    : column.Identifier == relationship.Identity.LookupKey.Identifier
                        ? dialect.ConvertDocumentIdentityLookup(identity.LookupKey)
                        : throw new InvalidOperationException($"Unsupported linked identity column '{column.Identifier}'."))).ToArray();
    }

    private StorageUnit GetUnit(string documentKind) =>
        manifest.StorageUnits.SingleOrDefault(unit => unit.Identity.Value == documentKind)
        ?? throw new InvalidOperationException($"Document kind '{documentKind}' is not declared by manifest '{manifest.Identity.Value}'.");

    private DocumentScopeSelection ResolveScope(StorageUnit unit, StorageScopeOperation operation) =>
        DocumentStoreScopeResolver.Resolve(unit, Access, operation, scopeObserver);

    private static StorageScopePolicy ScopePolicy(StorageUnit unit) =>
        unit.Tenancy.Kind == TenancyKind.Scoped ? StorageScopePolicy.Scoped : StorageScopePolicy.Global;

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (connection!.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
    }

    private async Task<T> ExecuteAsync<T>(Func<DbConnection, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (sessionFactory is not null)
            return await sessionFactory.ExecuteAsync(action, ct);

        await connectionGate.WaitAsync(ct);
        try
        {
            await EnsureOpenAsync(ct);
            return await action(connection!, ct);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private sealed class UnitOfWork : IDocumentUnitOfWork
    {
        private readonly RelationalPhysicalDocumentStore store;
        private readonly DocumentCommitScope scope;
        private readonly DbTransaction? transaction;
        private readonly RelationalUnitOfWork? ownedUnitOfWork;
        private bool completed;

        public UnitOfWork(
            RelationalPhysicalDocumentStore store,
            DocumentCommitScope scope,
            DbTransaction transaction)
        {
            this.store = store;
            this.scope = scope;
            this.transaction = transaction;
        }

        public UnitOfWork(
            RelationalPhysicalDocumentStore store,
            DocumentCommitScope scope,
            RelationalUnitOfWork ownedUnitOfWork)
        {
            this.store = store;
            this.scope = scope;
            this.ownedUnitOfWork = ownedUnitOfWork;
        }

        public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            ArgumentNullException.ThrowIfNull(request);
            store.ValidateDocumentIdentity(request.Id);
            scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var result = await ExecuteAsync(
                    (currentTransaction, ct) => store.SaveCoreAsync(request, currentTransaction, ct),
                    cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Saved)
                    await AbortNonSuccessAsync(cancellationToken);
                return result;
            }
            catch
            {
                await AbortAsync(CancellationToken.None);
                throw;
            }
        }
        public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            ArgumentNullException.ThrowIfNull(request);
            store.ValidateDocumentIdentity(request.Id);
            scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var result = await ExecuteAsync(
                    (currentTransaction, ct) => store.DeleteCoreAsync(request, currentTransaction, ct),
                    cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Deleted)
                    await AbortNonSuccessAsync(cancellationToken);
                return result;
            }
            catch
            {
                await AbortAsync(CancellationToken.None);
                throw;
            }
        }
        public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            store.ValidateDocumentIdentity(id);
            scope.EnsureIncludes(documentKind);
            return await ExecuteAsync(
                (currentTransaction, ct) => store.LoadCoreAsync(
                    currentTransaction.Connection!, documentKind, id, currentTransaction, ct),
                cancellationToken);
        }
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            if (ownedUnitOfWork is not null)
            {
                try { await ownedUnitOfWork.CommitAsync(cancellationToken); }
                finally { completed = true; }
                return;
            }
            try { await transaction!.CommitAsync(cancellationToken); }
            finally { await CompleteDirectAsync(); }
        }
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            await AbortAsync(cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            if (completed) return;
            if (ownedUnitOfWork is not null)
            {
                completed = true;
                await ownedUnitOfWork.DisposeAsync();
                return;
            }
            try { await transaction!.RollbackAsync(); }
            finally { await CompleteDirectAsync(); }
        }
        private async Task CompleteDirectAsync()
        {
            if (completed) return;
            completed = true;
            await transaction!.DisposeAsync();
            store.connectionGate.Release();
        }
        private async Task AbortAsync(CancellationToken cancellationToken)
        {
            if (completed) return;
            if (ownedUnitOfWork is not null)
            {
                try { await ownedUnitOfWork.RollbackAsync(cancellationToken); }
                finally { completed = true; }
                return;
            }
            try { await transaction!.RollbackAsync(cancellationToken); }
            finally { await CompleteDirectAsync(); }
        }
        private async Task AbortNonSuccessAsync(CancellationToken callerCancellationToken)
        {
            if (store.beforeNonSuccessAbort is not null)
                await store.beforeNonSuccessAbort(callerCancellationToken);
            await AbortAsync(CancellationToken.None);
        }
        private Task<T> ExecuteAsync<T>(
            Func<DbTransaction, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            ownedUnitOfWork is not null
                ? ownedUnitOfWork.Executor.ExecuteAsync(
                    (_, currentTransaction, ct) => operation(currentTransaction, ct),
                    cancellationToken)
                : operation(transaction!, cancellationToken);

        private void EnsureActive()
        {
            if (completed) throw new InvalidOperationException("The document transaction has already completed.");
        }
    }

    private sealed record IdentityValue(string Column, string Parameter, object Value);
}
