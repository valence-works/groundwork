namespace Groundwork.DiagnosticRecords.Relational;

internal sealed class RelationalDiagnosticQueryBuilder(
    DiagnosticRecordStreamDefinition definition,
    RelationalDiagnosticRecordDialect dialect)
{
    private readonly Dictionary<string, object> parameters = new(StringComparer.Ordinal);
    private int parameterIndex;

    public RelationalDiagnosticCommand Build(DiagnosticRecordQuery query, long snapshotHighWater)
    {
        AddScope(query.Scope, query.Stream);
        AddNamed("snapshot", snapshotHighWater);
        AddNamed("take", query.Limit + 1);
        var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
        var orderField = order.Field is null ? null : DiagnosticRecordFieldResolver.Resolve(definition, order.Field)!;
        var latestField = query.LatestPerKeyField is null ? null : DiagnosticRecordFieldResolver.Resolve(definition, query.LatestPerKeyField)!;
        var joins = new List<string>();
        var select = "r.tenant_id, r.scope_id, r.stream_id, r.cursor, r.record_id, r.occurred_at_ticks, r.payload_json";
        if (orderField is not null)
        {
            if (StringComparer.Ordinal.Equals(orderField.Name, DiagnosticRecordFieldNames.OccurredAt))
            {
                select += ", NULL AS order_value, r.occurred_at_ticks AS order_key";
            }
            else
            {
                AddNamed("orderField", orderField.Name);
                AddNamed("orderFieldType", (int)orderField.Type);
                joins.Add($"JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "ofield")} ON {FieldJoin("ofield", "r")} AND ofield.field_name = {dialect.Parameter("orderField")} AND ofield.field_type = {dialect.Parameter("orderFieldType")} AND ofield.value_ordinal = 0");
                select += ", ofield.canonical_value AS order_value, ofield.comparison_key AS order_key";
            }
        }
        else
        {
            select += ", NULL AS order_value, NULL AS order_key";
        }

        if (latestField is not null)
        {
            AddNamed("latestField", latestField.Name);
            AddNamed("latestFieldType", (int)latestField.Type);
            select += ", 1 AS latest_rank";
        }
        else
        {
            select += ", 1 AS latest_rank";
        }

        var predicates = new List<string>
        {
            $"r.tenant_id = {dialect.Parameter("tenant")}",
            $"r.scope_id = {dialect.Parameter("scope")}",
            $"r.stream_id = {dialect.Parameter("stream")}",
            latestField is null
                ? $"r.cursor <= {dialect.Parameter("snapshot")}"
                : $"r.cursor <= {dialect.Parameter("snapshot")}"
        };
        if (query.Predicate is not null && latestField is null)
            predicates.Add(BuildPredicate(query.Predicate));

        var from = latestField is null
            ? dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "r")
            : $"latest_winners JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "r")} ON r.tenant_id = {dialect.Parameter("tenant")} AND r.scope_id = {dialect.Parameter("scope")} AND r.stream_id = {dialect.Parameter("stream")} AND r.cursor = latest_winners.cursor";

        var latestSql = latestField is null
            ? null
            : $"""
              latest_winners AS (
                  SELECT lfield.comparison_key, MAX(lfield.cursor) AS cursor
                  FROM {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "lfield")}
                  {(query.Predicate is null ? "" : $"JOIN {dialect.TableReference(RelationalDiagnosticRecordSchema.RecordsTable, "lr")} ON {FieldJoin("lfield", "lr")}")}
                  WHERE lfield.tenant_id = {dialect.Parameter("tenant")}
                    AND lfield.scope_id = {dialect.Parameter("scope")}
                    AND lfield.stream_id = {dialect.Parameter("stream")}
                    AND lfield.field_name = {dialect.Parameter("latestField")}
                    AND lfield.field_type = {dialect.Parameter("latestFieldType")}
                    AND lfield.value_ordinal = 0
                    AND lfield.cursor <= {dialect.Parameter("snapshot")}
                    {(query.Predicate is null ? "" : $"AND {BuildPredicate(query.Predicate, "lr")}")}
                  GROUP BY lfield.comparison_key
              ),
              """;

        var candidateSql = $"""
            SELECT {select}
            FROM {from}
            {string.Join(Environment.NewLine, joins)}
            WHERE {string.Join(" AND ", predicates)}
            """;
        var selected = "SELECT * FROM candidates WHERE latest_rank = 1";
        var continuation = BuildContinuation(query.Continuation, order, orderField);
        var direction = order.Direction == DiagnosticSortDirection.Ascending ? "ASC" : "DESC";
        var orderBy = orderField is null
            ? $"cursor {direction}"
            : $"order_key {direction}, cursor {direction}";
        var pageSql = $"SELECT * FROM selected{(continuation is null ? "" : $" WHERE {continuation}")} ORDER BY {orderBy}";
        pageSql = dialect.ApplyLimit(pageSql, "take");
        return new(
            $"WITH {latestSql} candidates AS ({candidateSql}), selected AS ({selected}) {pageSql};",
            new Dictionary<string, object>(parameters, StringComparer.Ordinal));
    }

    public RelationalDiagnosticCommand BuildCount(DiagnosticRecordQuery query, long snapshotHighWater)
    {
        var page = Build(query with { Limit = 1, Continuation = null }, snapshotHighWater);
        return new(dialect.BuildCountFromPage(page.CommandText), page.Parameters);
    }

    private string BuildPredicate(DiagnosticRecordPredicate predicate, string recordAlias = "r") => predicate switch
    {
        DiagnosticRecordPredicate.All all => $"({string.Join(" AND ", all.Predicates.Select(predicate => BuildPredicate(predicate, recordAlias)))})",
        DiagnosticRecordPredicate.Any any => $"({string.Join(" OR ", any.Predicates.Select(predicate => BuildPredicate(predicate, recordAlias)))})",
        DiagnosticRecordPredicate.Comparison comparison => BuildComparison(comparison, recordAlias),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    private string BuildComparison(DiagnosticRecordPredicate.Comparison comparison, string recordAlias)
    {
        var field = DiagnosticRecordFieldResolver.Resolve(definition, comparison.Field)!;
        if (StringComparer.Ordinal.Equals(field.Name, DiagnosticRecordFieldNames.OccurredAt))
            return BuildValueExpression($"{recordAlias}.occurred_at_ticks", comparison, "value", value => DateTimeOffset.Parse(value.CanonicalValue).UtcTicks);

        var fieldParameter = Add("field", field.Name);
        var fieldTypeParameter = Add("fieldType", (int)field.Type);
        var usesCanonicalContains = comparison.Operator == DiagnosticPredicateOperator.Contains &&
                                    field.CasePolicy == DiagnosticStringCasePolicy.Ordinal;
        var valueExpression = BuildValueExpression(
            usesCanonicalContains ? "f.canonical_value" : "f.comparison_key",
            comparison,
            usesCanonicalContains ? "canonicalValue" : "value",
            usesCanonicalContains
                ? value => value.CanonicalValue
                : value => DiagnosticComparisonKeys.Create(value, field.CasePolicy));
        return $"EXISTS (SELECT 1 FROM {dialect.TableReference(RelationalDiagnosticRecordSchema.FieldsTable, "f")} WHERE {FieldJoin("f", recordAlias)} AND f.field_name = {fieldParameter} AND f.field_type = {fieldTypeParameter} AND {valueExpression})";
    }

    private string BuildValueExpression(
        string expression,
        DiagnosticRecordPredicate.Comparison comparison,
        string parameterPrefix,
        Func<DiagnosticFieldValue, object> convert)
    {
        var values = comparison.Values.Select(value => Add(parameterPrefix, convert(value))).ToArray();
        return comparison.Operator switch
        {
            DiagnosticPredicateOperator.Equal => $"{expression} = {values[0]}",
            DiagnosticPredicateOperator.In => $"{expression} IN ({string.Join(", ", values)})",
            DiagnosticPredicateOperator.RangeInclusive => $"{expression} BETWEEN {values[0]} AND {values[1]}",
            DiagnosticPredicateOperator.Contains => dialect.Contains(expression, values[0][1..]),
            _ => throw new ArgumentOutOfRangeException(nameof(comparison.Operator))
        };
    }

    private string? BuildContinuation(
        DiagnosticRecordContinuation? continuation,
        DiagnosticRecordOrder order,
        DiagnosticFieldDefinition? orderField)
    {
        if (continuation is null)
            return null;
        var lastCursor = Add("lastCursor", long.Parse(continuation.LastCursor.Value, System.Globalization.CultureInfo.InvariantCulture));
        var comparison = order.Direction == DiagnosticSortDirection.Ascending ? ">" : "<";
        if (orderField is null)
            return $"cursor {comparison} {lastCursor}";
        var lastOrder = Add(
            "lastOrder",
            StringComparer.Ordinal.Equals(orderField.Name, DiagnosticRecordFieldNames.OccurredAt)
                ? DateTimeOffset.Parse(continuation.LastOrderValue!.Value.CanonicalValue).UtcTicks
                : DiagnosticComparisonKeys.Create(continuation.LastOrderValue!.Value, orderField.CasePolicy));
        return $"(order_key {comparison} {lastOrder} OR (order_key = {lastOrder} AND cursor {comparison} {lastCursor}))";
    }

    private void AddScope(DiagnosticStorageScope scope, DiagnosticStreamId stream)
    {
        AddNamed("tenant", scope.TenantId);
        AddNamed("scope", scope.ScopeId);
        AddNamed("stream", stream.Value);
    }

    private void AddNamed(string name, object value) => parameters.Add(name, value);

    private string Add(string prefix, object value)
    {
        var name = $"{prefix}{parameterIndex++}";
        parameters.Add(name, value);
        return dialect.Parameter(name);
    }

    private static string FieldJoin(string fieldAlias, string recordAlias) =>
        $"{fieldAlias}.tenant_id = {recordAlias}.tenant_id AND {fieldAlias}.scope_id = {recordAlias}.scope_id AND {fieldAlias}.stream_id = {recordAlias}.stream_id AND {fieldAlias}.cursor = {recordAlias}.cursor";
}
