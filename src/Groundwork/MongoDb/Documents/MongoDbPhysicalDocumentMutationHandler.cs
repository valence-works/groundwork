using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>Observable transaction boundaries used by MongoDB mutation conformance tests.</summary>
internal enum MongoDbPhysicalMutationExecutionPoint
{
    BeforeCommit,
    AfterCommitBeforeAcknowledgement
}

/// <summary>
/// MongoDB executor for one certified bounded mutation source. The declared predicate is executed
/// by MongoDB inside the same snapshot transaction as the exact document writes and durable result.
/// </summary>
internal sealed class MongoDbPhysicalDocumentMutationHandler : IPhysicalDocumentMutationHandler
{
    private readonly MongoDbPhysicalDocumentStore store;
    private readonly ExecutableStorageRoute route;
    private readonly StorageUnitPhysicalStorage storage;
    private readonly Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept;

    internal MongoDbPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        MongoDbPhysicalDocumentStore store,
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications,
        IReadOnlyDictionary<string, string> nativeFieldIdentifiers,
        Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("A handler identity is required.", nameof(identity))
            : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Certifications = Array.AsReadOnly(
            (certifications ?? throw new ArgumentNullException(nameof(certifications))).ToArray());
        NativeFieldIdentifiers = source == PhysicalQuerySourceKind.NativeDocumentFields
            ? (nativeFieldIdentifiers ?? throw new ArgumentNullException(nameof(nativeFieldIdentifiers)))
                .ToFrozenDictionary(StringComparer.Ordinal)
            : FrozenDictionary<string, string>.Empty;
        this.intercept = intercept;
    }

    public string Identity { get; }

    public PhysicalQuerySourceKind Source { get; }

    public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
        Enum.GetValues<PortableQueryOperation>().ToFrozenSet();

    public IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; } =
        Enum.GetValues<BoundedMutationActionKind>().ToFrozenSet();

    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }

    public IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; }

    public bool SupportsCompoundPredicates => true;

    public bool SupportsDisjunction => true;

    public async Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        CancellationToken cancellationToken)
    {
        var scope = store.ResolveMutationScope(mutation.DocumentKind);
        if (scope.AcrossScopes || scope.StorageKey is null)
            throw new InvalidOperationException("Bounded mutations require one route-derived target scope.");
        var fingerprint = BoundedMutationRequestFingerprint.Create(mutation, plan, scope.StorageKey);
        var completed = false;
        return await store.ExecutePhysicalMutationAsync(
            mutation.DocumentKind,
            async (session, ct) =>
            {
                completed = false;
                var durable = await ReadOperationAsync(session, mutation, plan, scope, ct);
                if (durable is not null)
                {
                    if (!string.Equals(durable.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new BoundedMutationOperationConflictException(
                            mutation.OperationId,
                            fingerprint,
                            durable.Value.Fingerprint);
                    }
                    return BoundedMutationResult.Replayed(durable.Value.AffectedCount);
                }

                var affected = plan.Action is PhysicalDeleteMutationAction
                    ? await DeleteAsync(session, mutation, plan, scope, ct)
                    : await TransitionAsync(
                        session,
                        mutation,
                        plan,
                        scope,
                        (PhysicalTransitionMutationAction)plan.Action,
                        ct);
                await RecordOperationAsync(
                    session,
                    mutation,
                    plan,
                    scope,
                    fingerprint,
                    affected,
                    ct);
                completed = true;
                return BoundedMutationResult.Completed(affected);
            },
            intercept is null
                ? null
                : _ => completed
                    ? intercept(MongoDbPhysicalMutationExecutionPoint.BeforeCommit)
                    : ValueTask.CompletedTask,
            intercept is null
                ? null
                : _ => completed
                    ? intercept(MongoDbPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement)
                    : ValueTask.CompletedTask,
            cancellationToken);
    }

    internal FilterDefinition<BsonDocument> BuildPrimaryFilter(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) =>
        BuildMutationFilter(mutation, plan, scope, linked: false);

    internal FilterDefinition<BsonDocument> BuildLinkedFilter(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) =>
        BuildMutationFilter(mutation, plan, scope, linked: true);

    private async Task<long> DeleteAsync(
        IClientSessionHandle session,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        CancellationToken cancellationToken)
    {
        long? linkedCount = null;
        if (route.LinkedIndexStorage is not null)
        {
            var linked = await store.Database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
                .DeleteManyAsync(
                    session,
                    BuildLinkedFilter(mutation, plan, scope),
                    options: null,
                    cancellationToken);
            linkedCount = linked.DeletedCount;
        }
        var primary = await store.Database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .DeleteManyAsync(
                session,
                BuildPrimaryFilter(mutation, plan, scope),
                options: null,
                cancellationToken);
        if (linkedCount is { } actual)
            EnsureExactPhysicalCount("linked delete", primary.DeletedCount, actual);
        return primary.DeletedCount;
    }

    private async Task<long> TransitionAsync(
        IClientSessionHandle session,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        PhysicalTransitionMutationAction transition,
        CancellationToken cancellationToken)
    {
        var nativeValue = NativeValue(transition);
        var primaryUpdates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.Set(
                $"{route.Envelope.CanonicalJson.Identifier}.{transition.Path}",
                nativeValue),
            Builders<BsonDocument>.Update.Set(
                $"{MongoDbPhysicalStorageFields.NativeContent}.{transition.Path}",
                nativeValue),
            Builders<BsonDocument>.Update.Set(
                MongoDbPhysicalMutationStorage.Field(route.StorageUnit, transition.Path),
                MongoDbPhysicalMutationStorage.QueryValue(
                    route,
                    transition.Path,
                    transition.Field.ValueKind,
                    transition.TargetValue)),
            Builders<BsonDocument>.Update.Inc(route.Envelope.Version.Identifier, 1L),
            Builders<BsonDocument>.Update.Set(
                MongoDbPhysicalStorageFields.UpdatedAt,
                DateTime.UtcNow)
        };
        primaryUpdates.AddRange(route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                             column.Definition.Path == transition.Path)
            .Select(projection => Builders<BsonDocument>.Update.Set(
                projection.Column.Identifier,
                MongoDbPhysicalProjectionValues.ParseQueryValue(projection, transition.TargetValue))));
        var primary = await store.Database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .UpdateManyAsync(
                session,
                BuildPrimaryFilter(mutation, plan, scope),
                Builders<BsonDocument>.Update.Combine(primaryUpdates),
                cancellationToken: cancellationToken);
        EnsureExactPhysicalCount("primary transition modification", primary.MatchedCount, primary.ModifiedCount);

        if (route.LinkedIndexStorage is null)
            return primary.MatchedCount;
        var linkedUpdates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.Set(
                MongoDbPhysicalMutationStorage.Field(route.StorageUnit, transition.Path),
                MongoDbPhysicalMutationStorage.QueryValue(
                    route,
                    transition.Path,
                    transition.Field.ValueKind,
                    transition.TargetValue)),
            Builders<BsonDocument>.Update.Inc(MongoDbPhysicalStorageFields.LinkedPrimaryVersion, 1L)
        };
        linkedUpdates.AddRange(route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage &&
                             column.Definition.Path == transition.Path)
            .Select(projection => Builders<BsonDocument>.Update.Set(
                projection.Column.Identifier,
                MongoDbPhysicalProjectionValues.ParseQueryValue(projection, transition.TargetValue))));
        var linked = await store.Database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
            .UpdateManyAsync(
                session,
                BuildLinkedFilter(mutation, plan, scope),
                Builders<BsonDocument>.Update.Combine(linkedUpdates),
                cancellationToken: cancellationToken);
        EnsureExactPhysicalCount("linked transition", primary.MatchedCount, linked.MatchedCount);
        EnsureExactPhysicalCount("linked transition modification", linked.MatchedCount, linked.ModifiedCount);
        return primary.MatchedCount;
    }

    private FilterDefinition<BsonDocument> BuildMutationFilter(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        bool linked)
    {
        var query = PredicateQuery(mutation, plan);
        var discriminator = linked
            ? route.LinkedRelationship!.DocumentKind.Identifier
            : route.Envelope.DocumentKind.Identifier;
        var scopeField = linked
            ? route.LinkedRelationship!.StorageScope.Identifier
            : route.Envelope.StorageScope.Identifier;
        string Field(string path) => linked
            ? MongoDbPhysicalMutationStorage.LinkedField(route, path)
            : MongoDbPhysicalMutationStorage.PrimaryField(route, path);
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq(discriminator, route.Discriminator.Value),
            Builders<BsonDocument>.Filter.Eq(scopeField, scope.StorageKey)
        };
        var logicalIndex = storage.LogicalIndexes.Single(index =>
            index.Identity == plan.Predicate.LogicalIndexIdentity);
        if (logicalIndex.MissingValueBehavior == MissingValueBehavior.Excluded)
        {
            filters.AddRange(MongoDbPhysicalMutationStorage.IndexPaths(
                    storage,
                    storage.BoundedQueries.Single(candidate => candidate.Identity == plan.Predicate.QueryIdentity))
                .Select(path => Builders<BsonDocument>.Filter.Exists(Field(path), true)));
        }
        foreach (var clause in query.Clauses)
        {
            filters.Add(Builders<BsonDocument>.Filter.Or(clause.Comparisons.Select(comparison =>
            {
                var predicate = plan.Predicate.Predicates.Single(candidate =>
                    candidate.Path == comparison.Path);
                return Comparison(comparison, Field(comparison.Path), predicate.Field.ValueKind);
            })));
        }
        return Builders<BsonDocument>.Filter.And(filters);
    }

    private FilterDefinition<BsonDocument> Comparison(
        DocumentQueryComparison comparison,
        string field,
        IndexValueKind valueKind)
    {
        BsonValue ToValue(string? value) => MongoDbPhysicalMutationStorage.QueryValue(
            route,
            comparison.Path,
            valueKind,
            value);
        var value = comparison.Values.Count == 0 ? BsonNull.Value : ToValue(comparison.Values[0]);
        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => Builders<BsonDocument>.Filter.Eq(field, value),
            QueryComparisonOperator.NotEqual => Builders<BsonDocument>.Filter.Ne(field, value),
            QueryComparisonOperator.In => comparison.Values.Count == 0
                ? Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true)
                : Builders<BsonDocument>.Filter.In(field, comparison.Values.Select(ToValue)),
            QueryComparisonOperator.Contains => Builders<BsonDocument>.Filter.Regex(
                field,
                new BsonRegularExpression(Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.StartsWith => Builders<BsonDocument>.Filter.Regex(
                field,
                new BsonRegularExpression("^" + Regex.Escape(comparison.Values[0]!), "i")),
            QueryComparisonOperator.GreaterThan => Builders<BsonDocument>.Filter.Gt(field, value),
            QueryComparisonOperator.GreaterThanOrEqual => Builders<BsonDocument>.Filter.Gte(field, value),
            QueryComparisonOperator.LessThan => Builders<BsonDocument>.Filter.Lt(field, value),
            QueryComparisonOperator.LessThanOrEqual => Builders<BsonDocument>.Filter.Lte(field, value),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison.Operator, null)
        };
    }

    private async Task<(string Fingerprint, long AffectedCount)?> ReadOperationAsync(
        IClientSessionHandle session,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        CancellationToken cancellationToken)
    {
        var document = await store.Database.GetCollection<BsonDocument>(
                MongoDbPhysicalStorageFields.BoundedMutationOperationsCollection)
            .Find(session, Builders<BsonDocument>.Filter.Eq(
                MongoDbPhysicalStorageFields.Id,
                OperationIdentity(mutation, plan, scope)))
            .SingleOrDefaultAsync(cancellationToken);
        return document is null
            ? null
            : (document["request_fingerprint"].AsString, document["affected_count"].ToInt64());
    }

    private Task RecordOperationAsync(
        IClientSessionHandle session,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        string fingerprint,
        long affectedCount,
        CancellationToken cancellationToken) =>
        store.Database.GetCollection<BsonDocument>(MongoDbPhysicalStorageFields.BoundedMutationOperationsCollection).InsertOneAsync(
            session,
            new BsonDocument
            {
                [MongoDbPhysicalStorageFields.Id] = OperationIdentity(mutation, plan, scope),
                ["request_fingerprint"] = fingerprint,
                ["affected_count"] = affectedCount,
                ["completed_provider_version"] = plan.Predicate.Provider.Version,
                ["completed_utc"] = DateTime.UtcNow
            },
            cancellationToken: cancellationToken);

    private BsonDocument OperationIdentity(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) =>
        new()
        {
            ["manifest_id"] = store.ManifestIdentity,
            ["provider_name"] = plan.Predicate.Provider.Name,
            ["storage_unit"] = mutation.DocumentKind,
            ["storage_scope"] = scope.StorageKey!,
            ["operation_id"] = mutation.OperationId
        };

    private static DocumentQuery PredicateQuery(DocumentMutation mutation, PhysicalMutationPlan plan)
    {
        var clauses = mutation.Clauses.ToList();
        if (plan.Action is PhysicalTransitionMutationAction transition)
        {
            var predicate = plan.Predicate.Predicates.Single(item => item.Path == transition.Path);
            clauses.Add(predicate.Operations.Contains(PortableQueryOperation.In)
                ? DocumentQueryClause.Of(DocumentQueryComparison.In(
                    transition.Path,
                    transition.AllowedSourceValues))
                : DocumentQueryClause.AnyOf(transition.AllowedSourceValues
                    .Select(value => DocumentQueryComparison.Equal(transition.Path, value))
                    .ToArray()));
        }
        return new DocumentQuery(mutation.DocumentKind, plan.Predicate.QueryIdentity, clauses);
    }

    private static BsonValue NativeValue(PhysicalTransitionMutationAction transition) =>
        MongoDbCanonicalJson.Parse($"{{\"value\":{CanonicalJsonValue(transition)}}}")["value"];

    private static string CanonicalJsonValue(PhysicalTransitionMutationAction transition) =>
        transition.Field.ValueKind switch
        {
            IndexValueKind.Boolean or IndexValueKind.Number => transition.TargetValue,
            _ => System.Text.Json.JsonSerializer.Serialize(transition.TargetValue)
        };

    private static void EnsureExactPhysicalCount(string operation, long expected, long actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded mutation {operation} affected {actual} physical rows; expected exactly {expected}.");
        }
    }
}
