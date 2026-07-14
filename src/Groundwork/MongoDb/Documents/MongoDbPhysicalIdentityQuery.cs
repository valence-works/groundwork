using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

internal static class MongoDbPhysicalIdentityQuery
{
    public static FilterDefinition<BsonDocument> Build(
        DocumentQueryComparison comparison,
        PhysicalQueryPlan plan,
        string? lookupIdentifier = null,
        string? comparisonIdentifier = null)
    {
        lookupIdentifier ??= plan.DocumentIdentity.Lookup.Identifier;
        comparisonIdentifier ??= plan.DocumentIdentity.Comparison.Identifier;
        var bound = PhysicalDocumentIdentityQuery.Bind(plan, comparison);
        if (comparison.Operator == QueryComparisonOperator.In)
        {
            return bound.Values.Count == 0
                ? Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true)
                : Builders<BsonDocument>.Filter.Or(bound.Values.Select(value =>
                    Exact(lookupIdentifier, comparisonIdentifier, RequireExact(value), QueryComparisonOperator.Equal)));
        }

        var evidence = bound.Values.Single();
        return evidence switch
        {
            PhysicalQueryIdentityValue.Exact exact => Exact(
                lookupIdentifier,
                comparisonIdentifier,
                exact,
                comparison.Operator),
            PhysicalQueryIdentityValue.Ordered ordered => Ordered(
                comparisonIdentifier,
                ordered.ComparisonKey,
                comparison.Operator),
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
        };
    }

    private static FilterDefinition<BsonDocument> Exact(
        string lookupIdentifier,
        string comparisonIdentifier,
        PhysicalQueryIdentityValue.Exact evidence,
        QueryComparisonOperator operation)
    {
        var builder = Builders<BsonDocument>.Filter;
        return operation switch
        {
            QueryComparisonOperator.Equal => builder.And(
                builder.Eq(lookupIdentifier, evidence.LookupKey),
                builder.Eq(comparisonIdentifier, evidence.ComparisonKey)),
            QueryComparisonOperator.NotEqual => builder.Or(
                builder.Ne(lookupIdentifier, evidence.LookupKey),
                builder.Ne(comparisonIdentifier, evidence.ComparisonKey)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static FilterDefinition<BsonDocument> Ordered(
        string field,
        string comparisonKey,
        QueryComparisonOperator operation)
    {
        var builder = Builders<BsonDocument>.Filter;
        return operation switch
        {
            QueryComparisonOperator.StartsWith => builder.And(
                builder.Gte(field, comparisonKey),
                builder.Lt(field, PrefixUpperBound(comparisonKey))),
            QueryComparisonOperator.GreaterThan => builder.Gt(field, comparisonKey),
            QueryComparisonOperator.GreaterThanOrEqual => builder.Gte(field, comparisonKey),
            QueryComparisonOperator.LessThan => builder.Lt(field, comparisonKey),
            QueryComparisonOperator.LessThanOrEqual => builder.Lte(field, comparisonKey),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static string PrefixUpperBound(string prefix)
    {
        if (prefix.Length == 0)
            throw new InvalidOperationException("Document identity prefix evidence cannot be empty.");
        var upper = prefix.ToCharArray();
        upper[^1]++;
        return new string(upper);
    }

    private static PhysicalQueryIdentityValue.Exact RequireExact(PhysicalQueryIdentityValue value) =>
        value switch
        {
            PhysicalQueryIdentityValue.Exact exact => exact,
            PhysicalQueryIdentityValue.Ordered => throw new InvalidOperationException(
                "Identity membership requires exact lookup and comparison evidence."),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
}
