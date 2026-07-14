using System.Text.RegularExpressions;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

internal static class MongoDbPhysicalIdentityQuery
{
    public static FilterDefinition<BsonDocument> Build(
        DocumentQueryComparison comparison,
        PhysicalQueryPlan plan)
    {
        var bound = PhysicalDocumentIdentityQuery.Bind(plan, comparison);
        if (comparison.Operator == QueryComparisonOperator.In)
        {
            return bound.Values.Count == 0
                ? Builders<BsonDocument>.Filter.Eq("_groundwork_match_none", true)
                : Builders<BsonDocument>.Filter.Or(bound.Values.Select(value =>
                    Exact(plan.DocumentIdentity, RequireExact(value), QueryComparisonOperator.Equal)));
        }

        var evidence = bound.Values.Single();
        return evidence switch
        {
            PhysicalQueryIdentityValue.Exact exact => Exact(
                plan.DocumentIdentity,
                exact,
                comparison.Operator),
            PhysicalQueryIdentityValue.Ordered ordered => Ordered(
                plan.DocumentIdentity.Comparison.Identifier,
                ordered.ComparisonKey,
                comparison.Operator),
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
        };
    }

    private static FilterDefinition<BsonDocument> Exact(
        PhysicalQueryDocumentIdentityBinding identity,
        PhysicalQueryIdentityValue.Exact evidence,
        QueryComparisonOperator operation)
    {
        var builder = Builders<BsonDocument>.Filter;
        return operation switch
        {
            QueryComparisonOperator.Equal => builder.And(
                builder.Eq(identity.Lookup.Identifier, evidence.LookupKey),
                builder.Eq(identity.Comparison.Identifier, evidence.ComparisonKey)),
            QueryComparisonOperator.NotEqual => builder.Or(
                builder.Ne(identity.Lookup.Identifier, evidence.LookupKey),
                builder.Ne(identity.Comparison.Identifier, evidence.ComparisonKey)),
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
            QueryComparisonOperator.StartsWith => builder.Regex(
                field,
                new BsonRegularExpression("^" + Regex.Escape(comparisonKey))),
            QueryComparisonOperator.GreaterThan => builder.Gt(field, comparisonKey),
            QueryComparisonOperator.GreaterThanOrEqual => builder.Gte(field, comparisonKey),
            QueryComparisonOperator.LessThan => builder.Lt(field, comparisonKey),
            QueryComparisonOperator.LessThanOrEqual => builder.Lte(field, comparisonKey),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
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
