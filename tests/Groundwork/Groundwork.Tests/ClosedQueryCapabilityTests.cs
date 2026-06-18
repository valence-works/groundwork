using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.Tests;

public sealed class ClosedQueryCapabilityTests
{
    private static StorageUnit Unit(
        bool nameSortable = true,
        bool supportsDisjunction = true,
        bool supportsTotalCount = true,
        QueryPagingSupport paging = QueryPagingSupport.Offset)
        => new(
            new StorageUnitIdentity("widget"),
            "Widget",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.None,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [
                new IndexDeclaration(
                    "by-name",
                    [new IndexField("name")],
                    IndexValueKind.String,
                    false,
                    nameSortable,
                    MissingValueBehavior.Excluded,
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal,
                        PortableQueryOperation.In,
                        PortableQueryOperation.Contains
                    }),
                new IndexDeclaration(
                    "by-color",
                    [new IndexField("color")],
                    IndexValueKind.Keyword,
                    false,
                    false,
                    MissingValueBehavior.Excluded,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
            ],
            [
                new PortableQueryDeclaration(
                    "search-by-name",
                    "by-name",
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal,
                        PortableQueryOperation.In,
                        PortableQueryOperation.Contains
                    },
                    QuerySortSupport.Both,
                    paging,
                    SupportsDisjunction: supportsDisjunction,
                    SupportsTotalCount: supportsTotalCount),
            ],
            PhysicalizationPolicy.Portable);

    [Fact]
    public void DescribeSurfacesOperatorsAndSortDirection()
    {
        var support = ClosedQueryCapabilityModel.Describe(Unit());

        Assert.True(support.SupportsOperator("by-name", PortableQueryOperation.In));
        Assert.True(support.SupportsOperator("by-name", PortableQueryOperation.Contains));
        Assert.False(support.SupportsOperator("by-color", PortableQueryOperation.In));
        Assert.True(support.SupportsOrderBy("by-name"));
        Assert.True(support.SupportsOrderBy("by-name", descending: true));
        Assert.False(support.SupportsOrderBy("by-color"));
    }

    [Fact]
    public void DescribeIgnoresUndeclaredIndexOperators()
    {
        // 'by-color' physically supports Equal but has no PortableQueryDeclaration, so it is not
        // part of the closed surface and must not appear as natively supported.
        var support = ClosedQueryCapabilityModel.Describe(Unit());

        Assert.False(support.Indexes.ContainsKey("by-color"));
        Assert.False(support.SupportsOperator("by-color", PortableQueryOperation.Equal));
    }

    [Fact]
    public void DescribeRespectsDeclaredSortDirection()
    {
        var unit = Unit() with
        {
            Queries =
            [
                new PortableQueryDeclaration(
                    "search-by-name",
                    "by-name",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Ascending,
                    QueryPagingSupport.Offset),
            ],
        };

        var support = ClosedQueryCapabilityModel.Describe(unit);

        Assert.True(support.SupportsOrderBy("by-name"));
        Assert.False(support.SupportsOrderBy("by-name", descending: true));
    }

    [Fact]
    public void EvaluateRejectsDescendingOrderWhenOnlyAscendingDeclared()
    {
        var unit = Unit() with
        {
            Queries =
            [
                new PortableQueryDeclaration(
                    "search-by-name",
                    "by-name",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Ascending,
                    QueryPagingSupport.Offset,
                    SupportsTotalCount: true),
            ],
        };
        var query = new PortableDocumentQuery("widget")
            .OrderBy(new QueryOrder("by-name", Descending: true));

        var result = ClosedQueryNativeSupport.Evaluate(unit, query);

        Assert.False(result.IsNativelySupported);
        Assert.Contains(result.Reasons, reason => reason.Contains("by-name") && reason.Contains("ORDER BY"));
    }

    [Fact]
    public void EvaluateTreatsOrCountAndPagingAsUniversal()
    {
        // OR composition, total count, and offset paging are universal in the closed-query contract:
        // they are native even when the declaration leaves the advisory flags unset.
        var unit = Unit(supportsDisjunction: false, supportsTotalCount: false, paging: QueryPagingSupport.None);
        var query = new PortableDocumentQuery("widget")
            .Where(QueryClause.AnyOf(
                QueryComparison.Equal("by-name", "a"),
                QueryComparison.Equal("by-name", "b")))
            .Page(0, 10);

        var result = ClosedQueryNativeSupport.Evaluate(unit, query);

        Assert.True(result.IsNativelySupported);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void EvaluateTreatsMatchAllPagedQueryAsUniversal()
    {
        var query = new PortableDocumentQuery("widget", take: 10);

        var result = ClosedQueryNativeSupport.Evaluate(Unit(paging: QueryPagingSupport.None), query);

        Assert.True(result.IsNativelySupported);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void EvaluateAcceptsFullyDeclaredClosedQuery()
    {
        var query = new PortableDocumentQuery("widget")
            .Where(QueryClause.AnyOf(
                QueryComparison.In("by-name", new[] { "a", "b" }),
                QueryComparison.Contains("by-name", "c")))
            .OrderBy(new QueryOrder("by-name"))
            .Page(0, 10);

        var result = ClosedQueryNativeSupport.Evaluate(Unit(), query);

        Assert.True(result.IsNativelySupported);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void EvaluateRejectsUndeclaredOperator()
    {
        var query = new PortableDocumentQuery("widget")
            .Where(QueryClause.Of(QueryComparison.In("by-color", new[] { "red" })));

        var result = ClosedQueryNativeSupport.Evaluate(Unit(), query);

        Assert.False(result.IsNativelySupported);
        Assert.Contains(result.Reasons, reason => reason.Contains("by-color") && reason.Contains("In"));
    }

    [Fact]
    public void EvaluateRejectsOrderByOnNonSortableIndex()
    {
        var query = new PortableDocumentQuery("widget")
            .OrderBy(new QueryOrder("by-name"));

        var result = ClosedQueryNativeSupport.Evaluate(Unit(nameSortable: false), query);

        Assert.False(result.IsNativelySupported);
        Assert.Contains(result.Reasons, reason => reason.Contains("not sortable"));
    }

    [Fact]
    public void EvaluateAcceptsOrCompositionWithoutDisjunctionFlag()
    {
        // OR is a universal query shape; an OR clause is native regardless of the advisory flag.
        var query = new PortableDocumentQuery("widget")
            .Where(QueryClause.AnyOf(
                QueryComparison.Equal("by-name", "a"),
                QueryComparison.Equal("by-name", "b")));

        var result = ClosedQueryNativeSupport.Evaluate(Unit(supportsDisjunction: false), query);

        Assert.True(result.IsNativelySupported);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ValidatorRejectsOrderingDeclarationOnNonSortableIndex()
    {
        var unit = Unit() with
        {
            Queries =
            [
                new PortableQueryDeclaration(
                    "bad-sort",
                    "by-color",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Ascending,
                    QueryPagingSupport.None),
            ],
        };
        var manifest = new StorageManifest(
            new StorageManifestIdentity("widget.documents"),
            new StorageManifestOwner("sample"),
            new StorageManifestVersion("1.0.0"),
            [unit],
            new HashSet<string>(),
            []);

        var result = new StorageManifestValidator().Validate(manifest);

        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-QUERY-004");
    }
}
