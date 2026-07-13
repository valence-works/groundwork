using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
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

                var identities = await SelectAsync(session, mutation, plan, scope, ct);
                if (plan.Action is PhysicalDeleteMutationAction)
                    await DeleteAsync(session, identities, ct);
                else
                    await TransitionAsync(session, identities, (PhysicalTransitionMutationAction)plan.Action, ct);
                await RecordOperationAsync(
                    session,
                    mutation,
                    plan,
                    scope,
                    fingerprint,
                    identities.Count,
                    ct);
                completed = true;
                return BoundedMutationResult.Completed(identities.Count);
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

    internal FilterDefinition<BsonDocument> BuildSelectionFilter(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) =>
        MongoDbPhysicalQueryHandler.BuildFilter(
            PredicateQuery(mutation, plan),
            plan.Predicate,
            scope,
            storage,
            route);

    private async Task<IReadOnlyList<DocumentIdentity>> SelectAsync(
        IClientSessionHandle session,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        CancellationToken cancellationToken)
    {
        var selected = await store.Database
            .GetCollection<BsonDocument>(plan.Predicate.LookupObject.Identifier)
            .Find(session, BuildSelectionFilter(mutation, plan, scope))
            .ToListAsync(cancellationToken);
        var identities = plan.Predicate.RequiresPrimaryLookup
            ? selected.Select(document => new DocumentIdentity(
                document[route.LinkedRelationship!.StorageScope.Identifier].AsString,
                document[route.LinkedRelationship.DocumentId.Identifier].AsString))
            : selected.Select(document => new DocumentIdentity(
                document[route.Envelope.StorageScope.Identifier].AsString,
                document[route.Envelope.Id.Identifier].AsString));
        return identities.Distinct().ToArray();
    }

    private async Task DeleteAsync(
        IClientSessionHandle session,
        IReadOnlyList<DocumentIdentity> identities,
        CancellationToken cancellationToken)
    {
        if (identities.Count == 0)
            return;
        if (route.LinkedIndexStorage is not null)
        {
            var linked = await store.Database.GetCollection<BsonDocument>(route.LinkedIndexStorage.Name.Identifier)
                .DeleteManyAsync(
                    session,
                    LinkedIdentityFilter(identities),
                    options: null,
                    cancellationToken);
            EnsureExactPhysicalCount("linked delete", identities.Count, linked.DeletedCount);
        }
        var primary = await store.Database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .DeleteManyAsync(
                session,
                PrimaryIdentityFilter(identities),
                options: null,
                cancellationToken);
        EnsureExactPhysicalCount("primary delete", identities.Count, primary.DeletedCount);
    }

    private async Task TransitionAsync(
        IClientSessionHandle session,
        IReadOnlyList<DocumentIdentity> identities,
        PhysicalTransitionMutationAction transition,
        CancellationToken cancellationToken)
    {
        if (identities.Count == 0)
            return;
        var primary = store.Database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var documents = await primary.Find(session, PrimaryIdentityFilter(identities)).ToListAsync(cancellationToken);
        EnsureExactPhysicalCount("primary transition selection", identities.Count, documents.Count);
        var nativeValue = NativeValue(transition);
        var primaryProjection = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                             column.Definition.Path == transition.Path)
            .ToArray();
        foreach (var document in documents)
        {
            var updates = new List<UpdateDefinition<BsonDocument>>
            {
                Builders<BsonDocument>.Update.Set(
                    route.Envelope.CanonicalJson.Identifier,
                    SetCanonicalValue(
                        document[route.Envelope.CanonicalJson.Identifier].AsString,
                        transition)),
                Builders<BsonDocument>.Update.Set(
                    $"{MongoDbPhysicalStorageFields.NativeContent}.{transition.Path}",
                    nativeValue),
                Builders<BsonDocument>.Update.Inc(route.Envelope.Version.Identifier, 1L),
                Builders<BsonDocument>.Update.Set(
                    MongoDbPhysicalStorageFields.UpdatedAt,
                    DateTime.UtcNow)
            };
            updates.AddRange(primaryProjection.Select(projection =>
                Builders<BsonDocument>.Update.Set(
                    projection.Column.Identifier,
                    MongoDbPhysicalProjectionValues.ParseQueryValue(projection, transition.TargetValue))));
            var result = await primary.UpdateOneAsync(
                session,
                Builders<BsonDocument>.Filter.Eq(
                    MongoDbPhysicalStorageFields.Id,
                    document[MongoDbPhysicalStorageFields.Id]),
                Builders<BsonDocument>.Update.Combine(updates),
                cancellationToken: cancellationToken);
            EnsureExactPhysicalCount("primary transition", 1, result.MatchedCount);
        }

        if (route.LinkedIndexStorage is null)
            return;
        var linkedUpdates = new List<UpdateDefinition<BsonDocument>>
        {
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
                LinkedIdentityFilter(identities),
                Builders<BsonDocument>.Update.Combine(linkedUpdates),
                cancellationToken: cancellationToken);
        EnsureExactPhysicalCount("linked transition", identities.Count, linked.MatchedCount);
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

    private FilterDefinition<BsonDocument> PrimaryIdentityFilter(
        IReadOnlyList<DocumentIdentity> identities) =>
        Builders<BsonDocument>.Filter.Or(identities.Select(identity =>
            Builders<BsonDocument>.Filter.Eq(route.Envelope.DocumentKind.Identifier, route.Discriminator.Value) &
            Builders<BsonDocument>.Filter.Eq(route.Envelope.StorageScope.Identifier, identity.Scope) &
            Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, identity.Id)));

    private FilterDefinition<BsonDocument> LinkedIdentityFilter(
        IReadOnlyList<DocumentIdentity> identities) =>
        Builders<BsonDocument>.Filter.Or(identities.Select(identity =>
            Builders<BsonDocument>.Filter.Eq(
                route.LinkedRelationship!.DocumentKind.Identifier,
                route.Discriminator.Value) &
            Builders<BsonDocument>.Filter.Eq(route.LinkedRelationship.StorageScope.Identifier, identity.Scope) &
            Builders<BsonDocument>.Filter.Eq(route.LinkedRelationship.DocumentId.Identifier, identity.Id)));

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
        BsonDocument.Parse($"{{\"value\":{CanonicalJsonValue(transition)}}}")["value"];

    private static string SetCanonicalValue(
        string canonicalJson,
        PhysicalTransitionMutationAction transition)
    {
        var root = JsonNode.Parse(canonicalJson) as JsonObject
            ?? throw new InvalidDataException("A physical document mutation requires a JSON object document.");
        var segments = transition.Path.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw new InvalidDataException("A physical document mutation path cannot be empty.");
        var current = root;
        foreach (var segment in segments[..^1])
        {
            current = current[segment] as JsonObject
                ?? throw new InvalidDataException(
                    $"Physical document mutation path '{transition.Path}' does not resolve to an object.");
        }
        current[segments[^1]] = JsonNode.Parse(CanonicalJsonValue(transition));
        return root.ToJsonString();
    }

    private static string CanonicalJsonValue(PhysicalTransitionMutationAction transition) =>
        transition.Field.ValueKind switch
        {
            IndexValueKind.Boolean => JsonSerializer.Serialize(bool.Parse(transition.TargetValue)),
            IndexValueKind.Number => transition.TargetValue,
            _ => JsonSerializer.Serialize(transition.TargetValue)
        };

    private static void EnsureExactPhysicalCount(string operation, long expected, long actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded mutation {operation} affected {actual} physical rows; expected exactly {expected}.");
        }
    }

    private readonly record struct DocumentIdentity(string Scope, string Id);
}
