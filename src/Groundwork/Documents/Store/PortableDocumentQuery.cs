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

        ArgumentNullException.ThrowIfNull(values);

        switch (@operator)
        {
            case QueryComparisonOperator.Equal when values.Count != 1:
                throw new ArgumentException("Equal requires exactly one value.", nameof(values));
            case QueryComparisonOperator.Contains when values.Count != 1:
                throw new ArgumentException("Contains requires exactly one value.", nameof(values));
            case QueryComparisonOperator.Contains when values[0] is null:
                throw new ArgumentException("Contains does not accept a null value.", nameof(values));
        }

        IndexName = indexName;
        Operator = @operator;
        Values = values;
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

/// <summary>
/// The closed, provider-neutral document query: an <c>AND</c> of <c>OR</c>-groups of single-field
/// comparisons, with at most one ordering and optional offset paging. Scope is always inherited
/// from the document-store session. Zero clauses match all documents of the kind.
/// </summary>
public sealed record PortableDocumentQuery
{
    public PortableDocumentQuery(
        string documentKind,
        IReadOnlyList<QueryClause>? clauses = null,
        QueryOrder? order = null,
        int? skip = null,
        int? take = null)
    {
        if (string.IsNullOrWhiteSpace(documentKind))
            throw new ArgumentException("Document kind must be provided.", nameof(documentKind));

        DocumentKind = documentKind;
        Clauses = clauses ?? Array.Empty<QueryClause>();
        Order = order;
        Skip = skip;
        Take = take;
    }

    public string DocumentKind { get; }
    public IReadOnlyList<QueryClause> Clauses { get; init; }
    public QueryOrder? Order { get; init; }

    private readonly int? skip;
    public int? Skip
    {
        get => skip;
        init
        {
            if (value is < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Skip must be greater than or equal to 0.");
            skip = value;
        }
    }

    private readonly int? take;
    public int? Take
    {
        get => take;
        init
        {
            if (value is < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Take must be greater than or equal to 0.");
            take = value;
        }
    }

    public PortableDocumentQuery Where(QueryClause clause) =>
        this with { Clauses = new List<QueryClause>(Clauses) { clause } };

    public PortableDocumentQuery OrderBy(QueryOrder order) => this with { Order = order };

    public PortableDocumentQuery Page(int? skip, int? take) => this with { Skip = skip, Take = take };

}

/// <summary>The result of a paged portable query: the page window plus the total predicate count.</summary>
public sealed record DocumentQueryResult(IReadOnlyList<DocumentEnvelope> Documents, long TotalCount)
{
    public static DocumentQueryResult Empty { get; } = new(Array.Empty<DocumentEnvelope>(), 0);
}
