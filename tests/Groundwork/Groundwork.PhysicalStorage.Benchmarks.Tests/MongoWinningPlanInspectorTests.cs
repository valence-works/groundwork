using MongoDB.Bson;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class MongoWinningPlanInspectorTests
{
    [Fact]
    public void Only_the_winning_plan_is_checked_for_the_expected_index_scan()
    {
        var explain = BsonDocument.Parse("""
            {
              "queryPlanner": {
                "winningPlan": {
                  "stage": "FETCH",
                  "inputStage": { "stage": "IXSCAN", "indexName": "expected_index" }
                },
                "rejectedPlans": [
                  { "stage": "COLLSCAN" },
                  { "stage": "IXSCAN", "indexName": "wrong_index" }
                ]
              },
              "executionStats": { "executionStages": { "stage": "COLLSCAN" } }
            }
            """);

        MongoWinningPlanInspector.EnsureIndexScan(explain, "expected_index");
    }

    [Fact]
    public void Aggregate_explain_uses_the_cursor_query_planner_winning_plan()
    {
        var explain = BsonDocument.Parse("""
            {
              "stages": [
                {
                  "$cursor": {
                    "queryPlanner": {
                      "winningPlan": { "stage": "IXSCAN", "indexName": "expected_index" },
                      "rejectedPlans": [ { "stage": "COLLSCAN" } ]
                    }
                  }
                },
                { "$group": { "_id": 1 } }
              ]
            }
            """);

        MongoWinningPlanInspector.EnsureIndexScan(explain, "expected_index");
    }

    [Theory]
    [InlineData("{ 'queryPlanner': { 'winningPlan': { 'stage': 'COLLSCAN' } } }")]
    [InlineData("{ 'queryPlanner': { 'winningPlan': { 'stage': 'IXSCAN', 'indexName': 'wrong_index' } } }")]
    [InlineData("{ 'queryPlanner': { 'winningPlan': { 'stage': 'FETCH' }, 'rejectedPlans': [ { 'stage': 'IXSCAN', 'indexName': 'expected_index' } ] } }")]
    public void Winning_plan_rejects_collection_scans_wrong_indexes_and_rejected_only_matches(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoWinningPlanInspector.EnsureIndexScan(BsonDocument.Parse(json.Replace('\'', '"')), "expected_index"));

        Assert.NotEmpty(exception.Message);
    }
}
