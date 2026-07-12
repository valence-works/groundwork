using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentStoreQueryTests
{
    [Fact]
    public void DocumentQueryIsTheImmutableRuntimeContractForOneBoundedDeclaration()
    {
        var clauses = new List<DocumentQueryClause>
        {
            DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))
        };
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            clauses,
            [new DocumentQueryOrder("stimulusType")],
            skip: 10,
            take: 20);

        clauses.Clear();

        Assert.Equal("workflowTriggerBinding", query.DocumentKind);
        Assert.Equal("list-by-stimulus-type", query.QueryIdentity);
        Assert.Single(query.Clauses);
        Assert.Equal(10, query.Skip);
        Assert.Equal(20, query.Take);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<DocumentQueryClause>>(query.Clauses).Clear());
    }

    [Fact]
    public void LegacyEqualityQueryBridgesToTheSingleDocumentQueryContract()
    {
#pragma warning disable GW0004
        var legacy = new DocumentStoreQuery(
            "workflowTriggerBinding",
            "by-stimulus-type",
            "http",
            skip: 5,
            take: 10);
#pragma warning restore GW0004

        var query = legacy.ToDocumentQuery("list-by-stimulus-type", "stimulusType");

        Assert.Equal("list-by-stimulus-type", query.QueryIdentity);
        var comparison = Assert.Single(Assert.Single(query.Clauses).Comparisons);
        Assert.Equal("stimulusType", comparison.Path);
        Assert.Equal(QueryComparisonOperator.Equal, comparison.Operator);
        Assert.Equal("http", Assert.Single(comparison.Values));
        Assert.Equal(5, query.Skip);
        Assert.Equal(10, query.Take);
    }

    [Fact]
    public void DocumentQueryExpressesTheFullPlannedRuntimeShape()
    {
        var query = new DocumentQuery(
                "workflowTriggerBinding",
                "latest-by-stimulus-type",
                [
                    DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http")),
                    DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http", "timer"])),
                    DocumentQueryClause.Of(DocumentQueryComparison.Contains("stimulusType", "ttp")),
                    DocumentQueryClause.Of(DocumentQueryComparison.NotEqual("stimulusType", "signal")),
                    DocumentQueryClause.Of(DocumentQueryComparison.GreaterThanOrEqual("stimulusType", "http")),
                    DocumentQueryClause.Of(DocumentQueryComparison.StartsWith("stimulusType", "route")),
                    DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("stimulusType", "a")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThan("stimulusType", "z")),
                    DocumentQueryClause.Of(DocumentQueryComparison.LessThanOrEqual("stimulusType", "zz"))
                ],
                [new DocumentQueryOrder("stimulusType")],
                take: 25)
            .ThenBy(new DocumentQueryOrder("createdAt", Groundwork.Core.PhysicalStorage.PhysicalSortDirection.Descending))
            .ContinueAfter("opaque-keyset")
            .LatestPerKey("stimulusType")
            .Select(Groundwork.Core.PhysicalStorage.BoundedQueryResultOperation.First);

        Assert.Equal(2, query.Order.Count);
        Assert.Equal("opaque-keyset", query.Continuation);
        Assert.Equal("stimulusType", query.LatestPerKeyPath);
        Assert.Equal(Groundwork.Core.PhysicalStorage.BoundedQueryResultOperation.First, query.ResultOperation);
        Assert.Equal(
            Enum.GetValues<QueryComparisonOperator>().Order(),
            query.Clauses.SelectMany(clause => clause.Comparisons).Select(comparison => comparison.Operator).Order());
    }

    [Fact]
    public void SupersededQueryTypesCarryActionableDeprecationGuidance()
    {
        var portable = Assert.Single(typeof(PortableDocumentQuery).GetCustomAttributes(
            typeof(ObsoleteAttribute), inherit: false).Cast<ObsoleteAttribute>());
        var equality = Assert.Single(typeof(DocumentStoreQuery).GetCustomAttributes(
            typeof(ObsoleteAttribute), inherit: false).Cast<ObsoleteAttribute>());

        Assert.Contains(nameof(DocumentQuery), portable.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DocumentQuery), equality.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, null, "skip")]
    [InlineData(null, -1, "take")]
    public void NegativePagingValuesFailClearly(int? skip, int? take, string parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
#pragma warning disable GW0004
            new DocumentStoreQuery("configurationDocument", "by-key", "alpha", skip, take));
#pragma warning restore GW0004

        Assert.Equal(parameterName, exception.ParamName);
    }
}
