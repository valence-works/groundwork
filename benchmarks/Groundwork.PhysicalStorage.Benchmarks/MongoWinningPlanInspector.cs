using MongoDB.Bson;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class MongoWinningPlanInspector
{
    public static void EnsureIndexScan(BsonDocument explain, string expectedIndexName)
    {
        ArgumentNullException.ThrowIfNull(explain);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIndexName);
        var winningPlans = Descendants(explain)
            .Where(document => document.TryGetValue("queryPlanner", out var value) && value.IsBsonDocument)
            .Select(document => document["queryPlanner"].AsBsonDocument)
            .Where(queryPlanner => queryPlanner.TryGetValue("winningPlan", out _))
            .Select(queryPlanner => queryPlanner["winningPlan"])
            .ToArray();
        if (winningPlans.Length != 1)
        {
            throw new InvalidOperationException(
                $"MongoDB explain evidence must contain exactly one queryPlanner.winningPlan document; found {winningPlans.Length}.");
        }

        var stages = Descendants(winningPlans[0])
            .Where(document => document.TryGetValue("stage", out var stage) && stage.IsString)
            .ToArray();
        if (stages.Any(document => document["stage"].AsString.Equals("COLLSCAN", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("MongoDB winning plan contains a COLLSCAN stage.");
        if (!stages.Any(document =>
                document["stage"].AsString.Equals("IXSCAN", StringComparison.OrdinalIgnoreCase) &&
                document.TryGetValue("indexName", out var indexName) &&
                indexName.IsString &&
                indexName.AsString.Equals(expectedIndexName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"MongoDB winning plan does not contain IXSCAN for expected index '{expectedIndexName}'.");
        }
    }

    private static IEnumerable<BsonDocument> Descendants(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            var document = value.AsBsonDocument;
            yield return document;
            foreach (var element in document.Elements)
            foreach (var descendant in Descendants(element.Value))
                yield return descendant;
        }
        else if (value.IsBsonArray)
        {
            foreach (var item in value.AsBsonArray)
            foreach (var descendant in Descendants(item))
                yield return descendant;
        }
    }
}
