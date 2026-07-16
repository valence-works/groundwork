using System.Collections.Frozen;
using System.Data.Common;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

internal sealed record RelationalPhysicalNativeQueryPlan(string Format, string Content);

internal delegate Task<RelationalPhysicalNativeQueryPlan> RelationalPhysicalQueryExplainExecutor(
    DbCommand command,
    CancellationToken cancellationToken);

internal sealed record RelationalPhysicalQueryExplainCommand(
    PhysicalDocumentQueryCommandKind Kind,
    string Identity,
    RelationalPhysicalQueryCommand Command);

/// <summary>Reusable relational execution engine for one certified physical query source.</summary>
public class RelationalPhysicalDocumentQueryHandler : IPhysicalDocumentQueryHandler
{
    private readonly RelationalPhysicalDocumentStore store;
    private readonly RelationalPhysicalQueryExplainExecutor? explain;

    public RelationalPhysicalDocumentQueryHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications)
        : this(identity, source, store, certifications, null)
    {
    }

    internal RelationalPhysicalDocumentQueryHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications,
        RelationalPhysicalQueryExplainExecutor? explain)
    {
        Identity = string.IsNullOrWhiteSpace(identity) ? throw new ArgumentException("A handler identity is required.", nameof(identity)) : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.explain = explain;
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

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        ValidateDocumentIdentityValues(query.Clauses);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var scope = store.ResolveQueryScope(query.DocumentKind);
            await ThrowIfLinkedIdentityCollisionAsync(connection, BuildCollisionCheckCommand(query, plan, route, scope), route, ct);
            var total = await CountCoreAsync(connection, BuildCountCommand(query, plan, route, scope), ct);
            if (total == 0 || query.Take == 0)
                return new DocumentQueryResult([], total);

            var rendered = BuildQueryCommand(query, plan, route, scope);
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
            AddParameters(command, rendered.Parameters);
            var documents = new List<DocumentEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(store.ReadPhysicalEnvelope(reader).Envelope);
            return new DocumentQueryResult(documents, total);
        }, cancellationToken);
    }

    public Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        ValidateDocumentIdentityValues(query.Clauses);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var scope = store.ResolveQueryScope(query.DocumentKind);
            await ThrowIfLinkedIdentityCollisionAsync(connection, BuildCollisionCheckCommand(query, plan, route, scope), route, ct);
            return await CountCoreAsync(connection, BuildCountCommand(query, plan, route, scope), ct);
        }, cancellationToken);
    }

    internal RelationalPhysicalQueryCommand BuildCountCommand(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var route = store.GetRoute(query.DocumentKind);
        return BuildCountCommand(query, plan, route, store.ResolveQueryScope(query.DocumentKind));
    }

    internal RelationalPhysicalQueryCommand BuildQueryCommand(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var route = store.GetRoute(query.DocumentKind);
        return BuildQueryCommand(query, plan, route, store.ResolveQueryScope(query.DocumentKind));
    }

    public Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken)
    {
        if (explain is null)
        {
            return Task.FromException<PhysicalDocumentQueryExplanation>(new NotSupportedException(
                $"Physical query handler '{Identity}' does not support provider-native explain evidence."));
        }

        ValidateDocumentIdentityValues(query.Clauses);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var scope = store.ResolveQueryScope(query.DocumentKind);
            var commands = new List<PhysicalDocumentQueryCommandExplanation>();
            foreach (var explained in BuildExplainCommands(query, plan, route, scope))
            {
                await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
                    connection,
                    explained.Command.CommandText);
                AddParameters(command, explained.Command.Parameters);
                var native = await explain(command, ct);
                commands.Add(new PhysicalDocumentQueryCommandExplanation(
                    explained.Kind,
                    explained.Identity,
                    native.Format,
                    native.Content,
                    explained.Command.PredicateFieldIdentifiers));
            }
            return new PhysicalDocumentQueryExplanation(
                plan,
                PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, scope),
                commands);
        }, cancellationToken);
    }

    private IReadOnlyList<RelationalPhysicalQueryExplainCommand> BuildExplainCommands(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope)
    {
        var commands = new List<RelationalPhysicalQueryExplainCommand>();
        if (BuildCollisionCheckCommand(query, plan, route, scope) is { } collision)
        {
            commands.Add(new RelationalPhysicalQueryExplainCommand(
                PhysicalDocumentQueryCommandKind.LinkedIdentityCollisionCheck,
                PhysicalDocumentQueryCommandIdentities.LinkedIdentityCollisionCheck,
                collision));
        }

        switch (query.ResultOperation)
        {
            case BoundedQueryResultOperation.Documents:
                commands.Add(new RelationalPhysicalQueryExplainCommand(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    BuildCountCommand(query, plan, route, scope)));
                if (query.Take != 0)
                {
                    commands.Add(new RelationalPhysicalQueryExplainCommand(
                        PhysicalDocumentQueryCommandKind.Page,
                        PhysicalDocumentQueryCommandIdentities.Page,
                        BuildQueryCommand(query, plan, route, scope)));
                }
                break;
            case BoundedQueryResultOperation.Count:
                commands.Add(new RelationalPhysicalQueryExplainCommand(
                    PhysicalDocumentQueryCommandKind.Count,
                    PhysicalDocumentQueryCommandIdentities.Count,
                    BuildCountCommand(query, plan, route, scope)));
                break;
            case BoundedQueryResultOperation.First:
                commands.Add(new RelationalPhysicalQueryExplainCommand(
                    PhysicalDocumentQueryCommandKind.First,
                    PhysicalDocumentQueryCommandIdentities.First,
                    BuildFirstCommand(query, plan, route, scope)));
                break;
            case BoundedQueryResultOperation.Any:
                commands.Add(new RelationalPhysicalQueryExplainCommand(
                    PhysicalDocumentQueryCommandKind.Any,
                    PhysicalDocumentQueryCommandIdentities.Any,
                    BuildAnyCommand(query, plan, route, scope)));
                break;
            default:
                throw new NotSupportedException(
                    $"Provider-native explain does not support result operation '{query.ResultOperation}'.");
        }

        return commands;
    }

    private RelationalPhysicalQueryCommand RenderQuery(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        RelationalPhysicalQueryPredicate built,
        ExecutableStorageRoute route)
    {
        var parameters = built.Parameters
            .Concat(new[]
            {
                ("take", (object?)(query.Take ?? int.MaxValue)),
                ("skip", (object?)(query.Skip ?? 0))
            })
            .ToArray();
        return new RelationalPhysicalQueryCommand(
            store.ApplyOffsetPage(
                $"SELECT {EnvelopeSelection(route)} {built.FromAndWhere} {OrderBy(plan)}",
                store.P("take"),
                store.P("skip")),
            parameters,
            built.PredicateFieldIdentifiers);
    }

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        ValidateDocumentIdentityValues(query.Clauses);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var scope = store.ResolveQueryScope(query.DocumentKind);
            await ThrowIfLinkedIdentityCollisionAsync(connection, BuildCollisionCheckCommand(query, plan, route, scope), route, ct);
            var rendered = BuildFirstCommand(query, plan, route, scope);
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
            AddParameters(command, rendered.Parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? store.ReadPhysicalEnvelope(reader).Envelope : null;
        }, cancellationToken);
    }

    public Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        ValidateDocumentIdentityValues(query.Clauses);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var route = store.GetRoute(query.DocumentKind);
            var scope = store.ResolveQueryScope(query.DocumentKind);
            await ThrowIfLinkedIdentityCollisionAsync(connection, BuildCollisionCheckCommand(query, plan, route, scope), route, ct);
            var rendered = BuildAnyCommand(query, plan, route, scope);
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
            AddParameters(command, rendered.Parameters);
            return await command.ExecuteScalarAsync(ct) is not null;
        }, cancellationToken);
    }

    public static PhysicalQueryHandlerCertification Certify(PhysicalQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var fields = plan.RequiredFields
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
        DocumentScopeSelection scope,
        bool linkedIdentityOnly = false) =>
        Build(
            query,
            plan,
            store.GetRoute(query.DocumentKind),
            requiresPrimaryLookup: !linkedIdentityOnly,
            fixedScope: scope);

    private RelationalPhysicalQueryCommand BuildCountCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope) =>
        RenderCount(Build(query, plan, route, requiresPrimaryLookup: false, fixedScope: scope));

    private RelationalPhysicalQueryCommand BuildQueryCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope) =>
        RenderQuery(query, plan, Build(query, plan, route, requiresPrimaryLookup: true, fixedScope: scope), route);

    private RelationalPhysicalQueryCommand BuildFirstCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope)
    {
        var built = Build(query, plan, route, requiresPrimaryLookup: true, fixedScope: scope);
        return new RelationalPhysicalQueryCommand(
            store.ApplyFirst($"SELECT {EnvelopeSelection(route)} {built.FromAndWhere} {OrderBy(plan)}"),
            built.Parameters,
            built.PredicateFieldIdentifiers);
    }

    private RelationalPhysicalQueryCommand BuildAnyCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope)
    {
        var built = Build(query, plan, route, requiresPrimaryLookup: true, fixedScope: scope);
        return new RelationalPhysicalQueryCommand(
            store.ApplyFirst($"SELECT 1 {built.FromAndWhere}"),
            built.Parameters,
            built.PredicateFieldIdentifiers);
    }

    private RelationalPhysicalQueryCommand? BuildCollisionCheckCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope)
    {
        if (plan.AccessKind != PhysicalQueryAccessKind.LinkedIndexThenPrimary)
            return null;

        var built = Build(
            query,
            plan,
            route,
            requiresPrimaryLookup: true,
            fixedScope: scope,
            detectIdentityCollision: true);
        var relationship = route.LinkedRelationship!;
        return new RelationalPhysicalQueryCommand(
            store.ApplyFirst(
                $"SELECT l.{store.Q(relationship.DocumentId.Identifier)}, " +
                $"p.{store.Q(route.Envelope.Id.Identifier)}, " +
                $"l.{store.Q(relationship.Identity.LookupKey.Identifier)} {built.FromAndWhere}"),
            built.Parameters,
            built.PredicateFieldIdentifiers);
    }

    private async Task<long> CountCoreAsync(
        DbConnection connection,
        RelationalPhysicalQueryCommand rendered,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
        AddParameters(command, rendered.Parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static RelationalPhysicalQueryCommand RenderCount(RelationalPhysicalQueryPredicate built) =>
        new($"SELECT COUNT(*) {built.FromAndWhere};", built.Parameters, built.PredicateFieldIdentifiers);

    private RelationalPhysicalQueryPredicate Build(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        bool requiresPrimaryLookup,
        DocumentScopeSelection? fixedScope = null,
        bool detectIdentityCollision = false)
    {
        if (query.Continuation is not null)
            throw new NotSupportedException("This relational handler profile does not certify keyset continuations.");
        if (query.LatestPerKeyPath is not null)
            throw new NotSupportedException("This relational handler profile does not certify latest-per-key selection.");

        var linked = plan.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary;
        var needsPrimaryJoin = linked && (detectIdentityCollision || requiresPrimaryLookup || PredicateFields(plan)
            .Any(field => field.Target == ExecutableStorageObjectRole.PrimaryStorage));
        var from = linked && needsPrimaryJoin
            ? $"FROM {store.PhysicalQuerySource(route.LinkedIndexStorage!.Name.Identifier, "l", plan.IndexName?.Identifier)} " +
              $"JOIN {store.PhysicalQuerySource(route.PrimaryStorage.Name.Identifier, "p", null)} ON " +
              store.ExactPhysicalIdentityJoin(LinkedPrimaryJoin(route, detectIdentityCollision))
            : linked
                ? $"FROM {store.PhysicalQuerySource(route.LinkedIndexStorage!.Name.Identifier, "l", plan.IndexName?.Identifier)}"
                : $"FROM {store.PhysicalQuerySource(route.PrimaryStorage.Name.Identifier, "p", plan.IndexName?.Identifier)}";
        var parameters = new List<(string Name, object? Value)>();
        var predicateFieldIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var predicates = new List<string>();
        var scope = fixedScope ?? store.ResolveQueryScope(query.DocumentKind);
        if (!scope.AcrossScopes)
        {
            predicates.Add(store.ExactPhysicalIdentityPredicate(
                [new(plan.Scope.Field.Identifier, Alias(plan.Scope.Field), store.P("scope"))]));
            parameters.Add(("scope", scope.StorageKey));
            predicateFieldIdentifiers.Add(plan.Scope.Field.Identifier);
        }
        predicates.Add(store.ExactPhysicalIdentityPredicate(
            [new(plan.Discriminator.Identifier, Alias(plan.Discriminator), store.P("kind"))]));
        parameters.Add(("kind", route.Discriminator.Value));
        predicateFieldIdentifiers.Add(plan.Discriminator.Identifier);

        if (detectIdentityCollision)
        {
            predicates.Add(
                $"p.{store.Q(route.Envelope.Identity.ComparisonKey.Identifier)} <> " +
                $"l.{store.Q(route.LinkedRelationship!.Identity.ComparisonKey.Identifier)}");
            predicateFieldIdentifiers.Add(route.Envelope.Identity.ComparisonKey.Identifier);
            predicateFieldIdentifiers.Add(route.LinkedRelationship.Identity.ComparisonKey.Identifier);
        }

        var parameterIndex = 0;
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count == 0)
            {
                predicates.Add("0 = 1");
                continue;
            }
            var alternatives = clause.Comparisons.Select(comparison =>
                Comparison(
                    plan,
                    route,
                    comparison,
                    parameters,
                    predicateFieldIdentifiers,
                    ref parameterIndex)).ToArray();
            predicates.Add($"({string.Join(" OR ", alternatives)})");
        }
        if (parameters.Count > store.MaxPhysicalParameters - 2)
        {
            throw new InvalidOperationException(
                $"Document query '{query.QueryIdentity}' requires {parameters.Count + 2} parameters, exceeding the provider limit of {store.MaxPhysicalParameters}.");
        }
        var fromAndWhere = $"{from} WHERE {string.Join(" AND ", predicates)}";
        return new RelationalPhysicalQueryPredicate(
            fromAndWhere,
            parameters,
            predicateFieldIdentifiers.ToArray());
    }

    private static IReadOnlyList<RelationalPhysicalIdentityJoinPart> LinkedPrimaryJoin(
        ExecutableStorageRoute route,
        bool lookupOnly)
    {
        var relationship = route.LinkedRelationship!;
        var identityJoin = new List<RelationalPhysicalIdentityJoinPart>
        {
            new(route.Envelope.DocumentKind.Identifier, "p", relationship.DocumentKind.Identifier, "l"),
            new(route.Envelope.StorageScope.Identifier, "p", relationship.StorageScope.Identifier, "l"),
            new(route.Envelope.Identity.LookupKey.Identifier, "p", relationship.Identity.LookupKey.Identifier, "l")
        };
        if (!lookupOnly)
        {
            identityJoin.Add(new(
                route.Envelope.Identity.ComparisonKey.Identifier,
                "p",
                relationship.Identity.ComparisonKey.Identifier,
                "l"));
        }
        return identityJoin;
    }

    private async Task ThrowIfLinkedIdentityCollisionAsync(
        DbConnection connection,
        RelationalPhysicalQueryCommand? rendered,
        ExecutableStorageRoute route,
        CancellationToken ct)
    {
        if (rendered is null)
            return;

        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
        AddParameters(command, rendered.Parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;

        throw new DocumentIdentityLookupCollisionException(
            route.Discriminator.Value,
            reader.GetString(0),
            reader.GetString(1),
            store.ReadDocumentIdentityLookup(reader, 2));
    }

    private static IEnumerable<PhysicalQueryField> PredicateFields(PhysicalQueryPlan plan) =>
        new[] { plan.Scope.Field, plan.Discriminator }
            .Concat(plan.Predicates.Select(predicate => predicate.Field));

    private string Comparison(
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentQueryComparison comparison,
        List<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        if (comparison.Path == PhysicalDocumentFieldPaths.Id)
        {
            return IdentityComparison(
                plan,
                comparison,
                parameters,
                predicateFieldIdentifiers,
                ref parameterIndex);
        }

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
            {
                parts.Add(ScalarComparison(
                    predicate.Field,
                    field,
                    QueryComparisonOperator.Equal,
                    Convert(value),
                    parameters,
                    predicateFieldIdentifiers,
                    ref parameterIndex));
            }
            return $"({string.Join(" OR ", parts)})";
        }
        return ScalarComparison(
            predicate.Field,
            field,
            comparison.Operator,
            Convert(comparison.Values[0]),
            parameters,
            predicateFieldIdentifiers,
            ref parameterIndex);
    }

    private string IdentityComparison(
        PhysicalQueryPlan plan,
        DocumentQueryComparison comparison,
        List<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        var bound = PhysicalDocumentIdentityQuery.Bind(plan, comparison);
        if (comparison.Operator == QueryComparisonOperator.In)
        {
            if (bound.Values.Count == 0)
                return "0 = 1";
            var alternatives = new List<string>();
            foreach (var value in bound.Values)
            {
                alternatives.Add(ExactIdentityComparison(
                    plan.DocumentIdentity,
                    RequireExact(value),
                    QueryComparisonOperator.Equal,
                    parameters,
                    predicateFieldIdentifiers,
                    ref parameterIndex));
            }
            return $"({string.Join(" OR ", alternatives)})";
        }

        var evidence = bound.Values.Single();
        return evidence switch
        {
            PhysicalQueryIdentityValue.Exact exact => ExactIdentityComparison(
                plan.DocumentIdentity,
                exact,
                comparison.Operator,
                parameters,
                predicateFieldIdentifiers,
                ref parameterIndex),
            PhysicalQueryIdentityValue.Ordered ordered => ScalarComparison(
                plan.DocumentIdentity.Comparison,
                Field(plan.DocumentIdentity.Comparison),
                comparison.Operator,
                ordered.ComparisonKey,
                parameters,
                predicateFieldIdentifiers,
                ref parameterIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
        };
    }

    private string ExactIdentityComparison(
        PhysicalQueryDocumentIdentityBinding identity,
        PhysicalQueryIdentityValue.Exact evidence,
        QueryComparisonOperator operation,
        List<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        var comparison = operation == QueryComparisonOperator.NotEqual
            ? QueryComparisonOperator.NotEqual
            : QueryComparisonOperator.Equal;
        var lookupPredicate = ScalarComparison(
            identity.Lookup,
            Field(identity.Lookup),
            comparison,
            evidence.LookupKey,
            parameters,
            predicateFieldIdentifiers,
            ref parameterIndex);
        var comparisonPredicate = ScalarComparison(
            identity.Comparison,
            Field(identity.Comparison),
            comparison,
            evidence.ComparisonKey,
            parameters,
            predicateFieldIdentifiers,
            ref parameterIndex);
        var conjunction = operation == QueryComparisonOperator.NotEqual ? " OR " : " AND ";
        return $"({lookupPredicate}{conjunction}{comparisonPredicate})";
    }

    private static PhysicalQueryIdentityValue.Exact RequireExact(PhysicalQueryIdentityValue value) =>
        value switch
        {
            PhysicalQueryIdentityValue.Exact exact => exact,
            PhysicalQueryIdentityValue.Ordered => throw new InvalidOperationException(
                "Identity membership requires exact lookup and comparison evidence."),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private string ScalarComparison(
        PhysicalQueryField queryField,
        string field,
        QueryComparisonOperator operation,
        object? value,
        List<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        predicateFieldIdentifiers.Add(queryField.Identifier);
        if (value is null)
            return operation == QueryComparisonOperator.NotEqual ? $"{field} IS NOT NULL" : $"{field} IS NULL";
        if (IsDocumentIdentityField(queryField) &&
            operation == QueryComparisonOperator.StartsWith)
        {
            return IdentityPrefixRange(queryField, field, (string)value, parameters, ref parameterIndex);
        }
        var name = $"q{parameterIndex++}";
        object parameterValue = operation switch
        {
            QueryComparisonOperator.Contains => ContainsPattern.Build((string)value),
            QueryComparisonOperator.StartsWith => ContainsPattern.BuildStartsWith((string)value),
            _ => value
        };
        if (IsDocumentIdentityField(queryField))
            parameterValue = store.ConvertDocumentIdentityQueryValue(queryField, (string)parameterValue);
        parameters.Add((name, parameterValue));
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

    private string IdentityPrefixRange(
        PhysicalQueryField queryField,
        string field,
        string comparisonKey,
        List<(string Name, object? Value)> parameters,
        ref int parameterIndex)
    {
        var range = store.ConvertDocumentIdentityPrefix(comparisonKey);
        var lowerName = $"q{parameterIndex++}";
        parameters.Add((lowerName, range.Lower));
        var lower = store.NormalizeQueryExpression(store.P(lowerName), queryField.Source, queryField.ValueKind);
        if (range.Upper is null)
            return $"{field} >= {lower}";
        var upperName = $"q{parameterIndex++}";
        parameters.Add((upperName, range.Upper));
        var upper = store.NormalizeQueryExpression(store.P(upperName), queryField.Source, queryField.ValueKind);
        return $"({field} >= {lower} AND {field} < {upper})";
    }

    internal void ValidateDocumentIdentityValues(IEnumerable<DocumentQueryClause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        foreach (var value in clauses
                     .SelectMany(clause => clause.Comparisons)
                     .Where(comparison => comparison.Path == PhysicalDocumentFieldPaths.Id)
                     .SelectMany(comparison => comparison.Values)
                     .Where(value => value is not null))
        {
            store.ValidateDocumentIdentity(value!);
        }
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

    private static bool IsDocumentIdentityField(PhysicalQueryField field) =>
        field.Path is PhysicalDocumentIdentityFieldPaths.Original or
            PhysicalDocumentIdentityFieldPaths.Comparison or
            PhysicalDocumentIdentityFieldPaths.Lookup;

    private string OrderBy(PhysicalQueryPlan plan) =>
        plan.Order.Count == 0
            ? string.Empty
            : "ORDER BY " + string.Join(", ", plan.Order.Select(order =>
                $"{Field(order.Field)} {(order.Direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}"));

    private string EnvelopeSelection(ExecutableStorageRoute route) => string.Join(", ",
        RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route)
            .Select(column => $"p.{store.Q(column)}"));

    private void AddParameters(DbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
    }

}

internal sealed record RelationalPhysicalQueryPredicate(
    string FromAndWhere,
    IReadOnlyList<(string Name, object? Value)> Parameters,
    IReadOnlyList<string> PredicateFieldIdentifiers);

internal sealed record RelationalPhysicalQueryCommand(
    string CommandText,
    IReadOnlyList<(string Name, object? Value)> Parameters,
    IReadOnlyList<string> PredicateFieldIdentifiers);
