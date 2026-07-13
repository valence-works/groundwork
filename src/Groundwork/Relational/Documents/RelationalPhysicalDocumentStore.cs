using System.Data;
using System.Data.Common;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Scoping;
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
    public abstract string Contains(string fieldExpression, string parameterExpression);
    public abstract string StartsWith(string fieldExpression, string parameterExpression);
    public abstract string ApplyOffsetPage(string selectSql, string takeParameter, string skipParameter);
    public abstract string ApplyFirst(string selectSql);
    public virtual string QuerySource(string tableIdentifier, string alias, string? indexIdentifier) =>
        $"{QuoteIdentifier(tableIdentifier)} {alias}";

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
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    public RelationalPhysicalDocumentStore(
        DbConnection connection,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(connection ?? throw new ArgumentNullException(nameof(connection)), null, manifest, routes, dialect, access, scopeObserver)
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
        : this(connection, null, manifest, routes, dialect, access, scopeObserver)
    {
        this.beforeNonSuccessAbort = beforeNonSuccessAbort ??
            throw new ArgumentNullException(nameof(beforeNonSuccessAbort));
    }

    public RelationalPhysicalDocumentStore(
        RelationalSessionFactory sessionFactory,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(null, sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)), manifest, routes, dialect, access, scopeObserver)
    {
    }

    private RelationalPhysicalDocumentStore(
        DbConnection? connection,
        RelationalSessionFactory? sessionFactory,
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes,
        RelationalPhysicalDocumentDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver)
    {
        this.connection = connection;
        this.sessionFactory = sessionFactory;
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
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

    public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async (current, ct) =>
        {
            await using var transaction = await current.BeginTransactionAsync(ct);
            var result = await SaveCoreAsync(request, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Saved)
                await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);

    public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default) =>
        await ExecuteAsync((current, ct) => LoadCoreAsync(current, documentKind, id, null, ct), cancellationToken);

    public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async (current, ct) =>
        {
            await using var transaction = await current.BeginTransactionAsync(ct);
            var result = await DeleteCoreAsync(request, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Deleted)
                await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);

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
            await using var unitOfWork = await sessionFactory.BeginUnitOfWorkAsync(cancellationToken);
            var committed = false;
            try
            {
                var result = await unitOfWork.Executor.ExecuteAsync(action, cancellationToken);
                if (beforeCommit is not null)
                    await beforeCommit(cancellationToken);
                await unitOfWork.CommitAsync(cancellationToken);
                committed = true;
                if (afterCommitBeforeAcknowledgement is not null)
                    await afterCommitBeforeAcknowledgement(cancellationToken);
                return result;
            }
            catch
            {
                if (!committed)
                    await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        return await ExecuteAsync(async (current, ct) =>
        {
            await using var transaction = await current.BeginTransactionAsync(ct);
            var committed = false;
            try
            {
                var result = await action(current, transaction, ct);
                if (beforeCommit is not null)
                    await beforeCommit(ct);
                await transaction.CommitAsync(ct);
                committed = true;
                if (afterCommitBeforeAcknowledgement is not null)
                    await afterCommitBeforeAcknowledgement(ct);
                return result;
            }
            catch
            {
                if (!committed)
                    await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, cancellationToken);
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
    internal string P(string name) => dialect.Parameter(name);
    internal int MaxPhysicalParameters => dialect.MaxParameters;
    internal string Contains(string field, string parameter) => dialect.Contains(field, parameter);
    internal string StartsWith(string field, string parameter) => dialect.StartsWith(field, parameter);
    internal string ApplyOffsetPage(string sql, string take, string skip) => dialect.ApplyOffsetPage(sql, take, skip);
    internal string ApplyFirst(string sql) => dialect.ApplyFirst(sql);
    internal string PhysicalQuerySource(string table, string alias, string? index) =>
        dialect.QuerySource(table, alias, index);
    internal string ExactPhysicalIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) =>
        dialect.ExactIdentityPredicate(parts);
    internal string ExactPhysicalIdentityJoin(IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts) =>
        dialect.ExactIdentityJoin(parts);

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(SaveDocumentRequest request, DbTransaction transaction, CancellationToken ct)
    {
        var unit = GetUnit(request.DocumentKind);
        var route = GetRoute(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Save);
        var existing = await LoadCoreAsync(transaction.Connection!, request.DocumentKind, request.Id, transaction, ct);
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
                return request.ExpectedVersion is null ? DocumentStoreWriteResult.NotFound : DocumentStoreWriteResult.ConcurrencyConflict;
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
        var existing = await LoadCoreAsync(transaction.Connection!, request.DocumentKind, request.Id, transaction, ct);
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
            ? DocumentStoreWriteResult.Deleted
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
        var columns = EnvelopeColumns(route).Concat([RelationalPhysicalStorageColumns.CreatedUtc, RelationalPhysicalStorageColumns.UpdatedUtc]).Concat(projections.Select(column => column.Column.Identifier)).ToArray();
        var parameters = columns.Select((_, index) => P($"v{index}")).ToArray();
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"INSERT INTO {Q(route.PrimaryStorage.Name.Identifier)} ({string.Join(", ", columns.Select(Q))}) VALUES ({string.Join(", ", parameters)});",
            transaction);
        var values = EnvelopeValues(route, request, scope, version).Concat<object?>([createdAt.ToString("O"), updatedAt.ToString("O")])
            .Concat(ProjectedValues(projectedValues, projections));
        AddValues(command, values);
        try
        {
            return await command.ExecuteNonQueryAsync(ct);
        }
        catch (DbException exception) when (dialect.IsUniqueConstraintException(exception))
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
        var projections = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage).ToArray();
        var columns = new[]
        {
            relationship.DocumentKind.Identifier,
            relationship.StorageScope.Identifier,
            relationship.DocumentId.Identifier
        }.Concat(projections.Select(column => column.Column.Identifier)).ToArray();
        var values = new object?[] { route.Discriminator.Value, scope.StorageKey!, request.Id }
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
        await using var command = CreatePhysicalCommand(
            transaction.Connection!,
            $"DELETE FROM {Q(route.LinkedIndexStorage!.Name.Identifier)} WHERE " +
            dialect.ExactIdentityPredicate(
            [
                new(relationship.DocumentKind.Identifier, null, P("kind")),
                new(relationship.StorageScope.Identifier, null, P("scope")),
                new(relationship.DocumentId.Identifier, null, P("id"))
            ]) + ";",
            transaction);
        AddPhysicalParameter(command, "kind", route.Discriminator.Value);
        AddPhysicalParameter(command, "scope", scope.StorageKey!);
        AddPhysicalParameter(command, "id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DocumentEnvelope?> LoadCoreAsync(
        DbConnection currentConnection,
        string documentKind,
        string id,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        var unit = GetUnit(documentKind);
        var route = GetRoute(documentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Load);
        await using var command = CreatePhysicalCommand(
            currentConnection,
            $"SELECT {string.Join(", ", EnvelopeColumns(route).Select(Q))}, {Q(RelationalPhysicalStorageColumns.CreatedUtc)}, {Q(RelationalPhysicalStorageColumns.UpdatedUtc)} " +
            $"FROM {Q(route.PrimaryStorage.Name.Identifier)} WHERE {IdentityPredicate(route, false)};",
            transaction);
        AddIdentityParameters(command, route, id, scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new DocumentEnvelope(
            reader.GetString(0), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7)))
        { Scope = DocumentStoreScopeResolver.ReadScope(reader.GetString(1)) };
    }

    private string IdentityPredicate(ExecutableStorageRoute route, bool includeVersion) =>
        dialect.ExactIdentityPredicate(
        [
            new(route.Discriminator.Column.Identifier, null, P("kind")),
            new(route.ScopeKey.Column.Identifier, null, P("scope")),
            new(route.Envelope.Id.Identifier, null, P("id"))
        ]) +
        (includeVersion ? $" AND {Q(route.Envelope.Version.Identifier)} = {P("expectedVersion")}" : string.Empty);

    private void AddIdentityParameters(DbCommand command, ExecutableStorageRoute route, string id, DocumentScopeSelection scope)
    {
        AddPhysicalParameter(command, "kind", route.Discriminator.Value);
        AddPhysicalParameter(command, "scope", scope.StorageKey!);
        AddPhysicalParameter(command, "id", id);
    }

    private static string[] EnvelopeColumns(ExecutableStorageRoute route) =>
    [
        route.Envelope.DocumentKind.Identifier,
        route.Envelope.StorageScope.Identifier,
        route.Envelope.Id.Identifier,
        route.Envelope.SchemaVersion.Identifier,
        route.Envelope.Version.Identifier,
        route.Envelope.CanonicalJson.Identifier
    ];

    private static object?[] EnvelopeValues(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        DocumentScopeSelection scope,
        long version) =>
    [route.Discriminator.Value, scope.StorageKey!, request.Id, request.SchemaVersion, version, request.ContentJson];

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
        var retained = Enumerable.Range(0, identity.Count).Select(reader.GetString).ToArray();
        if (!retained.SequenceEqual(identity.Select(item => item.Value), StringComparer.Ordinal))
            throw new PhysicalIdentityHashCollisionException(table, identity.Select(item => item.Column).ToArray());
    }

    private static IdentityValue[] PrimaryIdentity(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope) =>
        route.PrimaryKey.Columns.Select((column, index) => new IdentityValue(
            column.Identifier,
            $"identity{index}",
            column.Identifier == route.Discriminator.Column.Identifier
                ? route.Discriminator.Value
                : column.Identifier == route.ScopeKey.Column.Identifier
                    ? scope.StorageKey!
                    : column.Identifier == route.Envelope.Id.Identifier
                        ? id
                        : throw new InvalidOperationException($"Unsupported primary identity column '{column.Identifier}'."))).ToArray();

    private static IdentityValue[] LinkedIdentity(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope)
    {
        var relationship = route.LinkedRelationship!;
        return route.AuxiliaryKey!.Columns.Select((column, index) => new IdentityValue(
            column.Identifier,
            $"identity{index}",
            column.Identifier == relationship.DocumentKind.Identifier
                ? route.Discriminator.Value
                : column.Identifier == relationship.StorageScope.Identifier
                    ? scope.StorageKey!
                    : column.Identifier == relationship.DocumentId.Identifier
                        ? id
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

    private sealed record IdentityValue(string Column, string Parameter, string Value);
}
