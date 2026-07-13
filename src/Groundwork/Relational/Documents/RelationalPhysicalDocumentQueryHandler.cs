using System.Collections.Frozen;
using System.Data.Common;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

/// <summary>Reusable relational execution engine for one certified physical query source.</summary>
public class RelationalPhysicalDocumentQueryHandler : IPhysicalDocumentQueryHandler
{
    private readonly RelationalPhysicalDocumentStore store;

    public RelationalPhysicalDocumentQueryHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications)
    {
        Identity = string.IsNullOrWhiteSpace(identity) ? throw new ArgumentException("A handler identity is required.", nameof(identity)) : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Certifications = Array.AsReadOnly((certifications ?? throw new ArgumentNullException(nameof(certifications))).ToArray());
    }

    public string Identity { get; }
    public PhysicalQuerySourceKind Source { get; }
    public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } = Enum.GetValues<PortableQueryOperation>().ToFrozenSet();
    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } = FrozenDictionary<string, string>.Empty;
    public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; }
    public bool SupportsCompoundPredicates => true;
    public bool SupportsDisjunction => true;
    public bool SupportsOffsetPaging => true;
    public bool SupportsKeysetPaging => false;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => false;

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken) =>
        store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var built = Build(query, plan, route);
            var total = await CountCoreAsync(connection, built, ct);
            if (total == 0 || query.Take == 0)
                return new DocumentQueryResult([], total);

            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, store.ApplyOffsetPage(
                $"SELECT {EnvelopeSelection(route)} {built.FromAndWhere} {OrderBy(plan)}",
                store.P("take"),
                store.P("skip")));
            AddParameters(command, built.Parameters);
            store.AddPhysicalParameter(command, "take", query.Take ?? int.MaxValue);
            store.AddPhysicalParameter(command, "skip", query.Skip ?? 0);
            var documents = new List<DocumentEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(ReadEnvelope(reader));
            return new DocumentQueryResult(documents, total);
        }, cancellationToken);

    public Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken) =>
        store.ExecutePhysicalQueryAsync((connection, ct) =>
            CountCoreAsync(connection, Build(query, plan, store.GetRoute(query.DocumentKind)), ct), cancellationToken);

    internal RelationalPhysicalQueryCommand BuildCountCommand(DocumentQuery query, PhysicalQueryPlan plan) =>
        RenderCount(Build(query, plan, store.GetRoute(query.DocumentKind)));

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken) =>
        store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var built = Build(query, plan, route);
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, store.ApplyFirst(
                $"SELECT {EnvelopeSelection(route)} {built.FromAndWhere} {OrderBy(plan)}"));
            AddParameters(command, built.Parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadEnvelope(reader) : null;
        }, cancellationToken);

    public Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken) =>
        store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var built = Build(query, plan, store.GetRoute(query.DocumentKind));
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
                connection,
                store.ApplyFirst($"SELECT 1 {built.FromAndWhere}"));
            AddParameters(command, built.Parameters);
            return await command.ExecuteScalarAsync(ct) is not null;
        }, cancellationToken);

    public static PhysicalQueryHandlerCertification Certify(PhysicalQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var fields = new[] { plan.Scope.Field, plan.Discriminator }
            .Concat(plan.Predicates.Select(predicate => predicate.Field))
            .Concat(plan.Order.Select(order => order.Field))
            .GroupBy(field => field.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal);
        return new PhysicalQueryHandlerCertification(
            plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            plan.LogicalIndexPaths,
            plan.AccessKind,
            plan.Scope.Field.Target,
            plan.LookupObject,
            plan.PrimaryObject,
            plan.IndexName,
            fields,
            plan.RouteFingerprint);
    }

    internal RelationalPhysicalQueryPredicate BuildPredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope) =>
        Build(query, plan, store.GetRoute(query.DocumentKind), scope);

    private async Task<long> CountCoreAsync(DbConnection connection, RelationalPhysicalQueryPredicate built, CancellationToken ct)
    {
        var rendered = RenderCount(built);
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
        AddParameters(command, rendered.Parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static RelationalPhysicalQueryCommand RenderCount(RelationalPhysicalQueryPredicate built) =>
        new($"SELECT COUNT(*) {built.FromAndWhere};", built.Parameters);

    private RelationalPhysicalQueryPredicate Build(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection? fixedScope = null)
    {
        if (query.Continuation is not null)
            throw new NotSupportedException("This relational handler profile does not certify keyset continuations.");
        if (query.LatestPerKeyPath is not null)
            throw new NotSupportedException("This relational handler profile does not certify latest-per-key selection.");

        var linked = plan.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary;
        var from = linked
            ? $"FROM {store.PhysicalQuerySource(route.LinkedIndexStorage!.Name.Identifier, "l", plan.IndexName?.Identifier)} " +
              $"JOIN {store.PhysicalQuerySource(route.PrimaryStorage.Name.Identifier, "p", null)} ON " +
              store.ExactPhysicalIdentityJoin(
              [
                  new(route.Envelope.DocumentKind.Identifier, "p", route.LinkedRelationship!.DocumentKind.Identifier, "l"),
                  new(route.Envelope.StorageScope.Identifier, "p", route.LinkedRelationship.StorageScope.Identifier, "l"),
                  new(route.Envelope.Id.Identifier, "p", route.LinkedRelationship.DocumentId.Identifier, "l")
              ])
            : $"FROM {store.PhysicalQuerySource(route.PrimaryStorage.Name.Identifier, "p", plan.IndexName?.Identifier)}";
        var parameters = new List<(string Name, object? Value)>();
        var predicates = new List<string>();
        var scope = fixedScope ?? store.ResolveQueryScope(query.DocumentKind);
        if (!scope.AcrossScopes)
        {
            predicates.Add(store.ExactPhysicalIdentityPredicate(
                [new(plan.Scope.Field.Identifier, Alias(plan.Scope.Field), store.P("scope"))]));
            parameters.Add(("scope", scope.StorageKey));
        }
        predicates.Add(store.ExactPhysicalIdentityPredicate(
            [new(plan.Discriminator.Identifier, Alias(plan.Discriminator), store.P("kind"))]));
        parameters.Add(("kind", route.Discriminator.Value));

        var parameterIndex = 0;
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
            {
                predicates.Add("0 = 1");
                continue;
            }
            var alternatives = clause.Comparisons.Select(comparison =>
                Comparison(plan, route, comparison, parameters, ref parameterIndex)).ToArray();
            predicates.Add($"({string.Join(" OR ", alternatives)})");
        }
        if (parameters.Count > store.MaxPhysicalParameters - 2)
        {
            throw new InvalidOperationException(
                $"Document query '{query.QueryIdentity}' requires {parameters.Count + 2} parameters, exceeding the provider limit of {store.MaxPhysicalParameters}.");
        }
        return new RelationalPhysicalQueryPredicate($"{from} WHERE {string.Join(" AND ", predicates)}", parameters);
    }

    private string Comparison(
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentQueryComparison comparison,
        List<(string Name, object? Value)> parameters,
        ref int parameterIndex)
    {
        var predicate = plan.Predicates.Single(item => item.Path == comparison.Path);
        var field = Field(predicate.Field);
        var projection = predicate.Field.Source == PhysicalQueryFieldSource.ProjectedColumn
            ? route.ProjectedColumns.Single(column =>
                column.Target == predicate.Field.Target &&
                column.Definition.Path == comparison.Path)
            : null;
        object? Convert(string? value) => value is null
            ? null
            : projection is null
                ? RelationalPhysicalProjectionValues.ConvertScalar(value, predicate.Field.ValueKind)
                : store.ConvertPhysicalQueryValue(value, predicate.Field.ValueKind, projection.Definition);
        if (comparison.Operator == QueryComparisonOperator.In)
        {
            if (comparison.Values.Count == 0)
                return "0 = 1";
            var parts = new List<string>();
            foreach (var value in comparison.Values)
                parts.Add(ScalarComparison(predicate.Field, field, QueryComparisonOperator.Equal, Convert(value), parameters, ref parameterIndex));
            return $"({string.Join(" OR ", parts)})";
        }
        return ScalarComparison(predicate.Field, field, comparison.Operator, Convert(comparison.Values[0]), parameters, ref parameterIndex);
    }

    private string ScalarComparison(
        PhysicalQueryField queryField,
        string field,
        QueryComparisonOperator operation,
        object? value,
        List<(string Name, object? Value)> parameters,
        ref int parameterIndex)
    {
        if (value is null)
            return operation == QueryComparisonOperator.NotEqual ? $"{field} IS NOT NULL" : $"{field} IS NULL";
        var name = $"q{parameterIndex++}";
        parameters.Add((name, operation switch
        {
            QueryComparisonOperator.Contains => ContainsPattern.Build((string)value),
            QueryComparisonOperator.StartsWith => ContainsPattern.BuildStartsWith((string)value),
            _ => value
        }));
        var parameter = store.NormalizeQueryExpression(store.P(name), queryField.Source, queryField.ValueKind);
        return operation switch
        {
            QueryComparisonOperator.Equal => $"{field} = {parameter}",
            QueryComparisonOperator.NotEqual => $"{field} <> {parameter}",
            QueryComparisonOperator.GreaterThan => $"{field} > {parameter}",
            QueryComparisonOperator.GreaterThanOrEqual => $"{field} >= {parameter}",
            QueryComparisonOperator.LessThan => $"{field} < {parameter}",
            QueryComparisonOperator.LessThanOrEqual => $"{field} <= {parameter}",
            QueryComparisonOperator.Contains => store.Contains(field, parameter),
            QueryComparisonOperator.StartsWith => store.StartsWith(field, parameter),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private string Field(PhysicalQueryField field)
    {
        var alias = Alias(field);
        var expression = $"{alias}.{store.Q(field.Identifier)}";
        var value = field.Source == PhysicalQueryFieldSource.CanonicalJsonPath
            ? store.JsonValue(expression, field.Path)
            : expression;
        return store.NormalizeQueryExpression(value, field.Source, field.ValueKind);
    }

    private static string Alias(PhysicalQueryField field) =>
        field.Target == ExecutableStorageObjectRole.LinkedIndexStorage ? "l" : "p";

    private string OrderBy(PhysicalQueryPlan plan) =>
        plan.Order.Count == 0
            ? string.Empty
            : "ORDER BY " + string.Join(", ", plan.Order.Select(order =>
                $"{Field(order.Field)} {(order.Direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}"));

    private string EnvelopeSelection(ExecutableStorageRoute route) => string.Join(", ", new[]
    {
        route.Envelope.DocumentKind.Identifier,
        route.Envelope.StorageScope.Identifier,
        route.Envelope.Id.Identifier,
        route.Envelope.SchemaVersion.Identifier,
        route.Envelope.Version.Identifier,
        route.Envelope.CanonicalJson.Identifier,
        RelationalPhysicalStorageColumns.CreatedUtc,
        RelationalPhysicalStorageColumns.UpdatedUtc
    }.Select(column => $"p.{store.Q(column)}"));

    private void AddParameters(DbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
    }

    private static DocumentEnvelope ReadEnvelope(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7)))
        { Scope = DocumentStoreScopeResolver.ReadScope(reader.GetString(1)) };

}

internal sealed record RelationalPhysicalQueryPredicate(
    string FromAndWhere,
    IReadOnlyList<(string Name, object? Value)> Parameters);

internal sealed record RelationalPhysicalQueryCommand(
    string CommandText,
    IReadOnlyList<(string Name, object? Value)> Parameters);
