using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Transactions;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

public class RelationalDocumentStore : IDocumentStore
{
    private readonly DbConnection? connection;
    private readonly RelationalSessionFactory? sessionFactory;
    private readonly StorageManifest manifest;
    private readonly RelationalDocumentStoreDialect dialect;
    private readonly IReadOnlyDictionary<string, DocumentIdentityBinding> identityBindings;
    private readonly DocumentStoreAccess access;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    public DocumentStoreAccess Access => access;

    internal RelationalDocumentStore(
        DbConnection connection,
        StorageManifest manifest,
        RelationalDocumentStoreDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        identityBindings = DocumentIdentityBinding.Bind(manifest);
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
    }

    internal RelationalDocumentStore(
        RelationalSessionFactory sessionFactory,
        StorageManifest manifest,
        RelationalDocumentStoreDialect dialect,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
    {
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        identityBindings = DocumentIdentityBinding.Bind(manifest);
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
    }

    public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        dialect.ValidateDocumentIdentity(request.Id);
        var unit = GetUnit(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Save);
        return await ExecuteWithConnectionAsync(async (currentConnection, ct) =>
        {
            await using var transaction = await currentConnection.BeginTransactionAsync(ct);
            var result = await SaveCoreAsync(request, unit, scope, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Saved)
                await transaction.CommitAsync(ct);

            return result;
        }, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(
        SaveDocumentRequest request,
        StorageUnit unit,
        DocumentScopeSelection scope,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var identity = identityBindings[request.DocumentKind];
        var requestedIdentity = identity.Project(request.Id);
        var existing = await LoadCoreAsync(
            transaction.Connection!,
            request.DocumentKind,
            requestedIdentity,
            identity,
            scope,
            transaction,
            ct);
        if (existing is not null && !string.Equals(existing.Envelope.Id, request.Id, StringComparison.Ordinal))
            return DocumentStoreWriteResult.IdentityConflict(existing.Envelope.Id);

        if (existing is not null && request.ExpectedVersion is not null && existing.Envelope.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        // ExpectedVersion 0 is the create-only claim ("no document exists yet") and falls through to the insert
        // path, where a concurrent duplicate insert surfaces as a duplicate-key ConcurrencyConflict inside this
        // transaction. Any other expected version can never match an absent document.
        if (existing is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var version = existing is null ? 1 : existing.Envelope.Version + 1;
        var createdAt = existing?.Envelope.CreatedAt ?? now;

        if (existing is null)
        {
            try
            {
                var inserted = await InsertDocumentAsync(request, requestedIdentity, scope, version, createdAt, now, transaction, ct);
                if (!inserted)
                {
                    var retained = await LoadCoreAsync(
                        transaction.Connection!,
                        request.DocumentKind,
                        requestedIdentity,
                        identity,
                        scope,
                        transaction,
                        ct);
                    return retained is not null && !string.Equals(retained.Envelope.Id, request.Id, StringComparison.Ordinal)
                        ? DocumentStoreWriteResult.IdentityConflict(retained.Envelope.Id)
                        : DocumentStoreWriteResult.ConcurrencyConflict;
                }
            }
            catch (DbException exception) when (dialect.IsDuplicateDocumentKeyException(exception))
            {
                var retained = await LoadCoreAsync(
                    transaction.Connection!,
                    request.DocumentKind,
                    requestedIdentity,
                    identity,
                    scope,
                    transaction,
                    ct);
                return retained is not null && !string.Equals(retained.Envelope.Id, request.Id, StringComparison.Ordinal)
                    ? DocumentStoreWriteResult.IdentityConflict(retained.Envelope.Id)
                    : DocumentStoreWriteResult.ConcurrencyConflict;
            }
        }
        else
        {
            var updated = await UpdateDocumentAsync(
                request,
                requestedIdentity,
                existing.Envelope.Id,
                scope,
                version,
                now,
                transaction,
                ct);
            if (!updated)
                return WriteMissResult(request.ExpectedVersion);
        }

        try
        {
            var authoritativeId = existing?.Envelope.Id ?? request.Id;
            await DeleteIndexesAsync(request.DocumentKind, authoritativeId, scope, transaction, ct);
            await InsertIndexesAsync(unit, authoritativeId, request.ContentJson, scope, transaction, ct);
            await RefreshPhysicalizedAsync(unit, authoritativeId, version, request.ContentJson, scope, transaction, ct);
        }
        catch (DbException exception) when (dialect.IsUniqueIndexException(exception))
        {
            return DocumentStoreWriteResult.ConcurrencyConflict;
        }
        catch (DbException exception) when (dialect.IsWriteDependencyException(exception))
        {
            return WriteMissResult(request.ExpectedVersion);
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

    public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
    {
        dialect.ValidateDocumentIdentity(id);
        var unit = GetUnit(documentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Load);
        var identity = identityBindings[documentKind];
        var requestedIdentity = identity.Project(id);
        var stored = await ExecuteWithConnectionAsync(
            (currentConnection, ct) => LoadCoreAsync(
                currentConnection,
                documentKind,
                requestedIdentity,
                identity,
                scope,
                null,
                ct),
            cancellationToken);
        return stored?.Envelope;
    }

    public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        dialect.ValidateDocumentIdentity(request.Id);
        var unit = GetUnit(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Delete);
        return await ExecuteWithConnectionAsync(async (currentConnection, ct) =>
        {
            await using var transaction = await currentConnection.BeginTransactionAsync(ct);
            var result = await DeleteCoreAsync(request, unit, scope, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Deleted)
                await transaction.CommitAsync(ct);

            return result;
        }, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(
        DeleteDocumentRequest request,
        StorageUnit unit,
        DocumentScopeSelection scope,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var identity = identityBindings[request.DocumentKind];
        var requestedIdentity = identity.Project(request.Id);
        var existing = await LoadCoreAsync(
            transaction.Connection!,
            request.DocumentKind,
            requestedIdentity,
            identity,
            scope,
            transaction,
            ct);
        if (existing is null)
            return DocumentStoreWriteResult.NotFound;

        if (request.ExpectedVersion is not null && existing.Envelope.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        await using var command = CreateCommand(dialect.DeleteDocumentCommandSql(request.ExpectedVersion is not null), transaction);
        AddParameter(command, "kind", request.DocumentKind);
        AddParameter(command, "scope", scope.StorageKey!);
        AddIdentityParameters(command, requestedIdentity);
        AddParameter(command, "authoritativeId", existing.Envelope.Id);
        if (request.ExpectedVersion is not null)
            AddParameter(command, "expectedVersion", request.ExpectedVersion.Value);

        var deletedRows = await command.ExecuteNonQueryAsync(ct);
        if (deletedRows == 0)
            return WriteMissResult(request.ExpectedVersion);

        await DeleteIndexesAsync(request.DocumentKind, existing.Envelope.Id, scope, transaction, ct);
        await DeletePhysicalizedAsync(unit, existing.Envelope.Id, scope, transaction, ct);

        return DocumentStoreWriteResult.Deleted(existing.Envelope.Id);
    }

    public TransactionBoundary TransactionBoundary => TransactionBoundary.CrossUnitAtomic;

    public async Task<IDocumentUnitOfWork> BeginAsync(DocumentCommitScope scope, CancellationToken cancellationToken = default)
    {
        var units = scope.Kinds.Select(GetUnit).ToArray();
        if (units.Select(ScopePolicy).Distinct().Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));

        var selections = units
            .Select(unit => ResolveScope(unit, StorageScopeOperation.BeginUnitOfWork))
            .ToArray();
        if (selections.Select(x => x.StorageKey).Distinct(StringComparer.Ordinal).Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(GetUnit(scope.Kinds[0])));

        if (sessionFactory is not null)
            return new RelationalDocumentUnitOfWork(
                this,
                scope,
                await sessionFactory.BeginUnitOfWorkAsync(cancellationToken));

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);
            var transaction = await connection!.BeginTransactionAsync(cancellationToken);
            return new RelationalDocumentUnitOfWork(this, scope, transaction);
        }
        catch
        {
            connectionGate.Release();
            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(DocumentStoreQuery query, CancellationToken cancellationToken = default)
    {
#pragma warning disable GW0004
        var result = await QueryAsync(
            new PortableDocumentQuery(
                query.DocumentKind,
                [QueryClause.Of(QueryComparison.Equal(query.IndexName, query.Value))],
                skip: query.Skip,
                take: query.Take ?? 100),
            cancellationToken);
#pragma warning restore GW0004
        return result.Documents;
    }

    public async Task<DocumentQueryResult> QueryAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var scope = ResolveQueryScope(unit);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, scope.StorageKey, out var whereSql, out var orderSql);

        return await ExecuteWithConnectionAsync(async (currentConnection, ct) =>
        {
            var total = await CountClosedAsync(currentConnection, translator, whereSql, ct);
            if (total == 0 || query.Take == 0)
                return new DocumentQueryResult(Array.Empty<DocumentEnvelope>(), total);

            await using var command = CreateCommand(currentConnection, translator.SelectSql(whereSql, orderSql, query.Skip ?? 0, query.Take));
            AddClosedQueryParameters(command, translator.Parameters);

            var documents = new List<DocumentEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(ReadEnvelope(reader));

            return new DocumentQueryResult(documents, total);
        }, cancellationToken);
    }

    public async Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var scope = ResolveQueryScope(unit);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, scope.StorageKey, out var whereSql, out var orderSql);

        return await ExecuteWithConnectionAsync(async (currentConnection, ct) =>
        {
            await using var command = CreateCommand(currentConnection, translator.SelectSql(whereSql, orderSql, 0, 1));
            AddClosedQueryParameters(command, translator.Parameters);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadEnvelope(reader) : null;
        }, cancellationToken);
    }

    public async Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var scope = ResolveQueryScope(unit);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, scope.StorageKey, out var whereSql, out _);

        return await ExecuteWithConnectionAsync(
            async (currentConnection, ct) => await CountClosedAsync(currentConnection, translator, whereSql, ct) > 0,
            cancellationToken);
    }

    private async Task<long> CountClosedAsync(DbConnection currentConnection, RelationalClosedQueryTranslator translator, string whereSql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(currentConnection, translator.CountSql(whereSql));
        AddClosedQueryParameters(command, translator.WhereParameters);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar);
    }

    private void AddClosedQueryParameters(DbCommand command, IEnumerable<KeyValuePair<string, object>> values)
    {
        foreach (var (name, value) in values)
            AddParameter(command, name, value);
    }

    private async Task<bool> InsertDocumentAsync(
        SaveDocumentRequest request,
        Groundwork.Core.Text.PortableStringIdentityProjection identity,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.InsertDocumentSql, transaction);
        AddDocumentParameters(command, request, identity, request.Id, scope, version, createdAt, updatedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> UpdateDocumentAsync(
        SaveDocumentRequest request,
        Groundwork.Core.Text.PortableStringIdentityProjection identity,
        string authoritativeId,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset updatedAt,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.UpdateDocumentCommandSql(request.ExpectedVersion is not null), transaction);
        AddDocumentParameters(command, request, identity, authoritativeId, scope, version, updatedAt);
        if (request.ExpectedVersion is not null)
            AddParameter(command, "expectedVersion", request.ExpectedVersion.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private void AddDocumentParameters(
        DbCommand command,
        SaveDocumentRequest request,
        Groundwork.Core.Text.PortableStringIdentityProjection identity,
        string authoritativeId,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        AddDocumentParameters(command, request, identity, authoritativeId, scope, version);
        AddParameter(command, "createdUtc", createdAt.ToString("O"));
        AddParameter(command, "updatedUtc", updatedAt.ToString("O"));
    }

    private void AddDocumentParameters(
        DbCommand command,
        SaveDocumentRequest request,
        Groundwork.Core.Text.PortableStringIdentityProjection identity,
        string authoritativeId,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset updatedAt)
    {
        AddDocumentParameters(command, request, identity, authoritativeId, scope, version);
        AddParameter(command, "updatedUtc", updatedAt.ToString("O"));
    }

    private void AddDocumentParameters(
        DbCommand command,
        SaveDocumentRequest request,
        Groundwork.Core.Text.PortableStringIdentityProjection identity,
        string authoritativeId,
        DocumentScopeSelection scope,
        long version)
    {
        AddParameter(command, "kind", request.DocumentKind);
        AddParameter(command, "scope", scope.StorageKey!);
        AddParameter(command, "id", request.Id);
        AddIdentityParameters(command, identity);
        AddParameter(command, "authoritativeId", authoritativeId);
        AddParameter(command, "schemaVersion", request.SchemaVersion);
        AddParameter(command, "version", version);
        AddParameter(command, "content", request.ContentJson);
    }

    private async Task<StoredDocument?> LoadCoreAsync(
        DbConnection currentConnection,
        string documentKind,
        Groundwork.Core.Text.PortableStringIdentityProjection requestedIdentity,
        DocumentIdentityBinding identity,
        DocumentScopeSelection scope,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(currentConnection, dialect.LoadDocumentSql, transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "scope", scope.StorageKey!);
        AddParameter(command, "idLookupKey", requestedIdentity.LookupKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var stored = new StoredDocument(ReadEnvelope(reader), reader.GetString(8), reader.GetString(9));
        identity.EnsureLookupIntegrity(
            documentKind,
            requestedIdentity,
            stored.Envelope.Id,
            stored.ComparisonKey,
            stored.LookupKey);
        return stored;
    }

    private void AddIdentityParameters(
        DbCommand command,
        Groundwork.Core.Text.PortableStringIdentityProjection identity)
    {
        AddParameter(command, "idComparisonKey", identity.ComparisonKey);
        AddParameter(command, "idLookupKey", identity.LookupKey);
    }

    private async Task DeleteIndexesAsync(string documentKind, string id, DocumentScopeSelection scope, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.DeleteIndexesSql, transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "scope", scope.StorageKey!);
        AddParameter(command, "id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertIndexesAsync(StorageUnit unit, string id, string contentJson, DocumentScopeSelection scope, DbTransaction transaction, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(contentJson);
        foreach (var index in unit.Indexes)
        {
            if (!TryGetIndexValue(document.RootElement, index, out var value))
                continue;

            await using var command = CreateCommand(dialect.InsertIndexSql, transaction);
            AddParameter(command, "kind", unit.Identity.Value);
            AddParameter(command, "scope", scope.StorageKey!);
            AddParameter(command, "index", index.Identity);
            AddParameter(command, "value", value);
            AddParameter(command, "documentId", id);
            AddParameter(command, "isUnique", dialect.Boolean(index.IsUnique));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task RefreshPhysicalizedAsync(
        StorageUnit unit,
        string id,
        long version,
        string contentJson,
        DocumentScopeSelection scope,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var fields = PhysicalizationProjection.EligibleFields(unit);
        if (fields.Count == 0)
            return;

        await DeletePhysicalizedAsync(unit, id, scope, transaction, cancellationToken);

        using var document = JsonDocument.Parse(contentJson);
        var table = RelationalPhysicalizationNames.TableName(unit);
        var columnNames = fields.Select(RelationalPhysicalizationNames.ColumnName).ToList();
        await using var command = CreateCommand(dialect.InsertPhysicalizedSql(table, columnNames), transaction);
        AddParameter(command, "kind", unit.Identity.Value);
        AddParameter(command, "scope", scope.StorageKey!);
        AddParameter(command, "id", id);
        AddParameter(command, "version", version);

        for (var index = 0; index < fields.Count; index++)
        {
            var value = RelationalPhysicalizationValues.TryRead(document.RootElement, fields[index].Path, out var physicalizedValue)
                ? physicalizedValue
                : null;
            AddParameter(command, $"physicalized{index}", value is null ? DBNull.Value : value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeletePhysicalizedAsync(StorageUnit unit, string id, DocumentScopeSelection scope, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (PhysicalizationProjection.EligibleFields(unit).Count == 0)
            return;

        await using var command = CreateCommand(dialect.DeletePhysicalizedSql(RelationalPhysicalizationNames.TableName(unit)), transaction);
        AddParameter(command, "kind", unit.Identity.Value);
        AddParameter(command, "scope", scope.StorageKey!);
        AddParameter(command, "id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(string commandText, DbTransaction transaction) =>
        CreateCommand(transaction.Connection!, commandText, transaction);

    private static DbCommand CreateCommand(DbConnection connection, string commandText, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }

    private void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = dialect.Parameter(name);
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private StorageUnit GetUnit(string documentKind) =>
        manifest.StorageUnits.SingleOrDefault(unit => unit.Identity.Value == documentKind)
        ?? throw new InvalidOperationException($"Document kind '{documentKind}' is not declared by manifest '{manifest.Identity}'.");

    private DocumentScopeSelection ResolveScope(StorageUnit unit, StorageScopeOperation operation) =>
        DocumentStoreScopeResolver.Resolve(unit, access, operation, scopeObserver);

    private DocumentScopeSelection ResolveQueryScope(StorageUnit unit) =>
        DocumentStoreScopeResolver.Resolve(unit, access, StorageScopeOperation.Query, scopeObserver, allowAcrossScopes: true);

    private static StorageScopePolicy ScopePolicy(StorageUnit unit) =>
        unit.Tenancy.Kind == TenancyKind.Scoped
            ? StorageScopePolicy.Scoped
            : StorageScopePolicy.Global;

    private static bool TryGetIndexValue(JsonElement root, IndexDeclaration index, out string value) =>
        RelationalIndexValues.TryGetIndexValue(root, index.Fields, out value);

    private static DocumentEnvelope ReadEnvelope(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)))
        {
            Scope = DocumentStoreScopeResolver.ReadScope(reader.GetString(1))
        };

    private static DocumentStoreWriteResult WriteMissResult(long? expectedVersion) =>
        expectedVersion is null
            ? DocumentStoreWriteResult.NotFound
            : DocumentStoreWriteResult.ConcurrencyConflict;

    private sealed record StoredDocument(
        DocumentEnvelope Envelope,
        string ComparisonKey,
        string LookupKey);

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection!.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
    }

    private async Task<T> ExecuteWithConnectionAsync<T>(
        Func<DbConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (sessionFactory is not null)
            return await sessionFactory.ExecuteAsync(operation, cancellationToken);

        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);
            return await operation(connection!, cancellationToken);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private sealed class RelationalDocumentUnitOfWork : IDocumentUnitOfWork
    {
        private readonly RelationalDocumentStore store;
        private readonly DocumentCommitScope scope;
        private readonly DbTransaction? transaction;
        private readonly RelationalUnitOfWork? ownedUnitOfWork;
        private bool completed;

        public RelationalDocumentUnitOfWork(
            RelationalDocumentStore store,
            DocumentCommitScope scope,
            DbTransaction transaction)
        {
            this.store = store;
            this.scope = scope;
            this.transaction = transaction;
        }

        public RelationalDocumentUnitOfWork(
            RelationalDocumentStore store,
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
            store.dialect.ValidateDocumentIdentity(request.Id);
            this.scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var unit = store.GetUnit(request.DocumentKind);
                var selection = store.ResolveScope(unit, StorageScopeOperation.Save);
                var result = await ExecuteAsync(
                    (currentTransaction, ct) => store.SaveCoreAsync(request, unit, selection, currentTransaction, ct),
                    cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Saved)
                    await AbortAsync(CancellationToken.None);
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
            store.dialect.ValidateDocumentIdentity(request.Id);
            this.scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var unit = store.GetUnit(request.DocumentKind);
                var selection = store.ResolveScope(unit, StorageScopeOperation.Delete);
                var result = await ExecuteAsync(
                    (currentTransaction, ct) => store.DeleteCoreAsync(request, unit, selection, currentTransaction, ct),
                    cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Deleted)
                    await AbortAsync(CancellationToken.None);
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
            store.dialect.ValidateDocumentIdentity(id);
            this.scope.EnsureIncludes(documentKind);
            var unit = store.GetUnit(documentKind);
            var scope = store.ResolveScope(unit, StorageScopeOperation.Load);
            var identity = store.identityBindings[documentKind];
            var requestedIdentity = identity.Project(id);
            var stored = await ExecuteAsync(
                (currentTransaction, ct) => store.LoadCoreAsync(
                    currentTransaction.Connection!,
                    documentKind,
                    requestedIdentity,
                    identity,
                    scope,
                    currentTransaction,
                    ct),
                cancellationToken);
            return stored?.Envelope;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            if (ownedUnitOfWork is not null)
            {
                try
                {
                    await ownedUnitOfWork.CommitAsync(cancellationToken);
                }
                finally
                {
                    completed = true;
                }
                return;
            }

            try
            {
                await transaction!.CommitAsync(cancellationToken);
            }
            finally
            {
                await CompleteAsync();
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            await AbortAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (completed)
                return;

            if (ownedUnitOfWork is not null)
            {
                completed = true;
                await ownedUnitOfWork.DisposeAsync();
                return;
            }

            try
            {
                await transaction!.RollbackAsync();
            }
            catch
            {
                // Best-effort rollback when disposed without an explicit commit/rollback.
            }
            finally
            {
                await CompleteAsync();
            }
        }

        private async Task CompleteAsync()
        {
            if (completed)
                return;

            completed = true;
            await transaction!.DisposeAsync();
            store.connectionGate.Release();
        }

        private async Task AbortAsync(CancellationToken cancellationToken)
        {
            if (completed)
                return;

            if (ownedUnitOfWork is not null)
            {
                try
                {
                    await ownedUnitOfWork.RollbackAsync(cancellationToken);
                }
                finally
                {
                    completed = true;
                }
                return;
            }

            try
            {
                await transaction!.RollbackAsync(cancellationToken);
            }
            finally
            {
                await CompleteAsync();
            }
        }

        private Task<T> ExecuteAsync<T>(
            Func<DbTransaction, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            ownedUnitOfWork is not null
                ? ownedUnitOfWork.Executor.ExecuteAsync((_, currentTransaction, ct) => operation(currentTransaction, ct), cancellationToken)
                : operation(transaction!, cancellationToken);

        private void EnsureActive()
        {
            if (completed)
                throw new InvalidOperationException("The document transaction has already been committed or rolled back.");
        }
    }
}
