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
public sealed class MongoDbPhysicalDocumentStore :
    IDocumentStore,
    IBoundedDocumentStore,
    IPhysicalDocumentQueryExplainer
{
    private const string ContentField = MongoDbPhysicalStorageFields.NativeContent;
    private const string CreatedField = MongoDbPhysicalStorageFields.CreatedAt;
    private const string UpdatedField = MongoDbPhysicalStorageFields.UpdatedAt;
    private readonly MongoDbPhysicalDocumentStoreRuntime runtime;
    private readonly IStorageScopeObserver scopeObserver;
    private readonly IReadOnlyDictionary<string, PhysicalQueryDocumentStore> queryStores;
    private IMongoDatabase database => runtime.Database;
    private MongoDbPhysicalStorageModel model => runtime.Model;
    private MongoDbPhysicalDocumentStoreOptions options => runtime.Options;
    private TimeProvider timeProvider => runtime.TimeProvider;
    private MongoDbPhysicalDocumentStoreExecutionHooks hooks => runtime.Hooks;
    private Func<CancellationToken, Task<IClientSessionHandle>> startSessionAsync => runtime.StartSessionAsync;
    private MongoDbTransactionCapability transactionCapability => runtime.TransactionCapability;

    internal MongoDbPhysicalDocumentStore(
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
        : this(
            new MongoDbPhysicalDocumentStoreRuntime(
                database,
                model,
                options,
                timeProvider,
                hooks,
                startSessionAsync,
                transactionCapability),
            access,
            scopeObserver)
    {
    }

    private MongoDbPhysicalDocumentStore(
        MongoDbPhysicalDocumentStoreRuntime runtime,
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        this.scopeObserver = scopeObserver ?? NullStorageScopeObserver.Instance;
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
            () => ClassifyDuplicateIdentityAsync(route, request.Id, scope.StorageKey!, cancellationToken),
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
            duplicateKeyResult: null,
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

    /// <summary>
    /// Returns ordered native MongoDB evidence. Linked queries can execute the bounded selector to
    /// derive exact hydration identities; this sensitive diagnostic operation can therefore be costly.
    /// </summary>
    public Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default) =>
        QueryStore(query.DocumentKind).ExplainAsync(query, cancellationToken);

    public PhysicalQueryPlan ResolvePlan(
        DocumentQuery query,
        BoundedQueryResultOperation operation = BoundedQueryResultOperation.Documents) =>
        QueryStore(query.DocumentKind).ResolvePlan(query, operation);

    private PhysicalQueryDocumentStore CreateQueryStore(ExecutableStorageRoute route)
    {
        var storage = model.StorageByStorageUnit[route.StorageUnit.Value];
        var capabilities = Capabilities(route, storage);
        var plans = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        if (!plans.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, plans.Diagnostics.Select(x => $"{x.Code}: {x.Message}")));

        ValidateScaleBearingOperations(storage);
        ValidateTypedPaths(route, storage);
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
        return PhysicalQueryDocumentStore.FromCompiledPlans(plans.Plans, capabilities, handlers);
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
        foreach (var query in storage.BoundedQueries.Where(query =>
                     query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            var unsupported = MongoDbScaleBearingOperationValidation.UnsupportedQueryOperations(storage, query);
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
            plan.RequiredFields
                .GroupBy(field => field.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal),
            plan.RouteFingerprint);

    private PhysicalQueryPlannerCapabilities Capabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage) =>
        MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);

    private async Task<DocumentStoreWriteResult> ExecuteAtomicAsync(
        IReadOnlyList<string> documentKinds,
        Func<IClientSessionHandle, Task<DocumentStoreWriteResult>> action,
        Func<Task<DocumentStoreWriteResult>>? duplicateKeyResult,
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
                return duplicateKeyResult is null
                    ? DocumentStoreWriteResult.ConcurrencyConflict
                    : await duplicateKeyResult();
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
        if (current is not null)
        {
            var authoritativeId = current[route.Envelope.Identity.OriginalId.Identifier].AsString;
            if (!string.Equals(authoritativeId, request.Id, StringComparison.Ordinal))
                return DocumentStoreWriteResult.IdentityConflict(authoritativeId);
        }
        if (current is not null && request.ExpectedVersion is not null && current[route.Envelope.Version.Identifier].ToInt64() != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;
        if (current is null && request.ExpectedVersion is { } expected && expected != 0)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var created = current is null ? now : new DateTimeOffset(current[CreatedField].ToUniversalTime());
        var version = current is null ? 1 : current[route.Envelope.Version.Identifier].ToInt64() + 1;
        var incarnation = current?.GetValue(MongoDbPhysicalStorageFields.Incarnation).AsString ?? Guid.NewGuid().ToString("N");
        var content = MongoDbCanonicalJson.Parse(request.ContentJson);
        var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(request.ContentJson, route.ProjectedColumns);
        var document = CreatePrimary(
            route,
            GetMutationBindings(route.StorageUnit.Value),
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
            var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, request.Id, scope.StorageKey!);
            if (request.ExpectedVersion is not null)
                filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
            var result = await primary.ReplaceOneAsync(session, filter, document, cancellationToken: cancellationToken);
            if (result.MatchedCount == 0)
                return DocumentStoreWriteResult.ConcurrencyConflict;
        }
        await MaintainLinkedAsync(
            route,
            GetMutationBindings(route.StorageUnit.Value),
            document,
            projectedValues,
            session,
            cancellationToken);
        return DocumentStoreWriteResult.Saved(ReadEnvelope(route, document));
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(
        DeleteDocumentRequest request,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, request.Id, scope.StorageKey!);
        if (request.ExpectedVersion is not null)
            filter &= Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, request.ExpectedVersion.Value);
        var deleted = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .FindOneAndDeleteAsync(session, filter, cancellationToken: cancellationToken);
        if (deleted is null)
        {
            var exists = await LoadDocumentAsync(route, request.Id, scope.StorageKey!, session, cancellationToken);
            return exists is null ? DocumentStoreWriteResult.NotFound : DocumentStoreWriteResult.ConcurrencyConflict;
        }
        if (route.LinkedIndexStorage is not null)
        {
            var linkedFilter = MongoDbPhysicalDocumentIdentity.LinkedExactFilter(
                route,
                request.Id,
                scope.StorageKey!);
            await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
                .DeleteOneAsync(session, linkedFilter, cancellationToken: cancellationToken);
        }
        return DocumentStoreWriteResult.Deleted(deleted[route.Envelope.Id.Identifier].AsString);
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
        var identity = route.Envelope.Identity.Project(id);
        var filter = MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, identity, scope);
        var exact = session is null
            ? await collection.Find(filter).SingleOrDefaultAsync(cancellationToken)
            : await collection.Find(session, filter).SingleOrDefaultAsync(cancellationToken);
        if (exact is not null)
            return exact;

        var lookupFilter = MongoDbPhysicalDocumentIdentity.PrimaryLookupFilter(
            route,
            scope,
            identity.LookupKey);
        var retained = session is null
            ? await collection.Find(lookupFilter).SingleOrDefaultAsync(cancellationToken)
            : await collection.Find(session, lookupFilter).SingleOrDefaultAsync(cancellationToken);
        if (retained is null)
            return null;
        MongoDbPhysicalDocumentIdentity.ThrowIfCollision(route, identity, retained);

        // A matching identity may be inserted after the exact read reports no document and before
        // the collision-evidence fallback runs. The retained comparison key is the authoritative
        // exact evidence in that race; a different comparison key still fails closed above.
        return retained;
    }

    private async Task<DocumentStoreWriteResult> ClassifyDuplicateIdentityAsync(
        ExecutableStorageRoute route,
        string requestedId,
        string scope,
        CancellationToken cancellationToken)
    {
        var requested = route.Envelope.Identity.Project(requestedId);
        var retained = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(MongoDbPhysicalDocumentIdentity.PrimaryLookupFilter(route, scope, requested.LookupKey))
            .SingleOrDefaultAsync(cancellationToken);
        if (retained is null)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        MongoDbPhysicalDocumentIdentity.ThrowIfCollision(route, requested, retained);
        var authoritativeId = retained[route.Envelope.Identity.OriginalId.Identifier].AsString;
        return string.Equals(authoritativeId, requestedId, StringComparison.Ordinal)
            ? DocumentStoreWriteResult.ConcurrencyConflict
            : DocumentStoreWriteResult.IdentityConflict(authoritativeId);
    }

    private static BsonDocument CreatePrimary(
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> mutationBindings,
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
            [route.Envelope.DocumentKind.Identifier] = route.Discriminator.Value,
            [route.Envelope.StorageScope.Identifier] = scope,
            [route.Envelope.Version.Identifier] = version,
            [route.Envelope.SchemaVersion.Identifier] = request.SchemaVersion,
            [route.Envelope.CanonicalJson.Identifier] = content.DeepClone(),
            [MongoDbPhysicalStorageFields.Incarnation] = incarnation,
            [ContentField] = content,
            [CreatedField] = created.UtcDateTime,
            [UpdatedField] = updated.UtcDateTime
        };
        MongoDbPhysicalDocumentIdentity.WritePrimary(document, route, request.Id);
        foreach (var projection in route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage))
        {
            var value = projectedValues[projection];
            if (value.IsPresent)
                document[projection.Column.Identifier] = value.Value;
        }
        MongoDbPhysicalMutationStorage.ApplyMirrors(
            document,
            document,
            content,
            route,
            mutationBindings,
            ExecutableStorageObjectRole.PrimaryStorage,
            projectedValues);
        document[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(route.PrimaryKey, document);
        return document;
    }

    private async Task MaintainLinkedAsync(
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> mutationBindings,
        BsonDocument primary,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (route.LinkedIndexStorage is null)
            return;
        var linked = MongoDbLinkedDocumentStorage.Create(route, primary, projectedValues);
        MongoDbPhysicalMutationStorage.ApplyMirrors(
            linked.Document,
            primary,
            primary[ContentField].AsBsonDocument,
            route,
            mutationBindings,
            ExecutableStorageObjectRole.LinkedIndexStorage,
            projectedValues);
        await database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier).ReplaceOneAsync(
            session,
            Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Id, linked.Identity),
            linked.Document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    internal static DocumentEnvelope ReadEnvelope(ExecutableStorageRoute route, BsonDocument document) =>
        new(
            route.StorageUnit.Value,
            document[route.Envelope.Id.Identifier].AsString,
            document[route.Envelope.SchemaVersion.Identifier].AsString,
            document[route.Envelope.Version.Identifier].ToInt64(),
            MongoDbCanonicalJson.Serialize(document[route.Envelope.CanonicalJson.Identifier]),
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

    internal MongoDbPhysicalDocumentStore WithAccess(
        DocumentStoreAccess access,
        IStorageScopeObserver? scopeObserver) =>
        new(runtime, access, scopeObserver);

    internal string ManifestIdentity => model.Manifest.Identity.Value;

    internal StorageManifest Manifest => model.Manifest;

    internal ProviderIdentity Provider => model.Provider;

    internal ExecutableStorageRoute GetRoute(string documentKind) => Route(documentKind);

    internal DocumentScopeSelection ResolveMutationScope(string documentKind) =>
        ResolveScope(Unit(documentKind), StorageScopeOperation.Mutate);

    internal PhysicalQueryPlannerCapabilities GetMutationCapabilities(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage) =>
        MongoDbPhysicalMutationCapabilities.Create(route, storage, model.Provider);

    internal IReadOnlyList<MongoDbPhysicalMutationBinding> GetMutationBindings(string documentKind) =>
        model.MutationBindingsByStorageUnit.TryGetValue(documentKind, out var bindings)
            ? bindings
            : [];

    internal Task EnsureMutationSupportedAsync(string documentKind, CancellationToken cancellationToken) =>
        transactionCapability.EnsureSupportedAsync(
            [documentKind],
            "physical bounded mutation explain",
            cancellationToken);

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
            var (route, selection) = store.ResolveOperation(request.DocumentKind, StorageScopeOperation.Save);
            try
            {
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
                return IsDuplicateKey(exception)
                    ? await store.ClassifyDuplicateIdentityAsync(
                        route,
                        request.Id,
                        selection.StorageKey!,
                        cancellationToken)
                    : DocumentStoreWriteResult.ConcurrencyConflict;
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

internal sealed class MongoDbPhysicalDocumentStoreRuntime
{
    public MongoDbPhysicalDocumentStoreRuntime(
        IMongoDatabase database,
        MongoDbPhysicalStorageModel model,
        MongoDbPhysicalDocumentStoreOptions? options,
        TimeProvider timeProvider,
        MongoDbPhysicalDocumentStoreExecutionHooks? hooks,
        Func<CancellationToken, Task<IClientSessionHandle>>? startSessionAsync,
        MongoDbTransactionCapability? transactionCapability)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority);
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Options = options ?? new MongoDbPhysicalDocumentStoreOptions();
        Options.Validate();
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Hooks = hooks ?? MongoDbPhysicalDocumentStoreExecutionHooks.None;
        StartSessionAsync = startSessionAsync ??
            (ct => Database.Client.StartSessionAsync(cancellationToken: ct));
        TransactionCapability = transactionCapability ?? MongoDbTransactionCapability.ForDatabase(Database);
    }

    public IMongoDatabase Database { get; }
    public MongoDbPhysicalStorageModel Model { get; }
    public MongoDbPhysicalDocumentStoreOptions Options { get; }
    public TimeProvider TimeProvider { get; }
    public MongoDbPhysicalDocumentStoreExecutionHooks Hooks { get; }
    public Func<CancellationToken, Task<IClientSessionHandle>> StartSessionAsync { get; }
    public MongoDbTransactionCapability TransactionCapability { get; }
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

internal sealed record MongoDbPhysicalQueryPredicate(
    FilterDefinition<BsonDocument> Filter,
    IReadOnlyList<string> FieldIdentifiers);

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
    private readonly MongoDbPhysicalQueryExplainer explainer;

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
        explainer = new MongoDbPhysicalQueryExplainer(
            database,
            route,
            storage,
            scope,
            transactionCapability);
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
    public bool SupportsKeysetPaging => true;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => true;

    public async Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var resolvedScope = scope();
        DocumentQueryContinuationCodec.ValidateScope(plan, resolvedScope);
        var basePredicate = BuildPredicate(query, plan, resolvedScope, storage, route);
        var pagePredicate = BuildPagePredicate(query, plan, resolvedScope, basePredicate);
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
                if (query.LatestPerKeyPath is not null)
                {
                    var renderedFilter = RenderFilter(collection, basePredicate.Filter);
                    var latestTotal = await CountLatestPerKeyAsync(
                        collection,
                        session,
                        renderedFilter,
                        query,
                        plan,
                        cancellationToken);
                    await hooks.QueryCountRead(session, attempt, cancellationToken);
                    if (latestTotal == 0 || query.Take == 0)
                    {
                        await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                        return new DocumentQueryResult([], latestTotal);
                    }

                    var latestFound = await collection.Aggregate<BsonDocument>(
                            session,
                            LatestPerKeyPagePipeline(renderedFilter, query, plan).ToArray())
                        .ToListAsync(cancellationToken);
                    await hooks.QueryPageRead(session, attempt, cancellationToken);
                    var latestDocuments = plan.RequiresPrimaryLookup
                        ? await LoadPrimaryAsync(session, latestFound, attempt, cancellationToken)
                        : latestFound.Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
                    await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                    return new DocumentQueryResult(latestDocuments, latestTotal);
                }

                var total = await collection.CountDocumentsAsync(
                    session,
                    basePredicate.Filter,
                    cancellationToken: cancellationToken);
                await hooks.QueryCountRead(session, attempt, cancellationToken);
                if (total == 0 || query.Take == 0)
                {
                    await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                    return new DocumentQueryResult([], total);
                }
                var find = collection.Find(session, pagePredicate.Filter).Sort(sort)
                    .Skip(plan.PagingSupport == QueryPagingSupport.Cursor ? 0 : query.Skip ?? 0);
                find = find.Limit(PageReadLimit(query, plan));
                var found = (await find.ToListAsync(cancellationToken)).ToList();
                await hooks.QueryPageRead(session, attempt, cancellationToken);
                var hasMore = query.Take is { } take &&
                              take < int.MaxValue &&
                              found.Count > take;
                if (hasMore)
                    found.RemoveAt(found.Count - 1);
                var documents = plan.RequiresPrimaryLookup
                    ? await LoadPrimaryAsync(session, found, attempt, cancellationToken)
                    : found.Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
                var next = hasMore && found.Count != 0
                    ? DocumentQueryContinuationCodec.Encode(
                        query,
                        plan,
                        resolvedScope,
                        ReadContinuationValues(found[^1], query, plan))
                    : null;
                await MongoDbPhysicalDocumentStore.AbortTransactionIgnoringFailureAsync(session);
                return new DocumentQueryResult(documents, total, next);
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
        var collection = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var filter = BuildFilter(query, plan, scope(), storage, route);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical count query",
            cancellationToken);
        return query.LatestPerKeyPath is null
            ? await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken)
            : await CountLatestPerKeyAsync(
                collection,
                session: null,
                RenderFilter(collection, filter),
                query,
                plan,
                cancellationToken);
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

    public Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
        explainer.ExplainAsync(query, plan, cancellationToken);

    internal static FilterDefinition<BsonDocument> BuildFilter(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        StorageUnitPhysicalStorage storage,
        ExecutableStorageRoute route) =>
        BuildPredicate(query, plan, scope, storage, route).Filter;

    internal static MongoDbPhysicalQueryPredicate BuildPredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        StorageUnitPhysicalStorage storage,
        ExecutableStorageRoute route)
    {
        var fieldIdentifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            plan.Discriminator.Identifier
        };
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq(plan.Discriminator.Identifier, plan.StorageUnit.Value)
        };
        if (scope.StorageKey is not null)
        {
            filters.Add(Builders<BsonDocument>.Filter.Eq(plan.Scope.Field.Identifier, scope.StorageKey));
            fieldIdentifiers.Add(plan.Scope.Field.Identifier);
        }
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
            fieldIdentifiers.UnionWith(membershipFields);
        }
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
            {
                const string matchNone = "_groundwork_match_none";
                return new MongoDbPhysicalQueryPredicate(
                    Builders<BsonDocument>.Filter.Eq(matchNone, true),
                    [matchNone]);
            }
            fieldIdentifiers.UnionWith(clause.Comparisons.Select(comparison =>
                plan.Predicates.Single(predicate => predicate.Path == comparison.Path).Field.Identifier));
            filters.Add(Builders<BsonDocument>.Filter.Or(clause.Comparisons.Select(comparison =>
                Comparison(
                    comparison,
                    plan,
                    plan.Predicates.Single(predicate => predicate.Path == comparison.Path).Field,
                    route))));
        }
        return new MongoDbPhysicalQueryPredicate(
            Builders<BsonDocument>.Filter.And(filters),
            fieldIdentifiers.Order(StringComparer.Ordinal).ToArray());
    }

    internal static MongoDbPhysicalQueryPredicate BuildPagePredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        MongoDbPhysicalQueryPredicate basePredicate)
    {
        if (query.Continuation is null)
            return basePredicate;
        var values = DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope);
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        return new MongoDbPhysicalQueryPredicate(
            Builders<BsonDocument>.Filter.And(
                basePredicate.Filter,
                ContinuationFilter(order, values)),
            basePredicate.FieldIdentifiers
                .Concat(order.Select(item => item.Field.Identifier))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    internal static SortDefinition<BsonDocument> BuildSort(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var requested = DocumentQueryOrderResolver.Resolve(query, plan);
        return Builders<BsonDocument>.Sort.Combine(requested.Select(order =>
            order.Direction == PhysicalSortDirection.Ascending
                ? Builders<BsonDocument>.Sort.Ascending(order.Field.Identifier)
                : Builders<BsonDocument>.Sort.Descending(order.Field.Identifier)));
    }

    internal static IReadOnlyList<BsonDocument> LatestPerKeyPagePipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var pipeline = LatestPerKeySelectionPipeline(renderedFilter, query, plan).ToList();
        if (query.Skip is { } skip && skip != 0)
            pipeline.Add(new BsonDocument("$skip", skip));
        pipeline.Add(new BsonDocument("$limit", PageReadLimit(query, plan)));
        return pipeline;
    }

    internal static IReadOnlyList<BsonDocument> LatestPerKeyCountPipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var group = LatestPerKeyField(query, plan);
        return
        [
            new BsonDocument("$match", renderedFilter),
            new BsonDocument("$group", new BsonDocument("_id", $"${group.Identifier}")),
            new BsonDocument("$count", "value")
        ];
    }

    private static IReadOnlyList<BsonDocument> LatestPerKeySelectionPipeline(
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan)
    {
        var group = LatestPerKeyField(query, plan);
        var sort = SortDocument(query, plan);
        return
        [
            new BsonDocument("$match", renderedFilter),
            new BsonDocument("$sort", sort),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = $"${group.Identifier}",
                ["document"] = new BsonDocument("$first", "$$ROOT")
            }),
            new BsonDocument("$replaceWith", "$document"),
            new BsonDocument("$sort", sort)
        ];
    }

    internal static BsonDocument SortDocument(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var sort = new BsonDocument();
        foreach (var order in DocumentQueryOrderResolver.Resolve(query, plan))
            sort[order.Field.Identifier] = order.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        return sort;
    }

    private static PhysicalQueryField LatestPerKeyField(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var path = query.LatestPerKeyPath
                   ?? throw new InvalidOperationException("Latest-per-key execution requires a grouping path.");
        return plan.Order.Single(order => !order.IsIdentityTieBreak && order.Path == path).Field;
    }

    private static BsonDocument RenderFilter(
        IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(
            collection.DocumentSerializer,
            BsonSerializer.SerializerRegistry));

    private static async Task<long> CountLatestPerKeyAsync(
        IMongoCollection<BsonDocument> collection,
        IClientSessionHandle? session,
        BsonDocument renderedFilter,
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken)
    {
        var pipeline = LatestPerKeyCountPipeline(renderedFilter, query, plan);
        var documents = session is null
            ? await collection.Aggregate<BsonDocument>(pipeline.ToArray()).ToListAsync(cancellationToken)
            : await collection.Aggregate<BsonDocument>(session, pipeline.ToArray()).ToListAsync(cancellationToken);
        return documents.FirstOrDefault()?["value"].ToInt64() ?? 0L;
    }

    private static FilterDefinition<BsonDocument> ContinuationFilter(
        IReadOnlyList<PhysicalQueryOrder> order,
        IReadOnlyList<DocumentQueryContinuationValue> values)
    {
        var alternatives = new List<FilterDefinition<BsonDocument>>();
        for (var boundaryIndex = 0; boundaryIndex < order.Count; boundaryIndex++)
        {
            var conjunction = new List<FilterDefinition<BsonDocument>>();
            for (var prefixIndex = 0; prefixIndex < boundaryIndex; prefixIndex++)
            {
                conjunction.Add(Builders<BsonDocument>.Filter.Eq(
                    order[prefixIndex].Field.Identifier,
                    ToBsonValue(values[prefixIndex])));
            }

            conjunction.Add(ContinuationAfter(order[boundaryIndex], values[boundaryIndex]));
            alternatives.Add(Builders<BsonDocument>.Filter.And(conjunction));
        }
        return Builders<BsonDocument>.Filter.Or(alternatives);
    }

    private static FilterDefinition<BsonDocument> ContinuationAfter(
        PhysicalQueryOrder order,
        DocumentQueryContinuationValue value)
    {
        var field = order.Field.Identifier;
        if (value.ScalarKind == DocumentQueryContinuationScalarKind.Null)
        {
            return order.Direction == PhysicalSortDirection.Ascending
                ? Builders<BsonDocument>.Filter.Ne(field, BsonNull.Value)
                : Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true);
        }

        var boundary = ToBsonValue(value);
        return order.Direction == PhysicalSortDirection.Ascending
            ? Builders<BsonDocument>.Filter.Gt(field, boundary)
            : Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Lt(field, boundary),
                Builders<BsonDocument>.Filter.Eq(field, BsonNull.Value));
    }

    internal static int PageReadLimit(DocumentQuery query, PhysicalQueryPlan plan) =>
        plan.PagingSupport == QueryPagingSupport.Cursor &&
        query.Take is { } take &&
        take < int.MaxValue
            ? take + 1
            : query.Take ?? int.MaxValue;

    private static IReadOnlyList<DocumentQueryContinuationValue> ReadContinuationValues(
        BsonDocument document,
        DocumentQuery query,
        PhysicalQueryPlan plan) =>
        DocumentQueryOrderResolver.Resolve(query, plan)
            .Select(order =>
                TryReadDotted(document, order.Field.Identifier, out var value)
                    ? FromBsonValue(order.Field.ValueKind, value)
                    : new DocumentQueryContinuationValue(
                        order.Field.ValueKind,
                        DocumentQueryContinuationScalarKind.Null,
                        null))
            .ToArray();

    private static DocumentQueryContinuationValue FromBsonValue(IndexValueKind kind, BsonValue value)
    {
        if (value.IsBsonNull)
            return new(kind, DocumentQueryContinuationScalarKind.Null, null);
        return value.BsonType switch
        {
            BsonType.String => new(kind, DocumentQueryContinuationScalarKind.String, value.AsString),
            BsonType.Int32 => new(
                kind,
                DocumentQueryContinuationScalarKind.Int64,
                value.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Int64 => new(
                kind,
                DocumentQueryContinuationScalarKind.Int64,
                value.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Double => new(
                kind,
                DocumentQueryContinuationScalarKind.Double,
                value.AsDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Decimal128 => new(
                kind,
                DocumentQueryContinuationScalarKind.Decimal,
                value.AsDecimal128.ToString()),
            BsonType.Boolean => new(
                kind,
                DocumentQueryContinuationScalarKind.Boolean,
                value.AsBoolean.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.DateTime => new(
                kind,
                DocumentQueryContinuationScalarKind.DateTimeOffset,
                new DateTimeOffset(value.ToUniversalTime()).ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture)),
            BsonType.Binary => new(
                kind,
                DocumentQueryContinuationScalarKind.Binary,
                Convert.ToBase64String(value.AsBsonBinaryData.Bytes)),
            _ => throw new InvalidOperationException(
                $"MongoDB physical query order returned unsupported BSON type '{value.BsonType}'.")
        };
    }

    private static BsonValue ToBsonValue(DocumentQueryContinuationValue value) =>
        value.ScalarKind switch
        {
            DocumentQueryContinuationScalarKind.Null => BsonNull.Value,
            DocumentQueryContinuationScalarKind.String => new BsonString(value.Value!),
            DocumentQueryContinuationScalarKind.Int64 => new BsonInt64(long.Parse(
                value.Value!,
                System.Globalization.CultureInfo.InvariantCulture)),
            DocumentQueryContinuationScalarKind.Decimal => new BsonDecimal128(Decimal128.Parse(value.Value!)),
            DocumentQueryContinuationScalarKind.Double => new BsonDouble(double.Parse(
                value.Value!,
                System.Globalization.CultureInfo.InvariantCulture)),
            DocumentQueryContinuationScalarKind.Boolean => new BsonBoolean(bool.Parse(value.Value!)),
            DocumentQueryContinuationScalarKind.DateTimeOffset => new BsonDateTime(DateTimeOffset.Parse(
                    value.Value!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)
                .UtcDateTime),
            DocumentQueryContinuationScalarKind.Binary => new BsonBinaryData(Convert.FromBase64String(value.Value!)),
            _ => throw new InvalidDocumentQueryContinuationException(
                "The document-query continuation contains an unsupported MongoDB physical value.")
        };

    private static bool TryReadDotted(BsonDocument document, string path, out BsonValue value)
    {
        value = document;
        foreach (var segment in path.Split('.'))
        {
            if (value is not BsonDocument current || !current.TryGetValue(segment, out value!))
            {
                value = BsonNull.Value;
                return false;
            }
        }
        return true;
    }

    private async Task<IReadOnlyList<DocumentEnvelope>> LoadPrimaryAsync(
        IClientSessionHandle session,
        IReadOnlyList<BsonDocument> linked,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (linked.Count == 0) return [];
        var rel = route.LinkedRelationship!;
        var filters = linked.Select(document =>
            MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, document));
        await hooks.QueryPrimaryHydrationStarting(session, attempt, cancellationToken);
        var primary = await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(session, Builders<BsonDocument>.Filter.Or(filters))
            .Limit(linked.Count)
            .ToListAsync(cancellationToken);
        var byKey = primary.ToDictionary(document => Key(
            document,
            route.Envelope.Identity,
            route.Envelope.StorageScope));
        return linked.Select(document => byKey[Key(document, rel.Identity, rel.StorageScope)])
            .Select(document => MongoDbPhysicalDocumentStore.ReadEnvelope(route, document)).ToArray();
    }

    private static DocumentIdentity Key(
        BsonDocument document,
        ExecutableDocumentIdentityRoute identity,
        ExecutableColumnRoute scope) =>
        new(
            document[scope.Identifier].AsString,
            document[identity.LookupKey.Identifier].AsString,
            document[identity.ComparisonKey.Identifier].AsString);

    private readonly record struct DocumentIdentity(string Scope, string LookupKey, string ComparisonKey);

    private static FilterDefinition<BsonDocument> Comparison(
        DocumentQueryComparison comparison,
        PhysicalQueryPlan plan,
        PhysicalQueryField queryField,
        ExecutableStorageRoute route)
    {
        if (comparison.Path == PhysicalDocumentFieldPaths.Id)
            return MongoDbPhysicalIdentityQuery.Build(comparison, plan);

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
            QueryComparisonOperator.NotContains => Builders<BsonDocument>.Filter.Not(
                Builders<BsonDocument>.Filter.Regex(field, new BsonRegularExpression(Regex.Escape(comparison.Values[0]!), "i"))),
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
