using Groundwork.Core.PhysicalStorage;

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
    Contains,
    NotEqual,
    StartsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
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
            case not QueryComparisonOperator.In when values.Count != 1:
                throw new ArgumentException($"{@operator} requires exactly one value.", nameof(values));
            case QueryComparisonOperator.Contains or QueryComparisonOperator.StartsWith or
                QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
                QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual when values[0] is null:
                throw new ArgumentException($"{@operator} does not accept a null value.", nameof(values));
        }

        IndexName = indexName;
        Operator = @operator;
        Values = Array.AsReadOnly(values.ToArray());
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

    public static QueryComparison NotEqual(string indexName, string? value) =>
        new(indexName, QueryComparisonOperator.NotEqual, [value]);

    public static QueryComparison StartsWith(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.StartsWith, [value ?? throw new ArgumentNullException(nameof(value))]);

    public static QueryComparison GreaterThan(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.GreaterThan, [value ?? throw new ArgumentNullException(nameof(value))]);

    public static QueryComparison GreaterThanOrEqual(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.GreaterThanOrEqual, [value ?? throw new ArgumentNullException(nameof(value))]);

    public static QueryComparison LessThan(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.LessThan, [value ?? throw new ArgumentNullException(nameof(value))]);

    public static QueryComparison LessThanOrEqual(string indexName, string value) =>
        new(indexName, QueryComparisonOperator.LessThanOrEqual, [value ?? throw new ArgumentNullException(nameof(value))]);
}

/// <summary>
/// A disjunction (<c>OR</c>) of single-field comparisons. A clause with no comparisons is the
/// constant-false "no match" sentinel and causes the whole query to match nothing.
/// </summary>
public sealed record QueryClause
{
    public QueryClause(IReadOnlyList<QueryComparison> comparisons) =>
        Comparisons = Array.AsReadOnly((comparisons ?? throw new ArgumentNullException(nameof(comparisons))).ToArray());

    public IReadOnlyList<QueryComparison> Comparisons { get; }

    /// <summary>The constant-false sentinel clause (an empty disjunction) that matches nothing.</summary>
    public static QueryClause MatchNone { get; } = new(Array.Empty<QueryComparison>());

    public static QueryClause AnyOf(params QueryComparison[] comparisons) => new(comparisons);

    public static QueryClause Of(QueryComparison comparison) => new(new[] { comparison });
}

/// <summary>Single-field ordering over a declared sortable index.</summary>
public sealed record QueryOrder(string IndexName, bool Descending = false);

/// <summary>One runtime comparison addressed by the stable path declared by a bounded query.</summary>
public sealed class DocumentQueryComparison
{
    public DocumentQueryComparison(string path, QueryComparisonOperator @operator, IReadOnlyList<string?> values)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A stable predicate path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(values);
        if (@operator != QueryComparisonOperator.In && values.Count != 1)
            throw new ArgumentException($"{@operator} requires exactly one value.", nameof(values));
        if (@operator is QueryComparisonOperator.Contains or QueryComparisonOperator.StartsWith or
            QueryComparisonOperator.GreaterThan or QueryComparisonOperator.GreaterThanOrEqual or
            QueryComparisonOperator.LessThan or QueryComparisonOperator.LessThanOrEqual && values[0] is null)
        {
            throw new ArgumentException($"{@operator} does not accept a null value.", nameof(values));
        }

        Path = path;
        Operator = @operator;
        Values = Array.AsReadOnly(values.ToArray());
    }

    public string Path { get; }
    public QueryComparisonOperator Operator { get; }
    public IReadOnlyList<string?> Values { get; }

    public static DocumentQueryComparison Equal(string path, string? value) => new(path, QueryComparisonOperator.Equal, [value]);
    public static DocumentQueryComparison In(string path, IEnumerable<string?> values) =>
        new(path, QueryComparisonOperator.In, (values ?? throw new ArgumentNullException(nameof(values))).ToArray());
    public static DocumentQueryComparison Contains(string path, string value) => new(path, QueryComparisonOperator.Contains, [value]);
    public static DocumentQueryComparison NotEqual(string path, string? value) => new(path, QueryComparisonOperator.NotEqual, [value]);
    public static DocumentQueryComparison StartsWith(string path, string value) => new(path, QueryComparisonOperator.StartsWith, [value]);
    public static DocumentQueryComparison GreaterThan(string path, string value) => new(path, QueryComparisonOperator.GreaterThan, [value]);
    public static DocumentQueryComparison GreaterThanOrEqual(string path, string value) => new(path, QueryComparisonOperator.GreaterThanOrEqual, [value]);
    public static DocumentQueryComparison LessThan(string path, string value) => new(path, QueryComparisonOperator.LessThan, [value]);
    public static DocumentQueryComparison LessThanOrEqual(string path, string value) => new(path, QueryComparisonOperator.LessThanOrEqual, [value]);
}

/// <summary>An OR clause in the bounded runtime query; clauses compose with AND.</summary>
public sealed class DocumentQueryClause
{
    public DocumentQueryClause(IReadOnlyList<DocumentQueryComparison> comparisons) =>
        Comparisons = Array.AsReadOnly((comparisons ?? throw new ArgumentNullException(nameof(comparisons))).ToArray());

    public IReadOnlyList<DocumentQueryComparison> Comparisons { get; }

    public static DocumentQueryClause MatchNone { get; } = new(Array.Empty<DocumentQueryComparison>());
    public static DocumentQueryClause AnyOf(params DocumentQueryComparison[] comparisons) => new(comparisons);
    public static DocumentQueryClause Of(DocumentQueryComparison comparison) => new([comparison]);
}

public sealed record DocumentQueryOrder(string Path, PhysicalSortDirection Direction = PhysicalSortDirection.Ascending);

/// <summary>
/// The single runtime request model for one declared bounded document query. Scope is not a caller
/// field: it is always inherited from the document-store session and injected by physical planning.
/// </summary>
public sealed class DocumentQuery
{
    public DocumentQuery(
        string documentKind,
        string queryIdentity,
        IReadOnlyList<DocumentQueryClause>? clauses = null,
        IReadOnlyList<DocumentQueryOrder>? order = null,
        int? skip = null,
        int? take = null,
        string? continuation = null,
        string? latestPerKeyPath = null,
        BoundedQueryResultOperation resultOperation = BoundedQueryResultOperation.Documents)
    {
        if (string.IsNullOrWhiteSpace(documentKind))
            throw new ArgumentException("Document kind must be provided.", nameof(documentKind));
        if (string.IsNullOrWhiteSpace(queryIdentity))
            throw new ArgumentException("Bounded query identity must be provided.", nameof(queryIdentity));
        if (skip is < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must be greater than or equal to 0.");
        if (take is < 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be greater than or equal to 0.");
        if (skip is not null && continuation is not null)
            throw new ArgumentException("Offset and keyset paging cannot be requested together.", nameof(continuation));
        if (continuation is not null && string.IsNullOrWhiteSpace(continuation))
            throw new ArgumentException("A keyset continuation cannot be empty.", nameof(continuation));
        if (latestPerKeyPath is not null && string.IsNullOrWhiteSpace(latestPerKeyPath))
            throw new ArgumentException("A latest-per-key stable path cannot be empty.", nameof(latestPerKeyPath));
        if (order is not null &&
            (order.Any(field => string.IsNullOrWhiteSpace(field.Path)) ||
             order.Select(field => field.Path).Distinct(StringComparer.Ordinal).Count() != order.Count))
            throw new ArgumentException("Compound order paths must be unique.", nameof(order));

        DocumentKind = documentKind;
        QueryIdentity = queryIdentity;
        Clauses = Array.AsReadOnly(clauses?.ToArray() ?? []);
        Order = Array.AsReadOnly(order?.ToArray() ?? []);
        Skip = skip;
        Take = take;
        Continuation = continuation;
        LatestPerKeyPath = latestPerKeyPath;
        ResultOperation = resultOperation;
    }

    public string DocumentKind { get; }
    public string QueryIdentity { get; }
    public IReadOnlyList<DocumentQueryClause> Clauses { get; }
    public IReadOnlyList<DocumentQueryOrder> Order { get; }
    public int? Skip { get; }
    public int? Take { get; }
    public string? Continuation { get; }
    public string? LatestPerKeyPath { get; }
    public BoundedQueryResultOperation ResultOperation { get; }

    public DocumentQuery Where(DocumentQueryClause clause) =>
        Copy(clauses: Clauses.Concat([clause]).ToArray());

    public DocumentQuery OrderBy(DocumentQueryOrder order) => Copy(order: [order]);

    public DocumentQuery ThenBy(DocumentQueryOrder order) => Copy(order: Order.Append(order).ToArray());

    public DocumentQuery Page(int? skip, int? take) =>
        new(DocumentKind, QueryIdentity, Clauses, Order, skip, take, null, LatestPerKeyPath, ResultOperation);

    public DocumentQuery ContinueAfter(string continuation) =>
        new(
            DocumentKind, QueryIdentity, Clauses, Order, null, Take,
            continuation ?? throw new ArgumentNullException(nameof(continuation)), LatestPerKeyPath, ResultOperation);

    public DocumentQuery LatestPerKey(string path) =>
        Copy(latestPerKeyPath: path ?? throw new ArgumentNullException(nameof(path)));

    public DocumentQuery Select(BoundedQueryResultOperation operation) => Copy(resultOperation: operation);

    /// <summary>Compatibility bridge used until providers consume physical plans directly.</summary>
    internal PortableDocumentQuery ToPortableDocumentQuery(PhysicalQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var comparisons = Clauses.SelectMany(clause => clause.Comparisons).ToArray();
        var plannedOrder = plan.Order.Where(order => !order.IsIdentityTieBreak).ToArray();
        var effectiveOrder = Order.Count == 0
            ? plannedOrder.Select(order => new DocumentQueryOrder(order.Path, order.Direction)).ToArray()
            : Order.ToArray();
        if (plan.LogicalIndexPaths.Count != 1 && (comparisons.Length != 0 || effectiveOrder.Length != 0))
        {
            throw new NotSupportedException(
                "The legacy provider bridge only addresses single-field logical indexes and cannot collapse compound stable paths.");
        }

        var stablePath = plan.LogicalIndexPaths.SingleOrDefault();
        if (comparisons.Any(comparison => comparison.Path != stablePath) ||
            effectiveOrder.Length > 1 ||
            effectiveOrder.Any(order => order.Path != stablePath) ||
            Continuation is not null ||
            LatestPerKeyPath is not null ||
            comparisons.Any(comparison => comparison.Operator is not (
                QueryComparisonOperator.Equal or QueryComparisonOperator.In or QueryComparisonOperator.Contains)))
        {
            throw new NotSupportedException(
                "The legacy provider bridge cannot represent this DocumentQuery shape; execute it through a bound physical query plan handler.");
        }

#pragma warning disable GW0004
        return new PortableDocumentQuery(
            DocumentKind,
            Clauses.Select(clause => new QueryClause(clause.Comparisons.Select(comparison =>
                new QueryComparison(plan.LogicalIndexIdentity, comparison.Operator, comparison.Values)).ToArray())).ToArray(),
            effectiveOrder.Length == 0
                ? null
                : new QueryOrder(
                    plan.LogicalIndexIdentity,
                    effectiveOrder[0].Direction == PhysicalSortDirection.Descending),
            Skip,
            Take);
#pragma warning restore GW0004
    }

    private DocumentQuery Copy(
        IReadOnlyList<DocumentQueryClause>? clauses = null,
        IReadOnlyList<DocumentQueryOrder>? order = null,
        string? latestPerKeyPath = null,
        BoundedQueryResultOperation? resultOperation = null) =>
        new(
            DocumentKind,
            QueryIdentity,
            clauses ?? Clauses,
            order ?? Order,
            Skip,
            Take,
            Continuation,
            latestPerKeyPath ?? LatestPerKeyPath,
            resultOperation ?? ResultOperation);

}

/// <summary>
/// The closed, provider-neutral document query: an <c>AND</c> of <c>OR</c>-groups of single-field
/// comparisons, with at most one ordering and optional offset paging. Scope is always inherited
/// from the document-store session. Zero clauses match all documents of the kind.
/// </summary>
[Obsolete(
    "Use DocumentQuery, which binds the runtime request to one BoundedQueryDeclaration identity.",
    DiagnosticId = "GW0004")]
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

    public DocumentQuery ToDocumentQuery(
        string queryIdentity,
        IReadOnlyDictionary<string, string> stablePathsByIndex) =>
        new(
            DocumentKind,
            queryIdentity,
            Clauses.Select(clause => new DocumentQueryClause(clause.Comparisons.Select(comparison =>
                new DocumentQueryComparison(
                    stablePathsByIndex[comparison.IndexName],
                    comparison.Operator,
                    comparison.Values)).ToArray())).ToArray(),
            Order is null
                ? null
                : [new DocumentQueryOrder(
                    stablePathsByIndex[Order.IndexName],
                    Order.Descending ? PhysicalSortDirection.Descending : PhysicalSortDirection.Ascending)],
            Skip,
            Take);

}

/// <summary>The result of a paged portable query: the page window plus the total predicate count.</summary>
public sealed record DocumentQueryResult(IReadOnlyList<DocumentEnvelope> Documents, long TotalCount)
{
    public static DocumentQueryResult Empty { get; } = new(Array.Empty<DocumentEnvelope>(), 0);
}
