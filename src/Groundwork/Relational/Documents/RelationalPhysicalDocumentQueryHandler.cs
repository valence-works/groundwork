using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
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
    public bool SupportsKeysetPaging => true;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => true;

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
    {
        ValidateDocumentIdentityValues(query.Clauses);
        var route = store.GetRoute(query.DocumentKind);
        var scope = store.ResolveQueryScope(query.DocumentKind);
        DocumentQueryContinuationCodec.ValidateScope(plan, scope);
        var continuation = query.Continuation is null
            ? null
            : DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            await ThrowIfLinkedIdentityCollisionAsync(connection, BuildCollisionCheckCommand(query, plan, route, scope), route, ct);
            var total = await CountCoreAsync(connection, BuildCountCommand(query, plan, route, scope), ct);
            if (total == 0 || query.Take == 0)
                return new DocumentQueryResult([], total);

            var rendered = BuildQueryCommand(query, plan, route, scope, continuation);
            await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, rendered.CommandText);
            AddParameters(command, rendered.Parameters);
            var rows = new List<RelationalPhysicalQueryRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var envelope = store.ReadPhysicalEnvelope(reader).Envelope;
                var boundary = plan.PagingSupport == QueryPagingSupport.Cursor
                    ? ReadContinuationValues(
                        reader,
                        query,
                        plan,
                        RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route).Count)
                    : [];
                rows.Add(new RelationalPhysicalQueryRow(envelope, boundary));
            }

            var hasMore = query.Take is { } take && take < int.MaxValue && rows.Count > take;
            if (hasMore)
                rows.RemoveAt(rows.Count - 1);
            var next = hasMore && rows.Count != 0
                ? DocumentQueryContinuationCodec.Encode(query, plan, scope, rows[^1].Boundary)
                : null;
            return new DocumentQueryResult(rows.Select(row => row.Envelope).ToArray(), total, next);
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
        var scope = store.ResolveQueryScope(query.DocumentKind);
        var continuation = query.Continuation is null
            ? null
            : DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope);
        return BuildQueryCommand(query, plan, route, scope, continuation);
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
        var route = store.GetRoute(query.DocumentKind);
        var scope = store.ResolveQueryScope(query.DocumentKind);
        DocumentQueryContinuationCodec.ValidateScope(plan, scope);
        var continuation = query.Continuation is null
            ? null
            : DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope);
        return store.ExecutePhysicalQueryAsync(async (connection, ct) =>
        {
            var commands = new List<PhysicalDocumentQueryCommandExplanation>();
            foreach (var explained in BuildExplainCommands(query, plan, route, scope, continuation))
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
                    explained.Command.PredicateFieldIdentifiers,
                    explained.Command.ProviderAppliedMaximumRows,
                    explained.Command.ProviderAppliedOrder));
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
        DocumentScopeSelection scope,
        IReadOnlyList<DocumentQueryContinuationValue>? continuation)
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
                        BuildQueryCommand(
                            query,
                            plan,
                            route,
                            scope,
                            continuation)));
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
        if (query.LatestPerKeyPath is not null)
            return RenderLatestPerKeyPage(query, plan, built, route, PageReadLimit(query, plan), query.Skip ?? 0);

        var parameters = built.Parameters
            .Concat(new[]
            {
                ("take", (object?)PageReadLimit(query, plan)),
                ("skip", (object?)(plan.PagingSupport == QueryPagingSupport.Cursor ? 0 : query.Skip ?? 0))
            })
            .ToArray();
        var selection = EnvelopeSelection(route);
        if (plan.PagingSupport == QueryPagingSupport.Cursor)
        {
            selection += ", " + string.Join(
                ", ",
                DocumentQueryOrderResolver.Resolve(query, plan).Select(order => Field(order.Field)));
        }
        return new RelationalPhysicalQueryCommand(
            store.ApplyOffsetPage(
                $"SELECT {selection} {built.FromAndWhere} {OrderBy(query, plan)}",
                store.P("take"),
                store.P("skip")),
            parameters,
            built.PredicateFieldIdentifiers,
            PageReadLimit(query, plan),
            ProviderAppliedOrder(query, plan));
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
            fixedScope: scope,
            continuation: query.Continuation is null
                ? null
                : DocumentQueryContinuationCodec.Decode(query.Continuation, query, plan, scope));

    private RelationalPhysicalQueryCommand BuildCountCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope) =>
        query.LatestPerKeyPath is null
            ? RenderCount(Build(query, plan, route, requiresPrimaryLookup: false, fixedScope: scope))
            : RenderLatestPerKeyCount(
                query,
                plan,
                Build(query, plan, route, requiresPrimaryLookup: false, fixedScope: scope));

    private RelationalPhysicalQueryCommand BuildQueryCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope,
        IReadOnlyList<DocumentQueryContinuationValue>? continuation) =>
        RenderQuery(
            query,
            plan,
            Build(
                query,
                plan,
                route,
                requiresPrimaryLookup: true,
                fixedScope: scope,
                continuation: continuation),
            route);

    private RelationalPhysicalQueryCommand BuildFirstCommand(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        DocumentScopeSelection scope)
    {
        var built = Build(query, plan, route, requiresPrimaryLookup: true, fixedScope: scope);
        if (query.LatestPerKeyPath is not null)
            return RenderLatestPerKeyPage(query, plan, built, route, take: 1, skip: query.Skip ?? 0);

        return new RelationalPhysicalQueryCommand(
            store.ApplyFirst($"SELECT {EnvelopeSelection(route)} {built.FromAndWhere} {OrderBy(query, plan)}"),
            built.Parameters,
            built.PredicateFieldIdentifiers,
            1,
            ProviderAppliedOrder(query, plan));
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
            built.PredicateFieldIdentifiers,
            1);
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
            built.PredicateFieldIdentifiers,
            1);
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
        new(
            $"SELECT COUNT(*) {built.FromAndWhere};",
            built.Parameters,
            built.PredicateFieldIdentifiers,
            1);

    private RelationalPhysicalQueryCommand RenderLatestPerKeyCount(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        RelationalPhysicalQueryPredicate built)
    {
        var group = LatestPerKeyField(query, plan);
        return new RelationalPhysicalQueryCommand(
            $"SELECT COUNT(*) FROM (SELECT 1 AS {store.Q("groundwork_latest_group")} {built.FromAndWhere} GROUP BY {Field(group)}) AS {store.Q("groundwork_latest_groups")};",
            built.Parameters,
            built.PredicateFieldIdentifiers
                .Append(group.Identifier)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            1);
    }

    private RelationalPhysicalQueryCommand RenderLatestPerKeyPage(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        RelationalPhysicalQueryPredicate built,
        ExecutableStorageRoute route,
        int take,
        int skip)
    {
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        var group = LatestPerKeyField(query, plan);
        var envelopeColumns = RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route);
        var envelopeSelection = envelopeColumns
            .Select((column, index) =>
                $"p.{store.Q(column)} AS {store.Q(LatestEnvelopeAlias(index))}")
            .ToArray();
        var orderSelection = order
            .Select((item, index) =>
                $"{Field(item.Field)} AS {store.Q(LatestOrderAlias(index))}")
            .ToArray();
        var winnerOrder = string.Join(", ", order.Select(item =>
            store.OrderPhysicalQueryExpression(Field(item.Field), item.Direction)));
        var pageOrder = string.Join(", ", order.Select((item, index) =>
            store.OrderPhysicalQueryExpression(store.Q(LatestOrderAlias(index)), item.Direction)));
        var rows = store.Q("groundwork_latest_rows");
        var rank = store.Q("groundwork_latest_rank");
        var select = $"""
            SELECT {string.Join(", ", envelopeColumns.Select((_, index) => store.Q(LatestEnvelopeAlias(index))))}
            FROM (
                SELECT {string.Join(", ", envelopeSelection.Concat(orderSelection))},
                       ROW_NUMBER() OVER (PARTITION BY {Field(group)} ORDER BY {winnerOrder}) AS {rank}
                {built.FromAndWhere}
            ) AS {rows}
            WHERE {rank} = 1
            ORDER BY {pageOrder}
            """;
        var parameters = built.Parameters
            .Concat(new[]
            {
                ("take", (object?)take),
                ("skip", (object?)skip)
            })
            .ToArray();
        return new RelationalPhysicalQueryCommand(
            store.ApplyOffsetPage(select, store.P("take"), store.P("skip")),
            parameters,
            built.PredicateFieldIdentifiers
                .Concat(order.Select(item => item.Field.Identifier))
                .Append(group.Identifier)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            take,
            ProviderAppliedOrder(order));
    }

    private static PhysicalQueryField LatestPerKeyField(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var path = query.LatestPerKeyPath
                   ?? throw new InvalidOperationException("Latest-per-key execution requires a grouping path.");
        return plan.Order.Single(order => !order.IsIdentityTieBreak && order.Path == path).Field;
    }

    private static string LatestEnvelopeAlias(int index) => $"groundwork_envelope_{index}";
    private static string LatestOrderAlias(int index) => $"groundwork_order_{index}";

    private static IReadOnlyList<PhysicalDocumentQueryCommandOrder> ProviderAppliedOrder(
        DocumentQuery query,
        PhysicalQueryPlan plan) =>
        ProviderAppliedOrder(DocumentQueryOrderResolver.Resolve(query, plan));

    private static IReadOnlyList<PhysicalDocumentQueryCommandOrder> ProviderAppliedOrder(
        IEnumerable<PhysicalQueryOrder> order) =>
        order.Select(item => new PhysicalDocumentQueryCommandOrder(
                item.Field.Identifier,
                item.Direction,
                item.IsIdentityTieBreak))
            .ToArray();

    private RelationalPhysicalQueryPredicate Build(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        ExecutableStorageRoute route,
        bool requiresPrimaryLookup,
        DocumentScopeSelection? fixedScope = null,
        bool detectIdentityCollision = false,
        IReadOnlyList<DocumentQueryContinuationValue>? continuation = null)
    {
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
        if (continuation is not null)
        {
            predicates.Add(ContinuationPredicate(
                query,
                plan,
                continuation,
                parameters,
                predicateFieldIdentifiers,
                ref parameterIndex));
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
            QueryComparisonOperator.Contains or QueryComparisonOperator.NotContains => ContainsPattern.Build((string)value),
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
            QueryComparisonOperator.NotContains => $"({field} IS NULL OR NOT ({store.Contains(field, parameter)}))",
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

    private string OrderBy(DocumentQuery query, PhysicalQueryPlan plan)
    {
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        return order.Count == 0
            ? string.Empty
            : "ORDER BY " + string.Join(", ", order.Select(item =>
                store.OrderPhysicalQueryExpression(Field(item.Field), item.Direction)));
    }

    private string EnvelopeSelection(ExecutableStorageRoute route) => string.Join(", ",
        RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route)
            .Select(column => $"p.{store.Q(column)}"));

    private void AddParameters(DbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
    }

    private static int PageReadLimit(DocumentQuery query, PhysicalQueryPlan plan) =>
        plan.PagingSupport == QueryPagingSupport.Cursor &&
        query.Take is { } take &&
        take < int.MaxValue
            ? take + 1
            : query.Take ?? int.MaxValue;

    private string ContinuationPredicate(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        IReadOnlyList<DocumentQueryContinuationValue> values,
        List<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        var alternatives = new List<string>();
        for (var boundaryIndex = 0; boundaryIndex < order.Count; boundaryIndex++)
        {
            var conjunction = new List<string>();
            for (var prefixIndex = 0; prefixIndex < boundaryIndex; prefixIndex++)
            {
                conjunction.Add(ContinuationEquality(
                    order[prefixIndex],
                    values[prefixIndex],
                    parameters,
                    predicateFieldIdentifiers,
                    ref parameterIndex));
            }
            conjunction.Add(ContinuationAfter(
                order[boundaryIndex],
                values[boundaryIndex],
                parameters,
                predicateFieldIdentifiers,
                ref parameterIndex));
            alternatives.Add($"({string.Join(" AND ", conjunction)})");
        }
        return $"({string.Join(" OR ", alternatives)})";
    }

    private string ContinuationEquality(
        PhysicalQueryOrder order,
        DocumentQueryContinuationValue value,
        ICollection<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        predicateFieldIdentifiers.Add(order.Field.Identifier);
        var field = Field(order.Field);
        if (value.ScalarKind == DocumentQueryContinuationScalarKind.Null)
            return $"{field} IS NULL";
        var parameter = AddContinuationParameter(order.Field, value, parameters, ref parameterIndex);
        return $"{field} = {parameter}";
    }

    private string ContinuationAfter(
        PhysicalQueryOrder order,
        DocumentQueryContinuationValue value,
        ICollection<(string Name, object? Value)> parameters,
        ISet<string> predicateFieldIdentifiers,
        ref int parameterIndex)
    {
        predicateFieldIdentifiers.Add(order.Field.Identifier);
        var field = Field(order.Field);
        if (value.ScalarKind == DocumentQueryContinuationScalarKind.Null)
            return order.Direction == PhysicalSortDirection.Ascending ? $"{field} IS NOT NULL" : "0 = 1";
        var parameter = AddContinuationParameter(order.Field, value, parameters, ref parameterIndex);
        return order.Direction == PhysicalSortDirection.Ascending
            ? $"{field} > {parameter}"
            : $"({field} < {parameter} OR {field} IS NULL)";
    }

    private string AddContinuationParameter(
        PhysicalQueryField field,
        DocumentQueryContinuationValue value,
        ICollection<(string Name, object? Value)> parameters,
        ref int parameterIndex)
    {
        var name = $"c{parameterIndex++}";
        object? parameterValue = value.ScalarKind switch
        {
            DocumentQueryContinuationScalarKind.String => value.Value!,
            DocumentQueryContinuationScalarKind.Int64 => long.Parse(value.Value!, CultureInfo.InvariantCulture),
            DocumentQueryContinuationScalarKind.Decimal => decimal.Parse(value.Value!, CultureInfo.InvariantCulture),
            DocumentQueryContinuationScalarKind.Double => double.Parse(value.Value!, CultureInfo.InvariantCulture),
            DocumentQueryContinuationScalarKind.Boolean => bool.Parse(value.Value!),
            DocumentQueryContinuationScalarKind.DateTimeOffset => DateTimeOffset.Parse(
                value.Value!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            DocumentQueryContinuationScalarKind.Binary => Convert.FromBase64String(value.Value!),
            DocumentQueryContinuationScalarKind.Null => null,
            _ => throw new InvalidDocumentQueryContinuationException(
                "The document-query continuation contains an unsupported physical value.")
        };
        if (IsDocumentIdentityField(field) && parameterValue is string identityValue)
            parameterValue = store.ConvertDocumentIdentityQueryValue(field, identityValue);
        parameters.Add((name, parameterValue));
        return store.NormalizeQueryExpression(store.P(name), field.Source, field.ValueKind);
    }

    private IReadOnlyList<DocumentQueryContinuationValue> ReadContinuationValues(
        DbDataReader reader,
        DocumentQuery query,
        PhysicalQueryPlan plan,
        int startOrdinal) =>
        DocumentQueryOrderResolver.Resolve(query, plan)
            .Select((order, index) => ReadContinuationValue(reader, startOrdinal + index, order.Field))
            .ToArray();

    private DocumentQueryContinuationValue ReadContinuationValue(
        DbDataReader reader,
        int ordinal,
        PhysicalQueryField field)
    {
        if (reader.IsDBNull(ordinal))
            return new(field.ValueKind, DocumentQueryContinuationScalarKind.Null, null);
        if (IsDocumentIdentityField(field))
        {
            var identityValue = field.Path == PhysicalDocumentIdentityFieldPaths.Lookup
                ? store.ReadDocumentIdentityLookup(reader, ordinal)
                : store.ReadDocumentIdentityComparison(reader, ordinal);
            return new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.String,
                identityValue);
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => new(field.ValueKind, DocumentQueryContinuationScalarKind.String, text),
            byte[] binary => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Binary,
                Convert.ToBase64String(binary)),
            bool boolean => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Boolean,
                boolean.ToString(CultureInfo.InvariantCulture)),
            decimal number => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Decimal,
                number.ToString(CultureInfo.InvariantCulture)),
            double number => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Double,
                number.ToString("R", CultureInfo.InvariantCulture)),
            float number => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Double,
                number.ToString("R", CultureInfo.InvariantCulture)),
            sbyte or byte or short or ushort or int or uint or long => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.Int64,
                Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            DateTimeOffset instant => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.DateTimeOffset,
                instant.ToString("O", CultureInfo.InvariantCulture)),
            DateTime instant => new(
                field.ValueKind,
                DocumentQueryContinuationScalarKind.DateTimeOffset,
                new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc))
                    .ToString("O", CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException(
                $"Physical query order field '{field.Path}' returned unsupported value type '{value.GetType().FullName}'.")
        };
    }

}

internal sealed record RelationalPhysicalQueryRow(
    DocumentEnvelope Envelope,
    IReadOnlyList<DocumentQueryContinuationValue> Boundary);

internal sealed record RelationalPhysicalQueryPredicate(
    string FromAndWhere,
    IReadOnlyList<(string Name, object? Value)> Parameters,
    IReadOnlyList<string> PredicateFieldIdentifiers);

internal sealed record RelationalPhysicalQueryCommand(
    string CommandText,
    IReadOnlyList<(string Name, object? Value)> Parameters,
    IReadOnlyList<string> PredicateFieldIdentifiers,
    int? ProviderAppliedMaximumRows = null,
    IReadOnlyList<PhysicalDocumentQueryCommandOrder>? ProviderAppliedOrder = null);
