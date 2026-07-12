using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>Canonical MongoDB membership metadata for one provider-neutral physical index.</summary>
internal static class MongoDbPhysicalIndexSemantics
{
    public static IReadOnlyList<string> ValueFields(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index)
    {
        var projected = route.ProjectedColumns
            .Where(projection => projection.Target == index.Target)
            .Select(projection => projection.Column.Identifier)
            .ToHashSet(StringComparer.Ordinal);
        return index.Columns
            .OrderBy(column => column.Order)
            .Select(column => column.Column.Identifier)
            .Where(projected.Contains)
            .ToArray();
    }

    public static BsonDocument? PartialFilter(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index)
    {
        if (index.MissingValueBehavior != MissingValueBehavior.Excluded)
            return null;
        var fields = ValueFields(route, index);
        if (fields.Count == 0)
            return null;
        var filter = new BsonDocument();
        foreach (var field in fields)
            filter[field] = new BsonDocument("$exists", true);
        return filter;
    }

    public static bool PartialFilterMatches(BsonDocument actualIndex, BsonDocument? expected)
    {
        if (expected is null)
            return !actualIndex.Contains("partialFilterExpression");
        return actualIndex.TryGetValue("partialFilterExpression", out var actual) &&
               actual.IsBsonDocument &&
               actual.AsBsonDocument.Equals(expected);
    }
}
