using MongoDB.Bson;

namespace Groundwork.MongoDb.Documents;

internal sealed record MongoDbWinningIndexBound(
    string Field,
    IReadOnlyList<string> Intervals,
    bool HasExactIntervalEvidence)
{
    public bool IsConstrained => HasExactIntervalEvidence && Intervals.Count > 0 &&
                                 Intervals.All(interval => !IsUnbounded(interval));

    private static bool IsUnbounded(string interval) => string.Concat(interval.Where(character =>
            !char.IsWhiteSpace(character)))
        .Equals("[MinKey,MaxKey]", StringComparison.Ordinal);
}

internal sealed record MongoDbWinningIndexScan(
    string IndexName,
    IReadOnlyList<MongoDbWinningIndexBound> Bounds);

internal sealed record MongoDbWinningPlanObservation(
    bool HasCollectionScan,
    IReadOnlyList<MongoDbWinningIndexScan> IndexScans);

internal static class MongoDbWinningPlanInspector
{
    public static BsonDocument ExactWinningPlan(BsonDocument explanation)
    {
        var planners = Descendants(explanation)
            .Where(document => document.TryGetValue("queryPlanner", out var value) && value.IsBsonDocument)
            .Select(document => document["queryPlanner"].AsBsonDocument)
            .ToArray();
        if (planners.Length != 1 ||
            !planners[0].TryGetValue("winningPlan", out var winningPlan) ||
            !winningPlan.IsBsonDocument)
        {
            throw new InvalidOperationException(
                "MongoDB explain must contain exactly one queryPlanner.winningPlan.");
        }
        return winningPlan.AsBsonDocument;
    }

    public static MongoDbWinningPlanObservation Inspect(BsonDocument winningPlan)
    {
        var stages = Descendants(winningPlan)
            .Where(document => document.TryGetValue("stage", out var stage) && stage.IsString)
            .ToArray();
        var indexScans = stages
            .Where(document => document["stage"].AsString.Equals("IXSCAN", StringComparison.OrdinalIgnoreCase))
            .Where(document => document.TryGetValue("indexName", out var indexName) && indexName.IsString)
            .Select(document => new MongoDbWinningIndexScan(
                document["indexName"].AsString,
                document.TryGetValue("indexBounds", out var bounds) && bounds.IsBsonDocument
                    ? bounds.AsBsonDocument.Elements.Select(ReadBound).ToArray()
                    : []))
            .ToArray();
        return new MongoDbWinningPlanObservation(
            stages.Any(document =>
                document["stage"].AsString.Equals("COLLSCAN", StringComparison.OrdinalIgnoreCase)),
            indexScans);
    }

    private static MongoDbWinningIndexBound ReadBound(BsonElement element)
    {
        if (!element.Value.IsBsonArray)
            return new MongoDbWinningIndexBound(element.Name, [], false);
        var values = element.Value.AsBsonArray;
        var intervals = values
            .Where(value => value.IsString)
            .Select(value => value.AsString)
            .ToArray();
        return new MongoDbWinningIndexBound(
            element.Name,
            intervals,
            intervals.Length == values.Count);
    }

    private static IEnumerable<BsonDocument> Descendants(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            var document = value.AsBsonDocument;
            yield return document;
            foreach (var child in document.Elements.SelectMany(element => Descendants(element.Value)))
                yield return child;
            yield break;
        }
        if (!value.IsBsonArray)
            yield break;
        foreach (var child in value.AsBsonArray.SelectMany(Descendants))
            yield return child;
    }
}
