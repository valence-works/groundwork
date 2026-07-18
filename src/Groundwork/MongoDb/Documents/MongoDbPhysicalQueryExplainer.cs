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
        DocumentQueryContinuationCodec.ValidateScope(plan, resolvedScope);
        var basePredicate = MongoDbPhysicalQueryHandler.BuildPredicate(
            query, plan, resolvedScope, storage, route);
        var pagePredicate = MongoDbPhysicalQueryHandler.BuildPagePredicate(
            query, plan, resolvedScope, basePredicate);
        var lookup = database.GetCollection<BsonDocument>(plan.LookupObject.Identifier);
        var renderedBaseFilter = Render(lookup, basePredicate.Filter);
        var renderedPageFilter = Render(lookup, pagePredicate.Filter);
        var sort = MongoDbPhysicalQueryHandler.BuildSort(query, plan);
        await transactionCapability.EnsureSupportedAsync(
            [route.StorageUnit.Value],
            "physical query explain",
            cancellationToken);
        var commands = new List<PhysicalDocumentQueryCommandExplanation>();

        switch (query.ResultOperation)
        {
            case BoundedQueryResultOperation.Documents:
                if (query.LatestPerKeyPath is not null)
                {
                    await AddLatestCountAsync();
                    if (query.Take != 0)
                    {
                        await AddLatestPageAsync(
                            query,
                            PhysicalDocumentQueryCommandKind.Page,
                            PhysicalDocumentQueryCommandIdentities.Page);
                    }
                    break;
                }

                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedBaseFilter),
                    basePredicate.FieldIdentifiers);
                if (query.Take != 0)
                {
                    var pageLimit = MongoDbPhysicalQueryHandler.PageReadLimit(query, plan);
                    await AddAsync(
                        PhysicalDocumentQueryCommandKind.Page,
                        PhysicalDocumentQueryCommandIdentities.Page,
                        FindCommand(lookup, query, renderedPageFilter, sort, pageLimit, includeSort: true, includeSkip: true),
                        pagePredicate.FieldIdentifiers);
                    if (plan.RequiresPrimaryLookup)
                    {
                        var found = await ExecuteBoundedFindAsync(
                            lookup, query, pagePredicate.Filter, sort, pageLimit, cancellationToken);
                        if (query.Take is { } take && found.Count > take)
                            found = found.Take(take).ToArray();
                        await AddPrimaryHydrationAsync(found);
                    }
                }
                break;
            case BoundedQueryResultOperation.Count:
                if (query.LatestPerKeyPath is not null)
                {
                    await AddLatestCountAsync();
                    break;
                }

                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedBaseFilter),
                    basePredicate.FieldIdentifiers);
                break;
            case BoundedQueryResultOperation.First:
                if (query.LatestPerKeyPath is not null)
                {
                    await AddLatestCountAsync();
                    await AddLatestPageAsync(
                        query.Page(query.Skip, 1),
                        PhysicalDocumentQueryCommandKind.First,
                        PhysicalDocumentQueryCommandIdentities.First);
                    break;
                }

                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    CountCommand(plan.LookupObject.Identifier, renderedBaseFilter),
                    basePredicate.FieldIdentifiers);
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.First,
                    PhysicalDocumentQueryCommandIdentities.First,
                    FindCommand(lookup, query, renderedPageFilter, sort, 1, includeSort: true, includeSkip: true),
                    pagePredicate.FieldIdentifiers);
                if (plan.RequiresPrimaryLookup)
                {
                    var found = await ExecuteBoundedFindAsync(
                        lookup, query, pagePredicate.Filter, sort, 1, cancellationToken);
                    await AddPrimaryHydrationAsync(found);
                }
                break;
            case BoundedQueryResultOperation.Any:
                await AddAsync(
                    PhysicalDocumentQueryCommandKind.Any,
                    PhysicalDocumentQueryCommandIdentities.Any,
                    FindCommand(lookup, query, renderedBaseFilter, sort: null, limit: 1, includeSort: false, includeSkip: false),
                    basePredicate.FieldIdentifiers);
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
            var shape = DescribeCommand(command, plan);
            commands.Add(new PhysicalDocumentQueryCommandExplanation(
                kind,
                identity,
                "mongodb-json",
                nativePlan.ToJson(),
                fieldIdentifiers,
                shape.MaximumRows,
                shape.Order));
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
                    ["filter"] = Render(primary, hydrationFilter),
                    ["limit"] = linked.Count
                },
                [MongoDbPhysicalStorageFields.Id, route.Envelope.Identity.ComparisonKey.Identifier]);
        }

        async Task AddLatestCountAsync()
        {
            var pipeline = MongoDbPhysicalQueryHandler.LatestPerKeyCountPipeline(
                renderedBaseFilter,
                query,
                plan);
            await AddAsync(
                PhysicalDocumentQueryCommandKind.Count,
                PhysicalDocumentQueryCommandIdentities.Count,
                AggregateCommand(plan.LookupObject.Identifier, pipeline),
                LatestPerKeyFieldIdentifiers(basePredicate, query, plan));
        }

        async Task AddLatestPageAsync(
            DocumentQuery executionQuery,
            PhysicalDocumentQueryCommandKind kind,
            string identity)
        {
            var pipeline = MongoDbPhysicalQueryHandler.LatestPerKeyPagePipeline(
                renderedBaseFilter,
                executionQuery,
                plan);
            await AddAsync(
                kind,
                identity,
                AggregateCommand(plan.LookupObject.Identifier, pipeline),
                LatestPerKeyFieldIdentifiers(basePredicate, executionQuery, plan));
            if (!plan.RequiresPrimaryLookup)
                return;

            var linked = await ExecuteAggregateAsync(lookup, pipeline, cancellationToken);
            await AddPrimaryHydrationAsync(linked);
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

    private static BsonDocument AggregateCommand(
        string collection,
        IReadOnlyList<BsonDocument> pipeline) =>
        new()
        {
            ["aggregate"] = collection,
            ["pipeline"] = new BsonArray(pipeline),
            ["cursor"] = new BsonDocument()
        };

    private static BsonDocument FindCommand(
        IMongoCollection<BsonDocument> collection,
        DocumentQuery query,
        BsonDocument renderedFilter,
        SortDefinition<BsonDocument>? sort,
        int limit,
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
        command["limit"] = limit;
        return command;
    }

    private static async Task<IReadOnlyList<BsonDocument>> ExecuteBoundedFindAsync(
        IMongoCollection<BsonDocument> collection,
        DocumentQuery query,
        FilterDefinition<BsonDocument> filter,
        SortDefinition<BsonDocument> sort,
        int limit,
        CancellationToken cancellationToken)
    {
        var find = collection.Find(filter).Sort(sort).Skip(query.Skip ?? 0);
        find = find.Limit(limit);
        return await find.ToListAsync(cancellationToken);
    }

    private static MongoDbPhysicalCommandShape DescribeCommand(
        BsonDocument command,
        PhysicalQueryPlan plan)
    {
        if (command.Contains("find"))
        {
            var findMaximumRows = command.TryGetValue("limit", out var limit)
                ? (int?)limit.ToInt32()
                : null;
            var order = command.TryGetValue("sort", out var sort)
                ? DescribeOrder(sort.AsBsonDocument, plan)
                : [];
            return new MongoDbPhysicalCommandShape(findMaximumRows, order);
        }

        var pipeline = command["pipeline"].AsBsonArray
            .Select(stage => stage.AsBsonDocument)
            .ToArray();
        var appliedLimit = pipeline
            .Where(stage => stage.Contains("$limit"))
            .Select(stage => (int?)stage["$limit"].ToInt32())
            .LastOrDefault();
        var maximumRows = appliedLimit ??
                          (pipeline.Any(stage => stage.Contains("$count")) ||
                           pipeline.Any(stage =>
                               stage.TryGetValue("$group", out var group) &&
                               group.AsBsonDocument.TryGetValue("_id", out var identity) &&
                               identity.IsInt32 &&
                               identity.AsInt32 == 1)
                              ? 1
                              : null);
        var renderedOrder = pipeline
            .Where(stage => stage.Contains("$sort"))
            .Select(stage => stage["$sort"].AsBsonDocument)
            .LastOrDefault();
        return new MongoDbPhysicalCommandShape(
            maximumRows,
            renderedOrder is null ? [] : DescribeOrder(renderedOrder, plan));
    }

    private static IReadOnlyList<PhysicalDocumentQueryCommandOrder> DescribeOrder(
        BsonDocument renderedOrder,
        PhysicalQueryPlan plan) =>
        renderedOrder.Elements
            .Select(element => new PhysicalDocumentQueryCommandOrder(
                element.Name,
                element.Value.ToInt32() == 1
                    ? PhysicalSortDirection.Ascending
                    : PhysicalSortDirection.Descending,
                plan.Order.Any(order =>
                    order.Field.Identifier == element.Name &&
                    order.IsIdentityTieBreak)))
            .ToArray();

    private static async Task<IReadOnlyList<BsonDocument>> ExecuteAggregateAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<BsonDocument> pipeline,
        CancellationToken cancellationToken) =>
        await collection.Aggregate<BsonDocument>(pipeline.ToArray()).ToListAsync(cancellationToken);

    private static IReadOnlyList<string> LatestPerKeyFieldIdentifiers(
        MongoDbPhysicalQueryPredicate predicate,
        DocumentQuery query,
        PhysicalQueryPlan plan) =>
        predicate.FieldIdentifiers
            .Concat(DocumentQueryOrderResolver.Resolve(query, plan).Select(order => order.Field.Identifier))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static BsonDocument Render(
        IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));

    private sealed record MongoDbPhysicalCommandShape(
        int? MaximumRows,
        IReadOnlyList<PhysicalDocumentQueryCommandOrder> Order);
}
