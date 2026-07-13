using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>Route-driven MongoDB document store for all three physical storage forms.</summary>
public sealed class MongoDbPhysicalDocumentStore : IDocumentStore, IBoundedDocumentStore
{
    private const string ContentField = MongoDbPhysicalStorageFields.NativeContent;
    private const string CreatedField = MongoDbPhysicalStorageFields.CreatedAt;
    private const string UpdatedField = MongoDbPhysicalStorageFields.UpdatedAt;
    private readonly IMongoDatabase database;
    private readonly MongoDbPhysicalStorageModel model;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly IReadOnlyDictionary<string, PhysicalQueryDocumentStore> queryStores;
    private readonly MongoDbPhysicalDocumentStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly MongoDbPhysicalDocumentStoreExecutionHooks hooks;
    private readonly Func<CancellationToken, Task<IClientSessionHandle>> startSessionAsync;
    private readonly MongoDbTransactionCapability transactionCapability;

    public MongoDbPhysicalDocumentStore(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver = null,
        MongoDbPhysicalDocumentStoreOptions? options = null)
        : this(database, model, access, scopeObserver, options, TimeProvider.System, null)
    {
    }

    internal MongoDbPhysicalDocumentStore(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver,
        MongoDbPhysicalDocumentStoreOptions? options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks? hooks,
        Func<CancellationToken, Task<IClientSessionHandle>>? startSessionAsync = null,
        MongoDbTransactionCapability? transactionCapability = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority);
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
        this.options = options ?? new MongoDbPhysicalDocumentStoreOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.hooks = hooks ?? MongoDbPhysicalDocumentStoreExecutionHooks.None;
        this.startSessionAsync = startSessionAsync ??
            (ct => this.database.Client.StartSessionAsync(cancellationToken: ct));
        this.transactionCapability = transactionCapability ?? MongoDbTransactionCapability.ForDatabase(this.database);
        DocumentStoreScopeResolver.ObserveAcquisition(access, this.scopeObserver);
        queryStores = model.Routes.ToFrozenDictionary(
            route => route.StorageUnit.Value,
            route => CreateQueryStore(route),
            StringComparer.Ordinal);
    }

    public DocumentStoreAccess Access { get; }

    public TransactionBoundary TransactionBoundary => transactionCapability.IsKnownSupported
        ? TransactionBoundary.CrossUnitAtomic
        : TransactionBoundary.PerOperation;

    public Task<DocumentStoreWriteResult> SaveAsync(
        SaveDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(request.DocumentKind, StorageScopeOperation.Save);
        return ExecuteAtomicAsync(
            [request.DocumentKind],
            session => SaveCoreAsync(request, route, scope, session, cancellationToken),
            cancellationToken);
    }

    public async Task<DocumentEnvelope?> LoadAsync(
        string documentKind,
        string id,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(documentKind, StorageScopeOperation.Load);
        await transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical storage",
            cancellationToken);
        return await LoadCoreAsync(route, id, scope, session: null, cancellationToken);
    }

    public Task<DocumentStoreWriteResult> DeleteAsync(
        DeleteDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var (route, scope) = ResolveOperation(request.DocumentKind, StorageScopeOperation.Delete);
        return ExecuteAtomicAsync(
            [request.DocumentKind],
            session => DeleteCoreAsync(request, route, scope, session, cancellationToken),
            cancellationToken);
    }

    public async Task<IDocumentUnitOfWork> BeginAsync(
        DocumentCommitScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var units = scope.Kinds.Select(Unit).ToArray();
        if (units.Select(unit => unit.Tenancy.Kind).Distinct().Count() != 1)
            throw DocumentStoreScopeResolver.RejectMixedUnitOfWork(scopeObserver, ScopePolicy(units[0]));
        foreach (var unit in units)
            ResolveScope(unit, StorageScopeOperation.BeginUnitOfWork);
        await transactionCapability.EnsureSupportedAsync(scope.Kinds, "physical storage", cancellationToken);

        var session = await startSessionAsync(cancellationToken);
        try
        {
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            return new UnitOfWork(this, session, scope);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).QueryAsync(query, cancellationToken);

    public Task<long> CountAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).CountAsync(query, cancellationToken);

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).FirstOrDefaultAsync(query, cancellationToken);

    public Task<bool> AnyAsync(DocumentQuery query, CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).AnyAsync(query, cancellationToken);

    public Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(
        DocumentStoreQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven MongoDB storage requires DocumentQuery with an explicit bounded-query identity.");

#pragma warning disable GW0004
    public Task<DocumentQueryResult> QueryAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven MongoDB storage requires DocumentQuery with an explicit bounded-query identity.");

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven MongoDB storage requires DocumentQuery with an explicit bounded-query identity.");

    public Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Route-driven MongoDB storage requires DocumentQuery with an explicit bounded-query identity.");
#pragma warning restore GW0004

    /// <summary>Returns native MongoDB explain evidence for the exact compiled bounded query.</summary>
    public Task<BsonDocument> ExplainAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        return ExplainCoreAsync(query, count: false, cancellationToken);
    }

    /// <summary>Returns native MongoDB explain evidence for the exact compiled bounded count query.</summary>
    public Task<BsonDocument> ExplainCountAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        return ExplainCoreAsync(query, count: true, cancellationToken);
    }

    private async Task<BsonDocument> ExplainCoreAsync(
        DocumentQuery query,
        bool count,
        CancellationToken cancellationToken)
    {
        var route = Route(query.DocumentKind);
        var physical = model.StorageByStorageUnit[query.DocumentKind];
        var plan = QueryStore(query.DocumentKind).ResolvePlan(
            query,
            count ? BoundedQueryResultOperation.Count : BoundedQueryResultOperation.Documents);
        var scope = ResolveScope(Unit(query.DocumentKind), StorageScopeOperation.Query, allowAcrossScopes: true);
        var filter = MongoDbPhysicalQueryHandler.BuildFilter(query, plan, scope, physical, route);
        var sort = MongoDbPhysicalQueryHandler.BuildSort(query, plan);
        var renderedFilter = filter.Render(new RenderArgs<BsonDocument>(
            database.GetCollection<BsonDocument>(plan.LookupObject.Identifier).DocumentSerializer,
            BsonSerializer.SerializerRegistry));
        var explainedCommand = count
            ? new BsonDocument
            {
                ["aggregate"] = plan.LookupObject.Identifier,
                ["pipeline"] = new BsonArray
                {
                    new BsonDocument("$match", renderedFilter),
                    new BsonDocument("$group", new BsonDocument
                    {
                        ["_id"] = 1,
                        ["n"] = new BsonDocument("$sum", 1)
                    })
                },
                ["cursor"] = new BsonDocument()
            }
            : new BsonDocument
            {
                ["find"] = plan.LookupObject.Identifier,
                ["filter"] = renderedFilter,
                ["sort"] = sort.Render(new RenderArgs<BsonDocument>(
                    database.GetCollection<BsonDocument>(plan.LookupObject.Identifier).DocumentSerializer,
                    BsonSerializer.SerializerRegistry))
            };
        if (!count && query.Skip is { } skip)
            explainedCommand["skip"] = skip;
        if (!count && query.Take is { } take)
            explainedCommand["limit"] = take;
        var command = new BsonDocument
        {
            ["explain"] = explainedCommand,
            ["verbosity"] = "queryPlanner"
        };
        await transactionCapability.EnsureSupportedAsync(
            [query.DocumentKind],
            "physical query explain",
            cancellationToken);
        return await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
    }

    private PhysicalQueryDocumentStore CreateQueryStore(ExecutableStorageRoute route)
    {
        var storage = model.StorageByStorageUnit[route.StorageUnit.Value];
        ValidateScaleBearingOperations(storage);
        ValidateTypedPaths(route, storage);
        var capabilities = Capabilities(route, storage);
        var plans = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        if (!plans.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, plans.Diagnostics.Select(x => $"{x.Code}: {x.Message}")));

        var linkedPlans = plans.Plans.Where(plan => plan.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary).ToArray();
        var nativePlans = plans.Plans.Where(plan => plan.AccessKind != PhysicalQueryAccessKind.LinkedIndexThenPrimary).ToArray();
        var handlers = new IPhysicalDocumentQueryHandler[]
        {
            new MongoDbPhysicalQueryHandler(
                MongoDbPhysicalQueryHandler.LinkedIdentity,
                PhysicalQuerySourceKind.LinkedIndex,
                database,
                route,
                storage,
                () => ResolveScope(Unit(route.StorageUnit.Value), StorageScopeOperation.Query, allowAcrossScopes: true),
                linkedPlans.Select(Certification).ToArray(),
                capabilities.NativeFieldIdentifiers,
                options,
                timeProvider,
                hooks,
                transactionCapability),
            new MongoDbPhysicalQueryHandler(
                MongoDbPhysicalQueryHandler.NativeIdentity,
                PhysicalQuerySourceKind.NativeDocumentFields,
                database,
                route,
                storage,
                () => ResolveScope(Unit(route.StorageUnit.Value), StorageScopeOperation.Query, allowAcrossScopes: true),
                nativePlans.Select(Certification).ToArray(),
                capabilities.NativeFieldIdentifiers,
                options,
                timeProvider,
                hooks,
                transactionCapability)
        };
        return new PhysicalQueryDocumentStore(route, storage, capabilities, handlers);
    }

    private static void ValidateTypedPaths(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage)
    {
        foreach (var index in storage.LogicalIndexes)
        {
            foreach (var field in index.Fields.Where(field => !PhysicalDocumentFieldPaths.IsEnvelope(field.Path)))
            {
                var valueKind = index.GetValueKind(field);
                var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
                    candidate.Definition.Path == field.Path);
                if (projection is null)
                {
                    if (valueKind is IndexValueKind.Number or IndexValueKind.DateTime)
                    {
                        throw new InvalidOperationException(
                            $"MongoDB cannot certify exact '{valueKind}' query semantics for path " +
                            $"'{field.Path}' without a typed projected column.");
                    }
                    continue;
                }
                if (!PortableQueryOperationCompatibility.Supports(valueKind, projection.Definition.Type))
                {
                    throw new InvalidOperationException(
                        $"MongoDB projected path '{field.Path}' type '{projection.Definition.Type}' cannot " +
                        $"preserve logical value kind '{valueKind}'.");
                }
            }

            foreach (var query in storage.BoundedQueries.Where(query => query.IndexIdentity == index.Identity))
            {
                var predicates = query.PredicateFields.Count == 0
                    ? index.Fields.Take(1).Select(field =>
                        new BoundedQueryPredicateField(field.Path, query.Operations)).ToArray()
                    : query.PredicateFields;
                foreach (var predicate in predicates)
                {
                    var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
                        candidate.Definition.Path == predicate.Path);
                    if (projection is not null && predicate.Operations.Any(operation =>
                            !PortableQueryOperationCompatibility.Supports(projection.Definition.Type, operation)))
                    {
                        throw new InvalidOperationException(
                            $"MongoDB projected path '{predicate.Path}' type '{projection.Definition.Type}' cannot " +
                            "execute every declared query operation without changing semantics.");
                    }
                }
            }
        }
    }

    private static void ValidateScaleBearingOperations(StorageUnitPhysicalStorage storage)
    {
        var nonIndexable = new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Contains,
            PortableQueryOperation.StartsWith
        };
        foreach (var query in storage.BoundedQueries.Where(query =>
                     query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            var unsupported = query.Operations.Intersect(nonIndexable).Order().ToArray();
            if (unsupported.Length == 0)
                continue;

            throw new InvalidOperationException(
                $"MongoDB cannot certify scale-bearing query '{query.Identity}' operations " +
                $"{string.Join(", ", unsupported)} as indexed: Groundwork case-insensitive regular-expression semantics " +
                "cannot be served by the declared ordinary MongoDB B-tree index.");
        }
    }

    private static PhysicalQueryHandlerCertification Certification(PhysicalQueryPlan plan) =>
        new(
            plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            plan.LogicalIndexPaths,
            plan.AccessKind,
            plan.Scope.Field.Target,
            plan.LookupObject,
            plan.PrimaryObject,
            plan.IndexName,
            new[] { plan.Scope.Field, plan.Discriminator }
                .Concat(plan.Predicates.Select(predicate => predicate.Field))
                .Concat(plan.Order.Select(order => order.Field))
                .GroupBy(field => field.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal),
            plan.RouteFingerprint);

    private PhysicalQueryPlannerCapabilities Capabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = route.Envelope.Id.Identifier,
            ["documentKind"] = route.Envelope.DocumentKind.Identifier,
            ["storageScope"] = route.Envelope.StorageScope.Identifier,
            ["version"] = route.Envelope.Version.Identifier,
            ["schemaVersion"] = route.Envelope.SchemaVersion.Identifier
        };
        foreach (var path in storage.LogicalIndexes.SelectMany(index => index.Fields).Select(field => field.Path).Distinct(StringComparer.Ordinal))
        {
            fields[path] = route.ProjectedColumns.SingleOrDefault(column =>
                    column.Target == ExecutableStorageObjectRole.PrimaryStorage && column.Definition.Path == path)?.Column.Identifier
                ?? $"{ContentField}.{path}";
        }
        return new PhysicalQueryPlannerCapabilities(
            model.Provider,
            [PhysicalQuerySourceKind.LinkedIndex, PhysicalQuerySourceKind.NativeDocumentFields],
            MongoDbPhysicalQueryHandler.Operations,
            new Dictionary<PhysicalQuerySourceKind, string>
            {
                [PhysicalQuerySourceKind.LinkedIndex] = MongoDbPhysicalQueryHandler.LinkedIdentity,
                [PhysicalQuerySourceKind.NativeDocumentFields] = MongoDbPhysicalQueryHandler.NativeIdentity
            },
            fields,
            supportsCompoundPredicates: true,
            supportsDisjunction: true,
            supportsOffsetPaging: true,
            supportsKeysetPaging: false,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey: false);
    }

    private async Task<DocumentStoreWriteResult> ExecuteAtomicAsync(
        IReadOnlyList<string> documentKinds,
        Func<IClientSessionHandle, Task<DocumentStoreWriteResult>> action,
        CancellationToken cancellationToken)
    {
        await transactionCapability.EnsureSupportedAsync(documentKinds, "physical storage", cancellationToken);
        var retryStarted = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await startSessionAsync(cancellationToken);
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            try
            {
                await hooks.TransactionBodyStarting(session, attempt, cancellationToken);
                var result = await action(session);
                await CommitWithRetryAsync(session, documentKinds, cancellationToken);
                return result;
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsDuplicateKey(exception))
            {
                await AbortTransactionIgnoringFailureAsync(session);
                return DocumentStoreWriteResult.ConcurrencyConflict;
            }
            catch (MongoException exception) when (
                IsTransientTransactionConflict(exception) &&
                !cancellationToken.IsCancellationRequested)
            {
                await AbortTransactionIgnoringFailureAsync(session);
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanRetry(
                        attempt,
                        retryStarted,
                        options.MaximumTransactionAttempts,
                        options.TransactionRetryTimeout))
                {
                    throw;
                }
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                if (timeProvider.GetElapsedTime(retryStarted) >= options.TransactionRetryTimeout)
                    throw;
            }
            catch (DocumentCommitAcknowledgementUncertainException)
            {
                await AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
            catch
            {
                await AbortTransactionIgnoringFailureAsync(session);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }

    private async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        IReadOnlyList<string> documentKinds,
        CancellationToken cancellationToken)
    {
        var retryStarted = timeProvider.GetTimestamp();
        MongoException? unknownCommitResult = null;
        var commitWasInvoked = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await hooks.CommitStarting(session, attempt, cancellationToken);
                    commitWasInvoked = true;
                    var commit = session.CommitTransactionAsync(cancellationToken);
                    await hooks.CommitInvoked(session, attempt, cancellationToken);
                    await commit;
                    return;
                }
                catch (MongoException exception)
                {
                    if (!exception.HasErrorLabel("UnknownTransactionCommitResult"))
                    {
                        if (unknownCommitResult is not null)
                            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw;
                    }

                    unknownCommitResult = exception;
                    await hooks.CommitResultUnknown(session, attempt, exception, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanRetry(
                            attempt,
                            retryStarted,
                            options.MaximumCommitAttempts,
                            options.CommitRetryTimeout))
                    {
                        throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                    }
                    await hooks.CommitRetryDelayStarting(session, attempt, cancellationToken);
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    await hooks.CommitRetryDelayCompleted(session, attempt, cancellationToken);
                    if (timeProvider.GetElapsedTime(retryStarted) >= options.CommitRetryTimeout)
                        throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
                }
            }
        }
        catch (OperationCanceledException exception) when (commitWasInvoked)
        {
            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
        }
        catch (TimeoutException exception) when (commitWasInvoked)
        {
            throw new DocumentCommitAcknowledgementUncertainException(documentKinds, exception);
        }
    }

    private bool CanRetry(int attempts, long started, int maximumAttempts, TimeSpan timeout) =>
        attempts < maximumAttempts && timeProvider.GetElapsedTime(started) < timeout;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var maximumDelay = Math.Min(100, 2 << Math.Min(attempt, 5));
        return Task.Delay(Random.Shared.Next(1, maximumDelay + 1), cancellationToken);
    }

    internal static bool IsTransientTransactionConflict(MongoException exception) =>
        exception.HasErrorLabel("TransientTransactionError") ||
        exception switch
        {
            MongoCommandException command => command.Code is 112 or 244 or 251,
            MongoWriteException write =>
                IsTransientTransactionConflictCode(write.WriteError?.Code) ||
                IsTransientTransactionConflictCode(write.WriteConcernError?.Code),
            MongoBulkWriteException bulk =>
                bulk.WriteErrors.Any(error => IsTransientTransactionConflictCode(error.Code)) ||
                IsTransientTransactionConflictCode(bulk.WriteConcernError?.Code),
            _ => false
        };

    private static bool IsDuplicateKey(MongoException exception) =>
        exception switch
        {
            MongoCommandException command => command.Code == 11000,
            MongoWriteException write => write.WriteError?.Code == 11000 || write.WriteConcernError?.Code == 11000,
            MongoBulkWriteException bulk =>
                bulk.WriteErrors.Any(error => error.Code == 11000) ||
                bulk.WriteConcernError?.Code == 11000,
            _ => false
        };

    private static bool IsTransientTransactionConflictCode(int? code) =>
        code is 112 or 244 or 251;

    internal static async Task AbortTransactionIgnoringFailureAsync(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            return;
        try
        {
            await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (MongoException)
        {
            // The operation error or structured conflict is authoritative.
        }
    }

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(
        SaveDocumentRequest request,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await LoadDocumentAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
        if (current is not null && request.ExpectedVersion is not null && current[route.Envelope.Version.Identifier].ToInt64() != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;
        if (current is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var created = current is null ? now : new DateTimeOffset(current[CreatedField].ToUniversalTime());
        var version = current is null ? 1 : current[route.Envelope.Version.Identifier].ToInt64() + 1;
        var incarnation = current?.GetValue(MongoDbPhysicalStorageFields.Incarnation).AsString ?? Guid.NewGuid().ToString("N");
        var content = BsonDocument.Parse(request.ContentJson);
        var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(request.ContentJson, route.ProjectedColumns);
        var document = CreatePrimary(
            route,
            request,
            scope.StorageKey!,
            version,
            incarnation,
            created,
            now,
            content,
            projectedValues);
        var primary = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        if (current is null)
        {
            await primary.InsertOneAsync(session, document, cancellationToken: cancellationToken);
        }
        else
        {
            var filter = IdentityFilter(route, request.Id, scope.StorageKey!);
            if (request.ExpectedVersion is not null)
                filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
            var result = await primary.ReplaceOneAsync(session, filter, document, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
                return DocumentStoreWriteResult.ConcurrencyConflict;
        }
        await MaintainLinkedAsync(route, document, projectedValues, session, cancellationToken);
        return DocumentStoreWriteResult.Saved(ReadEnvelope(route, document));
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(
        DeleteDocumentRequest request,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var filter = IdentityFilter(route, request.Id, scope.StorageKey!);
        if (request.ExpectedVersion is not null)
            filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
        var result = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .DeleteOneAsync(session, filter, cancellationToken: cancellationToken);
        if (result.DeletedCount == 0)
        {
            var exists = await LoadDocumentAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
            return exists is null ? DocumentStoreWriteResult.NotFound : DocumentStoreWriteResult.ConcurrencyConflict;
        }
        if (route.LinkedIndexStorage is not null)
        {
            var linkedFilter = Builders<BsonDocument>.Filter.Eq(route.LinkedRelationship!.DocumentId.Identifier, request.Id) &
                               Builders<BsonDocument>.Filter.Eq(route.LinkedRelationship.StorageScope.Identifier, scope.StorageKey) &
                               Builders<BsonDocument>.Filter.Eq(route.LinkedRelationship.DocumentKind.Identifier, route.Discriminator.Value);
            await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
                .DeleteOneAsync(session, linkedFilter, cancellationToken: cancellationToken);
        }
        return DocumentStoreWriteResult.Deleted;
    }

    private async Task<DocumentEnvelope?> LoadCoreAsync(
        ExecutableStorageRoute route,
        string id,
        DocumentScopeSelection scope,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(route, id, scope.StorageKey!, session, cancellationToken);
        return document is null ? null : ReadEnvelope(route, document);
    }

    private async Task<BsonDocument?> LoadDocumentAsync(
        ExecutableStorageRoute route,
        string id,
        string scope,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var filter = IdentityFilter(route, id, scope);
        return session is null
            ? await collection.Find(filter).SingleOrDefaultAsync(cancellationToken)
            : await collection.Find(session, filter).SingleOrDefaultAsync(cancellationToken);
    }

    private static BsonDocument CreatePrimary(
        ExecutableStorageRoute route,
        SaveDocumentRequest request,
        string scope,
        long version,
        string incarnation,
        DateTimeOffset created,
        DateTimeOffset updated,
        BsonDocument content,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        var document = new BsonDocument
        {
            [route.Envelope.Id.Identifier] = request.Id,
            [route.Envelope.DocumentKind.Identifier] = route.Discriminator.Value,
            [route.Envelope.StorageScope.Identifier] = scope,
            [route.Envelope.Version.Identifier] = version,
            [route.Envelope.SchemaVersion.Identifier] = request.SchemaVersion,
            [route.Envelope.CanonicalJson.Identifier] = request.ContentJson,
            [MongoDbPhysicalStorageFields.Incarnation] = incarnation,
            [ContentField] = content,
            [CreatedField] = created.UtcDateTime,
            [UpdatedField] = updated.UtcDateTime
        };
        foreach (var projection in route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage))
        {
            var value = projectedValues[projection];
            if (value.IsPresent)
                document[projection.Column.Identifier] = value.Value;
        }
        document[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(route.PrimaryKey, document);
        return document;
    }

    private async Task MaintainLinkedAsync(
        ExecutableStorageRoute route,
        BsonDocument primary,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (route.LinkedIndexStorage is null)
            return;
        var rel = route.LinkedRelationship!;
        var linked = new BsonDocument
        {
            [rel.DocumentId.Identifier] = primary[route.Envelope.Id.Identifier],
            [rel.DocumentKind.Identifier] = route.Discriminator.Value,
            [rel.StorageScope.Identifier] = primary[route.Envelope.StorageScope.Identifier],
            [MongoDbPhysicalStorageFields.LinkedPrimaryVersion] = primary[route.Envelope.Version.Identifier],
            [MongoDbPhysicalStorageFields.Incarnation] = primary[MongoDbPhysicalStorageFields.Incarnation]
        };
        foreach (var projection in route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage))
        {
            var value = projectedValues[projection];
            if (value.IsPresent)
                linked[projection.Column.Identifier] = value.Value;
        }
        linked[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(route.AuxiliaryKey!, linked);
        await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier).ReplaceOneAsync(
            session,
            Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Id, linked[MongoDbPhysicalStorageFields.Id]),
            linked,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    private static FilterDefinition<BsonDocument> IdentityFilter(ExecutableStorageRoute route, string id, string scope) =>
        Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, id) &
        Builders<BsonDocument>.Filter.Eq(route.Envelope.StorageScope.Identifier, scope) &
        Builders<BsonDocument>.Filter.Eq(route.Discriminator.Column.Identifier, route.Discriminator.Value);

    internal static DocumentEnvelope ReadEnvelope(ExecutableStorageRoute route, BsonDocument document) =>
        new(
            route.StorageUnit.Value,
            document[route.Envelope.Id.Identifier].AsString,
            document[route.Envelope.SchemaVersion.Identifier].AsString,
            document[route.Envelope.Version.Identifier].ToInt64(),
            document[route.Envelope.CanonicalJson.Identifier].AsString,
            new DateTimeOffset(document[CreatedField].ToUniversalTime()),
            new DateTimeOffset(document[UpdatedField].ToUniversalTime()))
        {
            Scope = DocumentStoreScopeResolver.ReadScope(document[route.Envelope.StorageScope.Identifier].AsString)
        };

    private PhysicalQueryDocumentStore QueryStore(string kind) =>
        queryStores.TryGetValue(kind, out var store) ? store : throw Unknown(kind);

    private ExecutableStorageRoute Route(string kind) =>
        model.RoutesByStorageUnit.TryGetValue(kind, out var route) ? route : throw Unknown(kind);

    private StorageUnit Unit(string kind) =>
        model.Manifest.StorageUnits.SingleOrDefault(unit => unit.Identity.Value == kind) ?? throw Unknown(kind);

    private static InvalidOperationException Unknown(string kind) => new($"Document kind '{kind}' is not declared by the compiled MongoDB physical model.");

    private DocumentScopeSelection ResolveScope(StorageUnit unit, StorageScopeOperation operation, bool allowAcrossScopes = false) =>
        DocumentStoreScopeResolver.Resolve(unit, Access, operation, scopeObserver, allowAcrossScopes);

    private (ExecutableStorageRoute Route, DocumentScopeSelection Scope) ResolveOperation(
        string documentKind,
        StorageScopeOperation operation) =>
        (Route(documentKind), ResolveScope(Unit(documentKind), operation));

    internal IMongoDatabase Database => database;

    internal string ManifestIdentity => model.Manifest.Identity.Value;

    internal ProviderIdentity Provider => model.Provider;

    internal ExecutableStorageRoute GetRoute(string documentKind) => Route(documentKind);

    internal DocumentScopeSelection ResolveMutationScope(string documentKind) =>
        ResolveScope(Unit(documentKind), StorageScopeOperation.Mutate);

    internal PhysicalQueryPlannerCapabilities GetMutationCapabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage) =>
        Capabilities(route, storage);

    internal async Task<T> ExecutePhysicalMutationAsync<T>(
        string documentKind,
        Func<IClientSessionHandle, CancellationToken, Task<T>> action,
        Func<CancellationToken, ValueTask>? beforeCommit,
        Func<CancellationToken, ValueTask>? afterCommitBeforeAcknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical bounded mutation",
            cancellationToken);
        var retryStarted = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await startSessionAsync(cancellationToken);
            session.StartTransaction(new TransactionOptions(
                ReadConcern.Snapshot,
                writeConcern: WriteConcern.WMajority));
            try
            {
                await hooks.TransactionBodyStarting(session, attempt, cancellationToken);
                var result = await action(session, cancellationToken);
                if (beforeCommit is not null)
                    await beforeCommit(cancellationToken);
                await CommitWithRetryAsync(session, [documentKind], cancellationToken);
                if (afterCommitBeforeAcknowledgement is not null)
                    await afterCommitBeforeAcknowledgement(cancellationToken);
                return result;
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                (IsDuplicateKey(exception) || IsTransientTransactionConflict(exception)))
            {
                await AbortTransactionIgnoringFailureAsync(session);
                if (!CanRetry(
                        attempt,
                        retryStarted,
                        options.MaximumTransactionAttempts,
                        options.TransactionRetryTimeout))
                {
                    throw;
                }
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                if (timeProvider.GetElapsedTime(retryStarted) >= options.TransactionRetryTimeout)
                    throw;
            }
            catch (DocumentCommitAcknowledgementUncertainException)
            {
                await AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
            catch
            {
                await AbortTransactionIgnoringFailureAsync(session);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }

    private static StorageScopePolicy ScopePolicy(StorageUnit unit) =>
        unit.Tenancy.Kind == TenancyKind.Scoped ? StorageScopePolicy.Scoped : StorageScopePolicy.Global;

    private sealed class UnitOfWork(
        MongoDbPhysicalDocumentStore store,
        IClientSessionHandle session,
        DocumentCommitScope scope) : IDocumentUnitOfWork
    {
        private bool completed;
        public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var (route, selection) = store.ResolveOperation(request.DocumentKind, StorageScopeOperation.Save);
                var result = await store.SaveCoreAsync(request, route, selection, session, cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Saved)
                    await AbortAsync(CancellationToken.None);
                return result;
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                (IsDuplicateKey(exception) || IsTransientTransactionConflict(exception)))
            {
                await AbortAsync(CancellationToken.None);
                return DocumentStoreWriteResult.ConcurrencyConflict;
            }
            catch
            {
                await AbortAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
        public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(request.DocumentKind);
            try
            {
                var (route, selection) = store.ResolveOperation(request.DocumentKind, StorageScopeOperation.Delete);
                var result = await store.DeleteCoreAsync(request, route, selection, session, cancellationToken);
                if (result.Status != DocumentStoreWriteStatus.Deleted)
                    await AbortAsync(CancellationToken.None);
                return result;
            }
            catch (MongoException exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsTransientTransactionConflict(exception))
            {
                await AbortAsync(CancellationToken.None);
                return DocumentStoreWriteResult.ConcurrencyConflict;
            }
            catch
            {
                await AbortAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
        public Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            scope.EnsureIncludes(documentKind);
            var (route, selection) = store.ResolveOperation(documentKind, StorageScopeOperation.Load);
            return store.LoadCoreAsync(route, id, selection, session, cancellationToken);
        }
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            try { await store.CommitWithRetryAsync(session, scope.Kinds, cancellationToken); }
            finally { Complete(); }
        }
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            await AbortAsync(cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            if (completed) return;
            try { if (session.IsInTransaction) await session.AbortTransactionAsync(); }
            finally { Complete(); }
        }
        private void EnsureActive()
        {
            if (completed) throw new InvalidOperationException("The document transaction has completed.");
        }
        private async Task AbortAsync(CancellationToken cancellationToken)
        {
            if (completed) return;
            try
            {
                if (session.IsInTransaction)
                    await session.AbortTransactionAsync(cancellationToken);
            }
            catch (MongoException)
            {
                // A failed write already makes the unit of work terminal.
            }
            finally
            {
                Complete();
            }
        }
        private void Complete()
        {
            if (completed) return;
            completed = true;
            session.Dispose();
        }
    }
}

internal sealed record MongoDbPhysicalDocumentStoreExecutionHooks(
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> TransactionBodyStarting,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitStarting,
    Func<IClientSessionHandle, int, MongoException, CancellationToken, ValueTask> CommitResultUnknown,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitRetryDelayStarting,
    Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitRetryDelayCompleted)
{
    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> CommitInvoked { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryPageRead { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryAttemptStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryCountRead { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryPrimaryHydrationStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryRetryDelayStarting { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public Func<IClientSessionHandle, int, CancellationToken, ValueTask> QueryRetryDelayCompleted { get; init; } =
        static (_, _, _) => ValueTask.CompletedTask;

    public static MongoDbPhysicalDocumentStoreExecutionHooks None { get; } = new(
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask);
}

internal sealed class MongoDbPhysicalQueryHandler : IPhysicalDocumentQueryHandler
{
    internal const string LinkedIdentity = "Groundwork.MongoDb.LinkedIndex.v1";
    internal const string NativeIdentity = "Groundwork.MongoDb.NativeDocumentFields.v1";
    internal static IReadOnlySet<PortableQueryOperation> Operations { get; } =
        Enum.GetValues<PortableQueryOperation>().ToFrozenSet();
    private readonly IMongoDatabase database;
    private readonly ExecutableStorageRoute route;
    private readonly StorageUnitPhysicalStorage storage;
    private readonly Func<DocumentScopeSelection> scope;
    private readonly MongoDbPhysicalDocumentStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly MongoDbPhysicalDocumentStoreExecutionHooks hooks;
    private readonly MongoDbTransactionCapability transactionCapability;

    public MongoDbPhysicalQueryHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IMongoDatabase database,
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        Func<DocumentScopeSelection> scope,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications,
        IReadOnlyDictionary<string, string> nativeFieldIdentifiers,
        MongoDbPhysicalDocumentStoreOptions options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks hooks,
        MongoDbTransactionCapability transactionCapability)
    {
        Identity = identity;
        Source = source;
        this.database = database;
        this.route = route;
        this.storage = storage;
        this.scope = scope;
        this.options = options;
        this.timeProvider = timeProvider;
        this.hooks = hooks;
        this.transactionCapability = transactionCapability;
        Certifications = Array.AsReadOnly(certifications.ToArray());
        NativeFieldIdentifiers = source == PhysicalQuerySourceKind.NativeDocumentFields
            ? nativeFieldIdentifiers.ToFrozenDictionary(StringComparer.Ordinal)
            : FrozenDictionary<string, string>.Empty;
    }

    public string Identity { get; }
    public PhysicalQuerySourceKind Source { get; }
    public IReadOnlySet<PortableQueryOperation> SupportedOperations => Operations;
    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }
    public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; }
    public bool SupportsCompoundPredicates => true;
    public bool SupportsDisjunction => true;
    public bool SupportsOffsetPaging => true;
    public bool SupportsKeysetPaging => false;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => false;

    public async Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var filter = BuildFilter(query, plan, scope(), storage, route);
        var sort = BuildSort(query, plan);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical snapshot query",
            cancellationToken);
        var started = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
            session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot));
            try
            {
                await hooks.QueryAttemptStarting(session, attempt, cancellationToken);
                var total = await collection.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken);
                await hooks.QueryCountRead(session, attempt, cancellationToken);
                if (total == 0 || query.Take == 0)
                {
                    await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                    return new DocumentQueryResult([], total);
                }
                var find = collection.Find(session, filter).Sort(sort).Skip(query.Skip ?? 0);
                if (query.Take is { } take) find = find.Limit(take);
                var found = await find.ToListAsync(cancellationToken);
                await hooks.QueryPageRead(session, attempt, cancellationToken);
                var documents = plan.RequiresPrimaryLookup
                    ? await LoadPrimaryAsync(session, found, attempt, cancellationToken)
                    : found.Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                return new DocumentQueryResult(documents, total);
            }
            catch (MongoException exception) when (
                MongoDbPhysicalDocumentStore.IsTransientTransactionConflict(exception) &&
                !cancellationToken.IsCancellationRequested)
            {
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                if (attempt >= options.MaximumTransactionAttempts ||
                    timeProvider.GetElapsedTime(started) >= options.TransactionRetryTimeout)
                {
                    throw;
                }
                await hooks.QueryRetryDelayStarting(session, attempt, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, 2 << Math.Min(attempt, 5))), cancellationToken);
                await hooks.QueryRetryDelayCompleted(session, attempt, cancellationToken);
                if (timeProvider.GetElapsedTime(started) >= options.TransactionRetryTimeout)
                    throw;
            }
            catch
            {
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                throw;
            }
        }
    }

    public async Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        var filter = BuildFilter(query, plan, scope(), storage, route);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical count query",
            cancellationToken);
        return await database.GetCollection<BsonDocument>(plan.LookupObject.Identifier)
            .CountDocumentsAsync(filter, cancellationToken: cancellationToken);
    }

    public async Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(new DocumentQuery(
            query.DocumentKind, query.QueryIdentity, query.Clauses, query.Order, query.Skip, 1,
            query.Continuation, query.LatestPerKeyPath), plan, cancellationToken);
        return result.Documents.FirstOrDefault();
    }

    public async Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        var filter = BuildFilter(query, plan, scope(), storage, route);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical existence query",
            cancellationToken);
        return await database.GetCollection<BsonDocument>(plan.LookupObject.Identifier)
            .Find(filter).Limit(1).AnyAsync(cancellationToken);
    }

    internal static FilterDefinition<BsonDocument> BuildFilter(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        StorageUnitPhysicalStorage storage,
        ExecutableStorageRoute route)
    {
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq(plan.Discriminator.Identifier, plan.StorageUnit.Value)
        };
        if (scope.StorageKey is not null)
            filters.Add(Builders<BsonDocument>.Filter.Eq(plan.Scope.Field.Identifier, scope.StorageKey));
        var logicalIndex = storage.LogicalIndexes.Single(index => index.Identity == plan.LogicalIndexIdentity);
        if (logicalIndex.MissingValueBehavior == MissingValueBehavior.Excluded)
        {
            var physicalIndex = route.Indexes.SingleOrDefault(index => index.Identity == plan.LogicalIndexIdentity);
            var membershipFields = physicalIndex is null
                ? logicalIndex.Fields
                    .Where(field => !PhysicalDocumentFieldPaths.IsEnvelope(field.Path))
                    .Select(field => $"{MongoDbPhysicalStorageFields.NativeContent}.{field.Path}")
                    .ToArray()
                : MongoDbPhysicalIndexSemantics.ValueFields(route, physicalIndex);
            filters.AddRange(membershipFields.Select(field => Builders<BsonDocument>.Filter.Exists(field, true)));
        }
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
                return Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true);
            filters.Add(Builders<BsonDocument>.Filter.Or(clause.Comparisons.Select(comparison =>
                Comparison(
                    comparison,
                    plan.Predicates.Single(predicate => predicate.Path == comparison.Path).Field,
                    route))));
        }
        return Builders<BsonDocument>.Filter.And(filters);
    }

    internal static SortDefinition<BsonDocument> BuildSort(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var requested = query.Order.Count == 0
            ? plan.Order
            : query.Order.Select(order => new PhysicalQueryOrder(
                order.Path,
                plan.Order.Single(planned => planned.Path == order.Path).Field,
                order.Direction,
                false)).Concat(plan.Order.Where(order => order.IsIdentityTieBreak)).ToArray();
        return Builders<BsonDocument>.Sort.Combine(requested.Select(order =>
            order.Direction == PhysicalSortDirection.Ascending
                ? Builders<BsonDocument>.Sort.Ascending(order.Field.Identifier)
                : Builders<BsonDocument>.Sort.Descending(order.Field.Identifier)));
    }

    private async Task<IReadOnlyList<DocumentEnvelope>> LoadPrimaryAsync(
        IClientSessionHandle session,
        IReadOnlyList<BsonDocument> linked,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (linked.Count == 0) return [];
        var rel = route.LinkedRelationship!;
        var keys = linked.Select(document => new BsonDocument
        {
            [route.Envelope.Id.Identifier] = document[rel.DocumentId.Identifier],
            [route.Envelope.DocumentKind.Identifier] = document[rel.DocumentKind.Identifier],
            [route.Envelope.StorageScope.Identifier] = document[rel.StorageScope.Identifier]
        }).ToArray();
        var filters = keys.Select(key => Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, key[route.Envelope.Id.Identifier]) &
                                         Builders<BsonDocument>.Filter.Eq(route.Envelope.DocumentKind.Identifier, key[route.Envelope.DocumentKind.Identifier]) &
                                         Builders<BsonDocument>.Filter.Eq(route.Envelope.StorageScope.Identifier, key[route.Envelope.StorageScope.Identifier]));
        await hooks.QueryPrimaryHydrationStarting(session, attempt, cancellationToken);
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(session, Builders<BsonDocument>.Filter.Or(filters)).ToListAsync(cancellationToken);
        var byKey = primary.ToDictionary(document => Key(document, route.Envelope.Id.Identifier, route.Envelope.StorageScope.Identifier));
        return linked.Select(document => byKey[Key(document, rel.DocumentId.Identifier, rel.StorageScope.Identifier)])
            .Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
    }

    private static DocumentIdentity Key(BsonDocument document, string id, string scope) =>
        new(document[scope].AsString, document[id].AsString);

    private readonly record struct DocumentIdentity(string Scope, string Id);

    private static FilterDefinition<BsonDocument> Comparison(
        DocumentQueryComparison comparison,
        PhysicalQueryField queryField,
        ExecutableStorageRoute route)
    {
        var field = queryField.Identifier;
        var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
            candidate.Target == queryField.Target &&
            candidate.Column.Identifier == queryField.Identifier);
        BsonValue ToValue(string? value) => projection is null
            ? ToLogicalValue(queryField.ValueKind, value)
            : MongoDbPhysicalProjectionValues.ParseQueryValue(projection, value);
        var value = comparison.Values.Count == 0 ? BsonNull.Value : ToValue(comparison.Values[0]);
        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => Builders<BsonDocument>.Filter.Eq(field, value),
            QueryComparisonOperator.NotEqual => Builders<BsonDocument>.Filter.Ne(field, value),
            QueryComparisonOperator.In => comparison.Values.Count == 0
                ? Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true)
                : Builders<BsonDocument>.Filter.In(field, comparison.Values.Select(ToValue).ToArray()),
            QueryComparisonOperator.Contains => Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression(Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.StartsWith => Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression("^" + Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.GreaterThan => Builders<BsonDocument>.Filter.Gt(field, value),
            QueryComparisonOperator.GreaterThanOrEqual => Builders<BsonDocument>.Filter.Gte(field, value),
            QueryComparisonOperator.LessThan => Builders<BsonDocument>.Filter.Lt(field, value),
            QueryComparisonOperator.LessThanOrEqual => Builders<BsonDocument>.Filter.Lte(field, value),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison.Operator, null)
        };
    }

    private static BsonValue ToLogicalValue(IndexValueKind kind, string? value)
    {
        if (value is null) return BsonNull.Value;
        try
        {
            return kind switch
            {
                IndexValueKind.Number => new BsonDecimal128(Decimal128.Parse(value)),
                IndexValueKind.Boolean => bool.Parse(value),
                IndexValueKind.DateTime => throw new InvalidOperationException(
                    "MongoDB exact DateTime queries require a typed projected field."),
                _ => value
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"MongoDB query value '{value}' cannot be converted to logical value kind '{kind}'.",
                exception);
        }
    }
}
