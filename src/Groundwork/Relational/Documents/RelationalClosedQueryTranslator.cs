using System.Text;
using Groundwork.Core.Manifests;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

/// <summary>
/// Translates a <see cref="PortableDocumentQuery"/> into a parameterized SQL predicate over the
/// generic <c>groundwork_document_indexes</c> table using EXISTS sub-queries, honouring the closed
/// contract's operator, composition, ordering, paging, and tenant-scope semantics.
/// </summary>
internal sealed class RelationalClosedQueryTranslator
{
    private const string Columns = "d.document_kind, d.id, d.schema_version, d.version, d.content_json, d.created_utc, d.updated_utc";
    private const string IndexTable = "groundwork_document_indexes";

    private readonly RelationalDocumentStoreDialect dialect;
    private readonly Dictionary<string, object> parameters = new();
    private int aliasCounter;
    private int parameterCounter;
    private int whereParameterCount;

    private RelationalClosedQueryTranslator(RelationalDocumentStoreDialect dialect) => this.dialect = dialect;

    /// <summary>Parameters referenced by the WHERE predicate, shared by both SELECT and COUNT.</summary>
    public IEnumerable<KeyValuePair<string, object>> WhereParameters =>
        parameters.Where(parameter => ParameterIndex(parameter.Key) < whereParameterCount);

    /// <summary>All parameters, including those referenced only by the ORDER BY clause.</summary>
    public IReadOnlyDictionary<string, object> Parameters => parameters;

    public static RelationalClosedQueryTranslator Translate(
        StorageUnit unit,
        PortableDocumentQuery query,
        RelationalDocumentStoreDialect dialect,
        string? ambientTenantId,
        out string whereSql,
        out string orderSql)
    {
        var translator = new RelationalClosedQueryTranslator(dialect);
        whereSql = translator.BuildWhere(unit, query, ambientTenantId);
        translator.whereParameterCount = translator.parameterCounter;
        orderSql = translator.BuildOrder(unit, query);
        return translator;
    }

    private static int ParameterIndex(string name) => int.Parse(name.AsSpan(1));

    public string SelectSql(string whereSql, string orderSql, int skip, int? take)
    {
        var paging = dialect.PaginationClause(skip, take);
        var builder = new StringBuilder();
        builder.Append("SELECT ").Append(Columns).Append(" FROM groundwork_documents d WHERE ").Append(whereSql);
        builder.Append(' ').Append(orderSql);
        if (paging.Length > 0)
            builder.Append(' ').Append(paging);
        return builder.Append(';').ToString();
    }

    public string CountSql(string whereSql) =>
        $"SELECT COUNT(*) FROM groundwork_documents d WHERE {whereSql};";

    private string BuildWhere(StorageUnit unit, PortableDocumentQuery query, string? ambientTenantId)
    {
        var builder = new StringBuilder();
        builder.Append("d.document_kind = ").Append(AddParameter(unit.Identity.Value));

        foreach (var clause in query.Clauses)
            builder.Append(" AND ").Append(BuildClause(unit, clause));

        var tenantPredicate = BuildTenantPredicate(unit, query, ambientTenantId);
        if (tenantPredicate is not null)
            builder.Append(" AND ").Append(tenantPredicate);

        return builder.ToString();
    }

    private string BuildClause(StorageUnit unit, QueryClause clause)
    {
        if (clause.Comparisons.Count == 0)
            return "(1 = 0)";

        var predicates = clause.Comparisons.Select(comparison => BuildComparison(unit, comparison));
        return "(" + string.Join(" OR ", predicates) + ")";
    }

    private string BuildComparison(StorageUnit unit, QueryComparison comparison)
    {
        var index = ClosedQueryIndexResolver.ResolveComparisonIndex(unit, comparison.IndexName, comparison.Operator);
        var indexParameter = AddParameter(index.Identity);

        return comparison.Operator switch
        {
            QueryComparisonOperator.Equal => BuildEqual(comparison, indexParameter),
            QueryComparisonOperator.In => BuildIn(comparison, indexParameter),
            QueryComparisonOperator.Contains => BuildContains(comparison, indexParameter),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison.Operator, "Unsupported operator.")
        };
    }

    private string BuildEqual(QueryComparison comparison, string indexParameter)
    {
        var value = comparison.Values.Count > 0 ? comparison.Values[0] : null;
        return value is null
            ? NotExists(indexParameter)
            : $"({Exists(indexParameter, $"{Alias()}.index_value = {AddParameter(value)}")})";
    }

    private string BuildIn(QueryComparison comparison, string indexParameter)
    {
        var nonNull = comparison.Values.Where(value => value is not null).Cast<string>().ToList();
        var hasNull = comparison.Values.Any(value => value is null);

        if (nonNull.Count == 0)
            return hasNull ? NotExists(indexParameter) : "(1 = 0)";

        var alias = aliasCounter; // capture alias used by the next Exists call
        var inList = string.Join(", ", nonNull.Select(AddParameter));
        var membership = $"({Exists(indexParameter, $"x{alias}.index_value IN ({inList})")})";

        return hasNull ? $"({membership} OR {NotExists(indexParameter)})" : membership;
    }

    private string BuildContains(QueryComparison comparison, string indexParameter)
    {
        var value = comparison.Values.Count > 0 ? comparison.Values[0] : null;
        if (value is null)
            throw new InvalidOperationException("Contains requires a non-null value.");

        var alias = aliasCounter;
        var pattern = AddParameter(ContainsPattern.Build(value));
        var predicate = dialect.ContainsPredicate($"x{alias}.index_value", ParameterNameOf(pattern));
        return $"({Exists(indexParameter, predicate)})";
    }

    private string? BuildTenantPredicate(StorageUnit unit, PortableDocumentQuery query, string? ambientTenantId)
    {
        if (query.TenantScope == QueryTenantScope.TenantAgnostic || ambientTenantId is null)
            return null;

        var tenantIndex = ClosedQueryIndexResolver.ResolveTenantIndex(unit);
        if (tenantIndex is null)
            return null;

        var indexParameter = AddParameter(tenantIndex.Identity);
        return $"({Exists(indexParameter, $"{Alias()}.index_value = {AddParameter(ambientTenantId)}")})";
    }

    private string BuildOrder(StorageUnit unit, PortableDocumentQuery query)
    {
        if (query.Order is null)
            return "ORDER BY d.id";

        var index = ClosedQueryIndexResolver.ResolveOrderIndex(unit, query.Order.IndexName);
        var indexParameter = AddParameter(index.Identity);
        var direction = query.Order.Descending ? "DESC" : "ASC";
        var subquery =
            $"(SELECT xo.index_value FROM {IndexTable} xo " +
            $"WHERE xo.document_kind = d.document_kind AND xo.document_id = d.id AND xo.index_name = {indexParameter})";
        return $"ORDER BY {subquery} {direction}, d.id";
    }

    private string Exists(string indexParameter, string valuePredicate)
    {
        var alias = $"x{aliasCounter++}";
        return
            $"EXISTS (SELECT 1 FROM {IndexTable} {alias} " +
            $"WHERE {alias}.document_kind = d.document_kind AND {alias}.document_id = d.id " +
            $"AND {alias}.index_name = {indexParameter} AND {valuePredicate})";
    }

    private string NotExists(string indexParameter)
    {
        var alias = $"x{aliasCounter++}";
        return
            $"NOT EXISTS (SELECT 1 FROM {IndexTable} {alias} " +
            $"WHERE {alias}.document_kind = d.document_kind AND {alias}.document_id = d.id " +
            $"AND {alias}.index_name = {indexParameter})";
    }

    private string Alias() => $"x{aliasCounter}";

    private string AddParameter(object value)
    {
        var name = $"p{parameterCounter++}";
        parameters[name] = value;
        return dialect.Parameter(name);
    }

    private string ParameterNameOf(string parameterReference) =>
        parameterReference.StartsWith(dialect.ParameterPrefix, StringComparison.Ordinal)
            ? parameterReference[dialect.ParameterPrefix.Length..]
            : parameterReference;
}
