using System.Collections.Frozen;
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
    private readonly IReadOnlyDictionary<string, MongoDbPhysicalMutationBinding> bindings;
    private readonly Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept;

    internal MongoDbPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        MongoDbPhysicalDocumentStore store,
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> bindings,
        IReadOnlyDictionary<string, string> nativeFieldIdentifiers,
        Func<MongoDbPhysicalMutationExecutionPoint, ValueTask>? intercept)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("A handler identity is required.", nameof(identity))
            : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        var exactBindings = (bindings ?? throw new ArgumentNullException(nameof(bindings))).ToArray();
        if (exactBindings.Any(binding => binding.Schema.Route.Fingerprint != route.Fingerprint))
            throw new ArgumentException("One mutation handler cannot mix executable routes.", nameof(bindings));
        this.bindings = exactBindings.ToFrozenDictionary(
            binding => binding.Plan.MutationIdentity,
            StringComparer.Ordinal);
        Certifications = Array.AsReadOnly(exactBindings
            .Select(binding => new PhysicalMutationHandlerCertification(binding.Plan, binding.Certification))
            .ToArray());
        SupportedActions = exactBindings
            .Select(binding => binding.Plan.Action.Kind)
            .ToFrozenSet();
        NativeFieldIdentifiers = source == PhysicalQuerySourceKind.NativeDocumentFields
            ? (nativeFieldIdentifiers ?? throw new ArgumentNullException(nameof(nativeFieldIdentifiers)))
                .ToFrozenDictionary(StringComparer.Ordinal)
            : FrozenDictionary<string, string>.Empty;
        this.intercept = intercept;
    }

    public string Identity { get; }

    public PhysicalQuerySourceKind Source { get; }

    public IReadOnlySet<PortableQueryOperation> SupportedOperations =>
        MongoDbPhysicalMutationCapabilities.Operations;

    public IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; }

    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }

    public IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; }

    public bool SupportsCompoundPredicates => true;

    public bool SupportsDisjunction => true;

    public async Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        CancellationToken cancellationToken)
    {
        var invocation = BindInvocation(mutation, plan);
        var completed = false;
        return await store.ExecutePhysicalMutationAsync(
            mutation.DocumentKind,
            async (session, ct) =>
            {
                completed = false;
                var durable = await ReadOperationAsync(session, mutation, plan, invocation.Scope, ct);
                if (durable is not null)
                {
                    if (!string.Equals(durable.Value.Fingerprint, invocation.Fingerprint, StringComparison.Ordinal))
                    {
                        throw new BoundedMutationOperationConflictException(
                            mutation.OperationId,
                            invocation.Fingerprint,
                            durable.Value.Fingerprint);
                    }
                    return BoundedMutationResult.Replayed(durable.Value.AffectedCount);
                }

                var affected = plan.Action is PhysicalDeleteMutationAction
                    ? await DeleteAsync(session, invocation, ct)
                    : await TransitionAsync(session, invocation, ct);
                await RecordOperationAsync(
                    session,
                    mutation,
                    plan,
                    invocation.Scope,
                    invocation.Fingerprint,
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

    internal MongoDbPhysicalMutationInvocation BindInvocation(
        DocumentMutation mutation,
        PhysicalMutationPlan plan)
    {
        var binding = Binding(plan);
        var scope = store.ResolveMutationScope(mutation.DocumentKind);
        if (scope.AcrossScopes || scope.StorageKey is null)
            throw new InvalidOperationException("Bounded mutations require one route-derived target scope.");
        var primaryFilter = BuildMutationFilter(mutation, plan, binding.Schema.Primary, scope);
        var linkedFilter = binding.Schema.Linked is null
            ? null
            : BuildMutationFilter(mutation, plan, binding.Schema.Linked, scope);
        UpdateDefinition<BsonDocument>? primaryUpdate = null;
        UpdateDefinition<BsonDocument>? linkedUpdate = null;
        if (plan.Action is PhysicalTransitionMutationAction transition)
        {
            primaryUpdate = BuildPrimaryTransitionUpdate(binding, transition);
            if (binding.Schema.Linked is not null)
                linkedUpdate = BuildLinkedTransitionUpdate(binding, transition);
        }
        return new MongoDbPhysicalMutationInvocation(
            binding,
            scope,
            BoundedMutationRequestFingerprint.Create(mutation, plan, scope.StorageKey),
            primaryFilter,
            linkedFilter,
            primaryUpdate,
            linkedUpdate);
    }

    private async Task<long> DeleteAsync(
        IClientSessionHandle session,
        MongoDbPhysicalMutationInvocation invocation,
        CancellationToken cancellationToken)
    {
        long? linkedCount = null;
        if (invocation.Binding.Schema.Linked is not null)
        {
            var linked = await store.Database.GetCollection<BsonDocument>(invocation.Binding.Schema.Linked.StorageObject.Identifier)
                .DeleteManyAsync(
                    session,
                    invocation.LinkedFilter!,
                    new DeleteOptions { Hint = invocation.Binding.Schema.Linked.Index.Identifier },
                    cancellationToken);
            linkedCount = linked.DeletedCount;
        }
        var primary = await store.Database.GetCollection<BsonDocument>(invocation.Binding.Schema.Primary.StorageObject.Identifier)
            .DeleteManyAsync(
                session,
                invocation.PrimaryFilter,
                new DeleteOptions { Hint = invocation.Binding.Schema.Primary.Index.Identifier },
                cancellationToken);
        if (linkedCount is { } actual)
            EnsureExactPhysicalCount("linked delete", primary.DeletedCount, actual);
        return primary.DeletedCount;
    }

    private async Task<long> TransitionAsync(
        IClientSessionHandle session,
        MongoDbPhysicalMutationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var primary = await store.Database.GetCollection<BsonDocument>(invocation.Binding.Schema.Primary.StorageObject.Identifier)
            .UpdateManyAsync(
                session,
                invocation.PrimaryFilter,
                invocation.PrimaryUpdate!,
                new UpdateOptions { Hint = invocation.Binding.Schema.Primary.Index.Identifier },
                cancellationToken: cancellationToken);
        EnsureExactPhysicalCount("primary transition modification", primary.MatchedCount, primary.ModifiedCount);

        if (invocation.Binding.Schema.Linked is null)
            return primary.MatchedCount;
        var linked = await store.Database.GetCollection<BsonDocument>(invocation.Binding.Schema.Linked.StorageObject.Identifier)
            .UpdateManyAsync(
                session,
                invocation.LinkedFilter!,
                invocation.LinkedUpdate!,
                new UpdateOptions { Hint = invocation.Binding.Schema.Linked.Index.Identifier },
                cancellationToken: cancellationToken);
        EnsureExactPhysicalCount("linked transition", primary.MatchedCount, linked.MatchedCount);
        EnsureExactPhysicalCount("linked transition modification", linked.MatchedCount, linked.ModifiedCount);
        return primary.MatchedCount;
    }

    private UpdateDefinition<BsonDocument> BuildPrimaryTransitionUpdate(
        MongoDbPhysicalMutationBinding binding,
        PhysicalTransitionMutationAction transition)
    {
        var nativeValue = NativeValue(transition);
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.Set(
                $"{route.Envelope.CanonicalJson.Identifier}.{transition.Path}",
                nativeValue),
            Builders<BsonDocument>.Update.Set(
                $"{MongoDbPhysicalStorageFields.NativeContent}.{transition.Path}",
                nativeValue),
            Builders<BsonDocument>.Update.Set(
                binding.Schema.Primary.FieldByPath[transition.Path].Identifier,
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
        updates.AddRange(route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                             column.Definition.Path == transition.Path)
            .Select(projection => Builders<BsonDocument>.Update.Set(
                projection.Column.Identifier,
                MongoDbPhysicalProjectionValues.ParseQueryValue(projection, transition.TargetValue))));
        return Builders<BsonDocument>.Update.Combine(updates);
    }

    private UpdateDefinition<BsonDocument> BuildLinkedTransitionUpdate(
        MongoDbPhysicalMutationBinding binding,
        PhysicalTransitionMutationAction transition)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.Set(
                binding.Schema.Linked!.FieldByPath[transition.Path].Identifier,
                MongoDbPhysicalMutationStorage.QueryValue(
                    route,
                    transition.Path,
                    transition.Field.ValueKind,
                    transition.TargetValue)),
            Builders<BsonDocument>.Update.Inc(MongoDbPhysicalStorageFields.LinkedPrimaryVersion, 1L)
        };
        updates.AddRange(route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage &&
                             column.Definition.Path == transition.Path)
            .Select(projection => Builders<BsonDocument>.Update.Set(
                projection.Column.Identifier,
                MongoDbPhysicalProjectionValues.ParseQueryValue(projection, transition.TargetValue))));
        return Builders<BsonDocument>.Update.Combine(updates);
    }

    private FilterDefinition<BsonDocument> BuildMutationFilter(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        MongoDbPhysicalMutationSelector selector,
        DocumentScopeSelection scope)
    {
        var query = PredicateQuery(mutation, plan);
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq(selector.DiscriminatorField, selector.DiscriminatorValue),
            Builders<BsonDocument>.Filter.Eq(selector.ScopeField, scope.StorageKey)
        };
        if (selector.MissingValueBehavior == MissingValueBehavior.Excluded)
        {
            filters.AddRange(selector.Fields.Select(mirror =>
                Builders<BsonDocument>.Filter.Exists(mirror.Identifier, true)));
        }
        foreach (var clause in query.Clauses)
        {
            filters.Add(Builders<BsonDocument>.Filter.Or(clause.Comparisons.Select(comparison =>
            {
                var mirror = selector.FieldByPath[comparison.Path];
                return Comparison(comparison, mirror.Identifier, mirror.ValueKind);
            })));
        }
        return Builders<BsonDocument>.Filter.And(filters);
    }

    private MongoDbPhysicalMutationBinding Binding(PhysicalMutationPlan plan)
    {
        if (!bindings.TryGetValue(plan.MutationIdentity, out var binding) || !binding.Certifies(plan))
        {
            throw new InvalidOperationException(
                $"MongoDB bounded mutation '{plan.MutationIdentity}' does not match its executable binding.");
        }
        return binding;
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

internal sealed record MongoDbPhysicalMutationInvocation(
    MongoDbPhysicalMutationBinding Binding,
    DocumentScopeSelection Scope,
    string Fingerprint,
    FilterDefinition<BsonDocument> PrimaryFilter,
    FilterDefinition<BsonDocument>? LinkedFilter,
    UpdateDefinition<BsonDocument>? PrimaryUpdate,
    UpdateDefinition<BsonDocument>? LinkedUpdate);
