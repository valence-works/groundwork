using System.Globalization;
using System.Text.RegularExpressions;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Transactions;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Documents.Store;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.UnitOfWork;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

public sealed class MongoDbDocumentStore : IDocumentStore
{
    private readonly IMongoDatabase database;
    private readonly StorageManifest manifest;
    private readonly IReadOnlyDictionary<string, DocumentIdentityBinding> identityBindings;
    private readonly DocumentStoreAccess access;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly Func<CancellationToken, Task<bool>> supportsTransactionsAsync;
    private readonly Func<CancellationToken, Task<IClientSessionHandle>> startSessionAsync;

    public DocumentStoreAccess Access => access;

    internal MongoDbDocumentStore(
        IMongoDatabase database,
        StorageManifest manifest,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null)
        : this(database, manifest, access, scopeObserver, null, null)
    {
    }

    internal MongoDbDocumentStore(
        IMongoDatabase database,
        StorageManifest manifest,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        Func<CancellationToken, Task<bool>>? supportsTransactionsAsync,
        Func<CancellationToken, Task<IClientSessionHandle>>? startSessionAsync)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        identityBindings = DocumentIdentityBinding.Bind(manifest);
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        this.supportsTransactionsAsync = supportsTransactionsAsync ??
            (ct => MongoDbTransactionTopology.SupportsTransactionsAsync(this.database, ct));
        this.startSessionAsync = startSessionAsync ??
            (ct => this.database.Client.StartSessionAsync(cancellationToken: ct));
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
    }

    public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Save);
        return await SaveCoreAsync(unit, request, scope, session: null, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(StorageUnit unit, SaveDocumentRequest request, DocumentScopeSelection scope, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        var collection = GetCollection(unit);
        var identity = identityBindings[request.DocumentKind];
        var requestedIdentity = identity.Project(request.Id);
        var existing = await LoadCoreAsync(unit, requestedIdentity, identity, scope, session, cancellationToken);

        if (existing is not null && !string.Equals(existing.Id, request.Id, StringComparison.Ordinal))
            return DocumentStoreWriteResult.IdentityConflict(existing.Id);

        if (existing is not null && request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        // ExpectedVersion 0 is the create-only claim ("no document exists yet") and falls through to the insert
        // path, where a concurrent duplicate insert surfaces as a duplicate-key ConcurrencyConflict. Any other
        // expected version can never match an absent document.
        if (existing is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var version = existing is null ? 1 : existing.Version + 1;
        var createdAt = existing?.CreatedAt ?? now;
        var document = CreateDocument(unit, request, requestedIdentity, scope, version, createdAt, now);

        if (existing is null)
        {
            try
            {
                await InsertOneAsync(collection, session, document, cancellationToken);
            }
            catch (MongoWriteException exception) when (IsDuplicateKey(exception))
            {
                var retained = await LoadCoreAsync(unit, requestedIdentity, identity, scope, session, cancellationToken);
                return retained is not null && !string.Equals(retained.Id, request.Id, StringComparison.Ordinal)
                    ? DocumentStoreWriteResult.IdentityConflict(retained.Id)
                    : DocumentStoreWriteResult.ConcurrencyConflict;
            }
        }
        else
        {
            var filter = request.ExpectedVersion is null
                ? AuthoritativeIdentityFilter(scope.StorageKey!, requestedIdentity, existing.Id)
                : AuthoritativeIdentityFilter(scope.StorageKey!, requestedIdentity, existing.Id) & Builders<BsonDocument>.Filter.Eq("version", request.ExpectedVersion.Value);
            ReplaceOneResult result;
            try
            {
                result = await ReplaceOneAsync(collection, session, filter, document, cancellationToken);
            }
            catch (MongoWriteException exception) when (IsDuplicateKey(exception))
            {
                return DocumentStoreWriteResult.ConcurrencyConflict;
            }

            if (result.MatchedCount == 0)
            {
                if (request.ExpectedVersion is null)
                    return DocumentStoreWriteResult.NotFound;

                return await LoadCoreAsync(unit, requestedIdentity, identity, scope, session, cancellationToken) is null
                    ? DocumentStoreWriteResult.NotFound
                    : DocumentStoreWriteResult.ConcurrencyConflict;
            }
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
        var unit = GetUnit(documentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Load);
        var identity = identityBindings[documentKind];
        return await LoadCoreAsync(unit, identity.Project(id), identity, scope, session: null, cancellationToken);
    }

    public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(request.DocumentKind);
        var scope = ResolveScope(unit, StorageScopeOperation.Delete);
        return await DeleteCoreAsync(unit, request, scope, session: null, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(StorageUnit unit, DeleteDocumentRequest request, DocumentScopeSelection scope, IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        var collection = GetCollection(unit);
        var identity = identityBindings[request.DocumentKind];
        var requestedIdentity = identity.Project(request.Id);
        var existing = await LoadCoreAsync(unit, requestedIdentity, identity, scope, session, cancellationToken);
        if (existing is null)
            return DocumentStoreWriteResult.NotFound;

        if (request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        var filter = request.ExpectedVersion is null
            ? AuthoritativeIdentityFilter(scope.StorageKey!, requestedIdentity, existing.Id)
            : AuthoritativeIdentityFilter(scope.StorageKey!, requestedIdentity, existing.Id) & Builders<BsonDocument>.Filter.Eq("version", request.ExpectedVersion.Value);

        var deleted = await FindOneAndDeleteAsync(collection, session, filter, cancellationToken);
        if (deleted is not null)
            return DocumentStoreWriteResult.Deleted(deleted["id_original"].AsString);

        return await LoadCoreAsync(unit, requestedIdentity, identity, scope, session, cancellationToken) is null
            ? DocumentStoreWriteResult.NotFound
            : DocumentStoreWriteResult.ConcurrencyConflict;
    }

    public TransactionBoundary TransactionBoundary =>
        MongoDbTransactionTopology.IsKnownTransactionCapable(database.Client.Cluster.Description)
            ? TransactionBoundary.CrossUnitAtomic
            : TransactionBoundary.PerOperation;

    public async Task<IDocumentUnitOfWork> BeginAsync(DocumentCommitScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var units = scope.Kinds.Select(GetUnit).ToArray();
        if (units.Select(ScopePolicy).Distinct().Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));

        var selections = units.Select(unit => ResolveScope(unit, StorageScopeOperation.BeginUnitOfWork)).ToArray();
        if (selections.Select(x => x.StorageKey).Distinct(StringComparer.Ordinal).Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(GetUnit(scope.Kinds[0])));

        var clusterType = database.Client.Cluster.Description.Type;
        if (!await supportsTransactionsAsync(cancellationToken))
            throw new UnsupportedAtomicCommitException(
                scope.Kinds,
                $"MongoDB multi-document transactions require a replica set or sharded cluster, but the connected deployment is '{clusterType}'.");

        var session = await startSessionAsync(cancellationToken);
        try
        {
            session.StartTransaction();
        }
        catch (NotSupportedException exception)
        {
            session.Dispose();
            throw new UnsupportedAtomicCommitException(scope.Kinds, exception.Message);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return new MongoDocumentUnitOfWork(this, session, scope);
    }

    private static Task InsertOneAsync(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, BsonDocument document, CancellationToken cancellationToken) =>
        session is null
            ? collection.InsertOneAsync(document, cancellationToken: cancellationToken)
            : collection.InsertOneAsync(session, document, cancellationToken: cancellationToken);

    private static Task<ReplaceOneResult> ReplaceOneAsync(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, FilterDefinition<BsonDocument> filter, BsonDocument document, CancellationToken cancellationToken) =>
        session is null
            ? collection.ReplaceOneAsync(filter, document, cancellationToken: cancellationToken)
            : collection.ReplaceOneAsync(session, filter, document, cancellationToken: cancellationToken);

    private static async Task<BsonDocument?> FindOneAndDeleteAsync(IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, FilterDefinition<BsonDocument> filter, CancellationToken cancellationToken) =>
        session is null
            ? await collection.FindOneAndDeleteAsync(filter, cancellationToken: cancellationToken)
            : await collection.FindOneAndDeleteAsync(session, filter, cancellationToken: cancellationToken);

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
        var collection = GetCollection(unit);
        var filter = BuildFilter(unit, query, scope);

        var total = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (total == 0 || query.Take == 0)
            return new DocumentQueryResult(Array.Empty<DocumentEnvelope>(), total);

        var find = collection.Find(filter).Sort(BuildSort(unit, query.Order)).Skip(query.Skip ?? 0);
        if (query.Take is { } take)
            find = find.Limit(take);

        var documents = await find.ToListAsync(cancellationToken);
        return new DocumentQueryResult(documents.Select(document => ReadEnvelope(unit, document)).ToList(), total);
    }

    public async Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var scope = ResolveQueryScope(unit);
        var collection = GetCollection(unit);
        var filter = BuildFilter(unit, query, scope);

        var document = await collection
            .Find(filter)
            .Sort(BuildSort(unit, query.Order))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        return document is null ? null : ReadEnvelope(unit, document);
    }

    public async Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var scope = ResolveQueryScope(unit);
        var collection = GetCollection(unit);
        var filter = BuildFilter(unit, query, scope);

        return await collection.Find(filter).Limit(1).AnyAsync(cancellationToken);
    }

    private FilterDefinition<BsonDocument> BuildFilter(StorageUnit unit, PortableDocumentQuery query, DocumentScopeSelection scope)
    {
        var filters = new List<FilterDefinition<BsonDocument>>();

        foreach (var clause in query.Clauses)
            filters.Add(BuildClauseFilter(unit, clause));

        if (scope.StorageKey is not null)
            filters.Add(Builders<BsonDocument>.Filter.Eq("storage_scope", scope.StorageKey));

        return filters.Count == 0 ? Builders<BsonDocument>.Filter.Empty : Builders<BsonDocument>.Filter.And(filters);
    }

    private FilterDefinition<BsonDocument> BuildClauseFilter(StorageUnit unit, QueryClause clause)
    {
        if (clause.Comparisons.Count == 0)
            return MatchNone;

        var comparisons = clause.Comparisons.Select(comparison => BuildComparisonFilter(unit, comparison)).ToList();
        return Builders<BsonDocument>.Filter.Or(comparisons);
    }

    private FilterDefinition<BsonDocument> BuildComparisonFilter(StorageUnit unit, QueryComparison comparison)
    {
        var index = ClosedQueryIndexResolver.ResolveComparisonIndex(unit, comparison.IndexName, comparison.Operator);
        var path = IndexPath(unit, index);

        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => BuildEqualFilter(index, path, comparison),
            QueryComparisonOperator.In => BuildInFilter(index, path, comparison),
            QueryComparisonOperator.Contains => BuildContainsFilter(path, comparison),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison.Operator, "Unsupported operator.")
        };
    }

    private static FilterDefinition<BsonDocument> BuildEqualFilter(IndexDeclaration index, string path, QueryComparison comparison)
    {
        var value = comparison.Values.Count > 0 ? comparison.Values[0] : null;
        return value is null
            ? Builders<BsonDocument>.Filter.Eq(path, BsonNull.Value)
            : Builders<BsonDocument>.Filter.Eq(path, ToBsonValue(index.ValueKind, value));
    }

    private static FilterDefinition<BsonDocument> BuildInFilter(IndexDeclaration index, string path, QueryComparison comparison)
    {
        var nonNull = comparison.Values.Where(value => value is not null).Cast<string>().ToList();
        var hasNull = comparison.Values.Any(value => value is null);

        if (nonNull.Count == 0)
            return hasNull ? Builders<BsonDocument>.Filter.Eq(path, BsonNull.Value) : MatchNone;

        var membership = Builders<BsonDocument>.Filter.In(path, nonNull.Select(value => ToBsonValue(index.ValueKind, value)));
        return hasNull
            ? Builders<BsonDocument>.Filter.Or(membership, Builders<BsonDocument>.Filter.Eq(path, BsonNull.Value))
            : membership;
    }

    private static FilterDefinition<BsonDocument> BuildContainsFilter(string path, QueryComparison comparison)
    {
        var value = comparison.Values.Count > 0 ? comparison.Values[0] : null;
        if (value is null)
            throw new InvalidOperationException("Contains requires a non-null value.");

        var pattern = new BsonRegularExpression(Regex.Escape(value), "i");
        return Builders<BsonDocument>.Filter.Regex(path, pattern);
    }

    private static SortDefinition<BsonDocument> BuildSort(StorageUnit unit, QueryOrder? order)
    {
        if (order is null)
            return Builders<BsonDocument>.Sort.Ascending("_id");

        var index = ClosedQueryIndexResolver.ResolveOrderIndex(unit, order.IndexName);
        var path = IndexPath(unit, index);
        var primary = order.Descending
            ? Builders<BsonDocument>.Sort.Descending(path)
            : Builders<BsonDocument>.Sort.Ascending(path);

        return Builders<BsonDocument>.Sort.Combine(primary, Builders<BsonDocument>.Sort.Ascending("_id"));
    }

    private static string IndexPath(StorageUnit unit, IndexDeclaration index)
    {
        var physicalizedField = PhysicalizationProjection.EligibleFields(unit).SingleOrDefault(field => field.Name == index.Identity);
        return physicalizedField is null
            ? $"content.{index.Fields[0].Path}"
            : $"physicalized.{MongoDbGroundworkNames.PhysicalizedFieldName(physicalizedField)}";
    }

    private static FilterDefinition<BsonDocument> MatchNone { get; } =
        Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true);

    private async Task<DocumentEnvelope?> LoadCoreAsync(
        StorageUnit unit,
        PortableStringIdentityProjection requestedIdentity,
        DocumentIdentityBinding identity,
        DocumentScopeSelection scope,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var collection = GetCollection(unit);
        var filter = DocumentLookupFilter(scope.StorageKey!, requestedIdentity.LookupKey);
        var find = session is null ? collection.Find(filter) : collection.Find(session, filter);
        var document = await find.SingleOrDefaultAsync(cancellationToken);
        if (document is null)
            return null;

        var retainedId = document["id_original"].AsString;
        identity.EnsureLookupIntegrity(
            unit.Identity.Value,
            requestedIdentity,
            retainedId,
            document["id_comparison_key"].AsString,
            document["id_lookup_key"].AsString);
        return ReadEnvelope(unit, document);
    }

    private IMongoCollection<BsonDocument> GetCollection(StorageUnit unit) =>
        database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(unit));

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

    private static BsonDocument CreateDocument(
        StorageUnit unit,
        SaveDocumentRequest request,
        PortableStringIdentityProjection identity,
        DocumentScopeSelection scope,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var content = BsonDocument.Parse(request.ContentJson);
        var document = new BsonDocument
        {
            ["_id"] = new BsonDocument { ["scope"] = scope.StorageKey, ["id"] = request.Id },
            ["storage_scope"] = scope.StorageKey,
            ["id_original"] = identity.OriginalValue,
            ["id_comparison_key"] = identity.ComparisonKey,
            ["id_lookup_key"] = identity.LookupKey,
            ["schema_version"] = request.SchemaVersion,
            ["version"] = version,
            ["content"] = content,
            ["content_json"] = request.ContentJson,
            ["created_utc"] = createdAt.ToString("O"),
            ["updated_utc"] = updatedAt.ToString("O")
        };

        var physicalized = CreatePhysicalizedDocument(unit, content);
        if (physicalized.ElementCount > 0)
            document["physicalized"] = physicalized;

        return document;
    }

    private static DocumentEnvelope ReadEnvelope(StorageUnit unit, BsonDocument document) =>
        new(
            unit.Identity.Value,
            document.GetValue("id_original").AsString,
            document.GetValue("schema_version").AsString,
            document.GetValue("version").ToInt64(),
            ReadContentJson(document),
            DateTimeOffset.Parse(document.GetValue("created_utc").AsString),
            DateTimeOffset.Parse(document.GetValue("updated_utc").AsString))
        {
            Scope = DocumentStoreScopeResolver.ReadScope(document.GetValue("storage_scope").AsString)
        };

    private static FilterDefinition<BsonDocument> DocumentLookupFilter(string storageScope, string lookupKey) =>
        Builders<BsonDocument>.Filter.Eq("storage_scope", storageScope) &
        Builders<BsonDocument>.Filter.Eq("id_lookup_key", lookupKey);

    private static FilterDefinition<BsonDocument> AuthoritativeIdentityFilter(
        string storageScope,
        PortableStringIdentityProjection identity,
        string authoritativeId) =>
        DocumentLookupFilter(storageScope, identity.LookupKey) &
        Builders<BsonDocument>.Filter.Eq("id_comparison_key", identity.ComparisonKey) &
        Builders<BsonDocument>.Filter.Eq("id_original", authoritativeId);

    private static string ReadContentJson(BsonDocument document) =>
        document.TryGetValue("content_json", out var contentJson)
            ? contentJson.AsString
            : document.GetValue("content").AsBsonDocument.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });

    private static BsonDocument CreatePhysicalizedDocument(StorageUnit unit, BsonDocument content)
    {
        var physicalized = new BsonDocument();
        foreach (var field in PhysicalizationProjection.EligibleFields(unit))
        {
            if (TryGetBsonPath(content, field.Path, out var value) && value is not BsonNull)
                physicalized[MongoDbGroundworkNames.PhysicalizedFieldName(field)] = value;
        }

        return physicalized;
    }

    private static bool TryGetBsonPath(BsonDocument root, string path, out BsonValue value)
    {
        value = BsonNull.Value;
        BsonValue current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!current.IsBsonDocument || !current.AsBsonDocument.TryGetValue(segment, out current))
                return false;
        }

        value = current;
        return true;
    }

    private static BsonValue ToBsonValue(IndexValueKind valueKind, string value) =>
        valueKind switch
        {
            IndexValueKind.Number when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) => longValue,
            IndexValueKind.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) => doubleValue,
            IndexValueKind.Boolean when bool.TryParse(value, out var boolValue) => boolValue,
            _ => value
        };

    private static bool IsDuplicateKey(MongoWriteException exception) =>
        exception.WriteError?.Code == 11000;

    private sealed class MongoDocumentUnitOfWork(
        MongoDbDocumentStore store,
        IClientSessionHandle session,
        DocumentCommitScope commitScope) : IDocumentUnitOfWork
    {
        private bool completed;

        public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            commitScope.EnsureIncludes(request.DocumentKind);
            try
            {
                var unit = store.GetUnit(request.DocumentKind);
                var scope = store.ResolveScope(unit, StorageScopeOperation.Save);
                var result = await store.SaveCoreAsync(unit, request, scope, session, cancellationToken);
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
            commitScope.EnsureIncludes(request.DocumentKind);
            try
            {
                var unit = store.GetUnit(request.DocumentKind);
                var scope = store.ResolveScope(unit, StorageScopeOperation.Delete);
                var result = await store.DeleteCoreAsync(unit, request, scope, session, cancellationToken);
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
            commitScope.EnsureIncludes(documentKind);
            var unit = store.GetUnit(documentKind);
            var scope = store.ResolveScope(unit, StorageScopeOperation.Load);
            var identity = store.identityBindings[documentKind];
            return await store.LoadCoreAsync(unit, identity.Project(id), identity, scope, session, cancellationToken);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            try
            {
                await session.CommitTransactionAsync(cancellationToken);
            }
            finally
            {
                Complete();
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

            try
            {
                if (session.IsInTransaction)
                    await session.AbortTransactionAsync();
            }
            catch
            {
                // Best-effort rollback when disposed without an explicit commit/rollback.
            }
            finally
            {
                Complete();
            }
        }

        private void Complete()
        {
            if (completed)
                return;

            completed = true;
            session.Dispose();
        }

        private async Task AbortAsync(CancellationToken cancellationToken)
        {
            if (completed)
                return;

            try
            {
                if (session.IsInTransaction)
                    await session.AbortTransactionAsync(cancellationToken);
            }
            catch (MongoException)
            {
                // A failed write or non-success result already makes the unit of work terminal.
            }
            finally
            {
                Complete();
            }
        }

        private void EnsureActive()
        {
            if (completed)
                throw new InvalidOperationException("The document transaction has already been committed or rolled back.");
        }
    }
}
