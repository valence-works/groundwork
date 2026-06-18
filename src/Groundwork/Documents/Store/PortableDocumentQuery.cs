namespace Groundwork.Documents.Store;

/// <summary>
/// The closed set of comparison operators a portable document query may use against a single declared index.
/// </summary>
public enum QueryComparisonOperator
{
    /// <summary>Exact equality. A <see langword="null"/> value matches documents whose field is null/absent.</summary>
    Equal,

    /// <summary>Set membership (SQL <c>IN</c>). An empty value set matches nothing.</summary>
    In,

    /// <summary>Case-insensitive substring match (SQL <c>LIKE '%v%'</c>). A null/absent field yields no match.</summary>
    Contains
}

/// <summary>
/// A single-field comparison addressed by declared index identity. Values are the normalized string
/// representations stored in the index, matching the existing equality model.
/// </summary>
public sealed record QueryComparison
{
    public QueryComparison(string indexName, QueryComparisonOperator @operator, IReadOnlyList<string?> values)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name must be provided.", nameof(indexName));

        IndexName = indexName;
        Operator = @operator;
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public string IndexName { get; }
    public QueryComparisonOperator Operator { get; }
    public IReadOnlyList<string?> Values { get; }

    /// <summary>Exact equality; pass <see langword="null"/> to match documents whose field is null/absent.</summary>
    public static QueryComparison Equal(string indexName, string? value) =>
        new(indexName, QueryComparisonOperator.Equal, new[] { value });

    /// <summary>Set membership over the field's own (string) representation. An empty set matches nothing.</summary>
    public static QueryComparison In(string indexName, IEnumerable<string?> values) =>
        new(indexName, QueryComparisonOperator.In, (values ?? throw new ArgumentNullException(nameof(values))).ToList());

    /// <summary>Case-insensitive, null-field-safe substring match.</summary>
    public static QueryComparison Contains(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.Contains, new[] { value ?? throw new ArgumentNullException(nameof(value)) });
}

/// <summary>
/// A disjunction (<c>OR</c>) of single-field comparisons. A clause with no comparisons is the
/// constant-false "no match" sentinel and causes the whole query to match nothing.
/// </summary>
public sealed record QueryClause
{
    public QueryClause(IReadOnlyList<QueryComparison> comparisons) =>
        Comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public IReadOnlyList<QueryComparison> Comparisons { get; }

    /// <summary>The constant-false sentinel clause (an empty disjunction) that matches nothing.</summary>
    public static QueryClause MatchNone { get; } = new(Array.Empty<QueryComparison>());

    public static QueryClause AnyOf(params QueryComparison[] comparisons) => new(comparisons);

    public static QueryClause Of(QueryComparison comparison) => new(new[] { comparison });
}

/// <summary>Single-field ordering over a declared sortable index.</summary>
public sealed record QueryOrder(string IndexName, bool Descending = false);

/// <summary>Whether a query is filtered by the ambient tenant or bypasses tenant filtering.</summary>
public enum QueryTenantScope
{
    /// <summary>Apply ambient tenant filtering (the default).</summary>
    TenantAware,

    /// <summary>Bypass ambient tenant filtering for this query.</summary>
    TenantAgnostic
}

/// <summary>
/// The closed, provider-neutral document query: an <c>AND</c> of <c>OR</c>-groups of single-field
/// comparisons, with at most one ordering, optional offset paging, and a tenant-scope flag. Zero
/// clauses match all documents of the kind.
/// </summary>
public sealed record PortableDocumentQuery
{
    public PortableDocumentQuery(
        string documentKind,
        IReadOnlyList<QueryClause>? clauses = null,
        QueryOrder? order = null,
        int? skip = null,
        int? take = null,
        QueryTenantScope tenantScope = QueryTenantScope.TenantAware)
    {
        if (string.IsNullOrWhiteSpace(documentKind))
            throw new ArgumentException("Document kind must be provided.", nameof(documentKind));

        if (skip is < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must be greater than or equal to 0.");

        if (take is < 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be greater than or equal to 0.");

        DocumentKind = documentKind;
        Clauses = clauses ?? Array.Empty<QueryClause>();
        Order = order;
        Skip = skip;
        Take = take;
        TenantScope = tenantScope;
    }

    public string DocumentKind { get; }
    public IReadOnlyList<QueryClause> Clauses { get; init; }
    public QueryOrder? Order { get; init; }
    public int? Skip { get; init; }
    public int? Take { get; init; }
    public QueryTenantScope TenantScope { get; init; }

    public PortableDocumentQuery Where(QueryClause clause) =>
        this with { Clauses = new List<QueryClause>(Clauses) { clause } };

    public PortableDocumentQuery OrderBy(QueryOrder order) => this with { Order = order };

    public PortableDocumentQuery Page(int? skip, int? take) => this with { Skip = skip, Take = take };

    public PortableDocumentQuery WithTenantScope(QueryTenantScope scope) => this with { TenantScope = scope };
}

/// <summary>The result of a paged portable query: the page window plus the total predicate count.</summary>
public sealed record DocumentQueryResult(IReadOnlyList<DocumentEnvelope> Documents, long TotalCount)
{
    public static DocumentQueryResult Empty { get; } = new(Array.Empty<DocumentEnvelope>(), 0);
}
