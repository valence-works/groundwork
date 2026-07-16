using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>Builds ordered native evidence for one complete MongoDB bounded-query invocation.</summary>
/// <remarks>
/// Linked queries execute the exact bounded lookup selector to derive the primary identities whose
/// hydration command must be explained. Explain is therefore a diagnostic operation that can issue
/// reads and should not be placed on latency-sensitive application paths.
/// </remarks>
internal sealed class MongoDbPhysicalQueryExplainer(
    IMongoDatabase database,
    ExecutableStorageRoute route,
    StorageUnitPhysicalStorage storage,
    Func<DocumentScopeSelection> scope,
    MongoDbTransactionCapability transactionCapability)
{
    public async Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken)
    {
        var resolvedScope = scope();
        var predicate = MongoDbPhysicalQueryHandler.BuildPredicate(query, plan, resolvedScope, storage, route);
        var lookup = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var renderedFilter = Render(lookup, predicate.Filter);
        var sort = MongoDbPhysicalQueryHandler.BuildSort(query, plan);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical query explain",
            cancellationToken);
        var commands = new List<PhysicalDocumentQueryCommandExplanation>();

        switch (query.ResultOperation)
        {
            case BoundedQueryResultOperation.Documents:
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedFilter),
                    predicate.FieldIdentifiers);
                if (query.Take != 0)
                {
                    await AddAsync(
                        PhysicalDocumentQueryCommandKind.Page,
                        PhysicalDocumentQueryCommandIdentities.Page,
                        FindCommand(lookup, query, renderedFilter, sort, query.Take, includeSort: true, includeSkip: true),
                        predicate.FieldIdentifiers);
                    if (plan.RequiresPrimaryLookup)
                    {
                        var found = await ExecuteBoundedFindAsync(
                            lookup, query, predicate.Filter, sort, query.Take, cancellationToken);
                        await AddPrimaryHydrationAsync(found);
                    }
                }
                break;
            case BoundedQueryResultOperation.Count:
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedFilter),
                    predicate.FieldIdentifiers);
                break;
            case BoundedQueryResultOperation.First:
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedFilter),
                    predicate.FieldIdentifiers);
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.First,
                    PhysicalDocumentQueryCommandIdentities.First,
                    FindCommand(lookup, query, renderedFilter, sort, 1, includeSort: true, includeSkip: true),
                    predicate.FieldIdentifiers);
                if (plan.RequiresPrimaryLookup)
                {
                    var found = await ExecuteBoundedFindAsync(lookup, query, predicate.Filter, sort, 1, cancellationToken);
                    await AddPrimaryHydrationAsync(found);
                }
                break;
            case BoundedQueryResultOperation.Any:
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Any,
                    PhysicalDocumentQueryCommandIdentities.Any,
                    FindCommand(lookup, query, renderedFilter, sort: null, limit: 1, includeSort: false, includeSkip: false),
                    predicate.FieldIdentifiers);
                break;
            default:
                throw new NotSupportedException(
                    $"MongoDB native explain does not support result operation '{query.ResultOperation}'.");
        }

        return new PhysicalDocumentQueryExplanation(
            plan,
            PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, resolvedScope),
            commands);

        async Task AddAsync(
            PhysicalDocumentQueryCommandKind kind,
            string identity,
            BsonDocument command,
            IReadOnlyList<string> fieldIdentifiers)
        {
            var nativePlan = await ExplainCommandAsync(command, cancellationToken);
            commands.Add(new PhysicalDocumentQueryCommandExplanation(
                kind,
                identity,
                "mongodb-json",
                nativePlan.ToJson(),
                fieldIdentifiers));
        }

        async Task AddPrimaryHydrationAsync(IReadOnlyList<BsonDocument> linked)
        {
            if (linked.Count == 0)
            {
                throw new InvalidOperationException(
                    "MongoDB cannot produce exact primary-hydration explain evidence because the bounded lookup returned no identities.");
            }

            var primary = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
            var hydrationFilter = Builders<BsonDocument>.Filter.Or(linked.Select(document =>
                MongoDbPhysicalDocumentIdentity.PrimaryExactFilter(route, document)));
            await AddAsync(
                PhysicalDocumentQueryCommandKind.PrimaryHydration,
                PhysicalDocumentQueryCommandIdentities.PrimaryHydration,
                new BsonDocument
                {
                    ["find"] = route.PrimaryStorage.Name.Identifier,
                    ["filter"] = Render(primary, hydrationFilter)
                },
                [MongoDbPhysicalStorageFields.Id, route.Envelope.Identity.ComparisonKey.Identifier]);
        }
    }

    private async Task<BsonDocument> ExplainCommandAsync(
        BsonDocument explainedCommand,
        CancellationToken cancellationToken) =>
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = explainedCommand,
                ["verbosity"] = "queryPlanner"
            },
            cancellationToken: cancellationToken);

    private static BsonDocument CountCommand(string collection, BsonDocument renderedFilter) =>
        new()
        {
            ["aggregate"] = collection,
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
        };

    private static BsonDocument FindCommand(
        IMongoCollection<BsonDocument> collection,
        DocumentQuery query,
        BsonDocument renderedFilter,
        SortDefinition<BsonDocument>? sort,
        int? limit,
        bool includeSort,
        bool includeSkip)
    {
        var command = new BsonDocument
        {
            ["find"] = collection.CollectionNamespace.CollectionName,
            ["filter"] = renderedFilter
        };
        if (includeSort && sort is not null)
            command["sort"] = sort.Render(new RenderArgs<BsonDocument>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));
        if (includeSkip && query.Skip is { } skip)
            command["skip"] = skip;
        if (limit is { } take)
            command["limit"] = take;
        return command;
    }

    private static async Task<IReadOnlyList<BsonDocument>> ExecuteBoundedFindAsync(
        IMongoCollection<BsonDocument> collection,
        DocumentQuery query,
        FilterDefinition<BsonDocument> filter,
        SortDefinition<BsonDocument> sort,
        int? limit,
        CancellationToken cancellationToken)
    {
        var find = collection.Find(filter).Sort(sort).Skip(query.Skip ?? 0);
        if (limit is { } take)
            find = find.Limit(take);
        return await find.ToListAsync(cancellationToken);
    }

    private static BsonDocument Render(
        IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));
}
