using System.Collections.Frozen;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

namespace Groundwork.Documents.Store;

/// <summary>
/// Immutable evidence that one physical handler is bound to the exact provider route and query
/// fields it can execute. A source-wide capability claim is not sufficient certification.
/// </summary>
public sealed class PhysicalQueryHandlerCertification : IEquatable<PhysicalQueryHandlerCertification>
{
    public PhysicalQueryHandlerCertification(
        ProviderIdentity provider,
        StorageUnitIdentity storageUnit,
        string queryIdentity,
        string logicalIndexIdentity,
        IReadOnlyList<string> logicalIndexPaths,
        PhysicalQueryAccessKind accessKind,
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName lookupObject,
        ProviderPhysicalObjectName primaryObject,
        ProviderPhysicalObjectName? indexName,
        IReadOnlyDictionary<string, string> fieldIdentifiers,
        string routeFingerprint)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        StorageUnit = storageUnit;
        QueryIdentity = string.IsNullOrWhiteSpace(queryIdentity)
            ? throw new ArgumentException("A bounded-query identity is required.", nameof(queryIdentity))
            : queryIdentity;
        LogicalIndexIdentity = string.IsNullOrWhiteSpace(logicalIndexIdentity)
            ? throw new ArgumentException("A logical-index identity is required.", nameof(logicalIndexIdentity))
            : logicalIndexIdentity;
        LogicalIndexPaths = Array.AsReadOnly(
            logicalIndexPaths?.ToArray() ?? throw new ArgumentNullException(nameof(logicalIndexPaths)));
        if (LogicalIndexPaths.Count == 0 || LogicalIndexPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty logical-index path is required.", nameof(logicalIndexPaths));
        AccessKind = accessKind;
        Target = target;
        LookupObject = lookupObject;
        PrimaryObject = primaryObject;
        IndexName = indexName;
        FieldIdentifiers = (fieldIdentifiers ?? throw new ArgumentNullException(nameof(fieldIdentifiers)))
            .ToFrozenDictionary(StringComparer.Ordinal);
        if (FieldIdentifiers.Any(field =>
                string.IsNullOrWhiteSpace(field.Key) || string.IsNullOrWhiteSpace(field.Value)))
        {
            throw new ArgumentException(
                "Certified field mappings require non-empty stable paths and provider identifiers.",
                nameof(fieldIdentifiers));
        }
        RouteFingerprint = string.IsNullOrWhiteSpace(routeFingerprint)
            ? throw new ArgumentException("An executable-route fingerprint is required.", nameof(routeFingerprint))
            : routeFingerprint;
    }

    public ProviderIdentity Provider { get; }
    public StorageUnitIdentity StorageUnit { get; }
    public string QueryIdentity { get; }
    public string LogicalIndexIdentity { get; }
    public IReadOnlyList<string> LogicalIndexPaths { get; }
    public PhysicalQueryAccessKind AccessKind { get; }
    public ExecutableStorageObjectRole Target { get; }
    public ProviderPhysicalObjectName LookupObject { get; }
    public ProviderPhysicalObjectName PrimaryObject { get; }
    public ProviderPhysicalObjectName? IndexName { get; }
    public IReadOnlyDictionary<string, string> FieldIdentifiers { get; }
    public string RouteFingerprint { get; }

    internal bool Certifies(PhysicalQueryPlan plan)
    {
        if (Provider != plan.Provider ||
            StorageUnit != plan.StorageUnit ||
            QueryIdentity != plan.QueryIdentity ||
            LogicalIndexIdentity != plan.LogicalIndexIdentity ||
            !LogicalIndexPaths.SequenceEqual(plan.LogicalIndexPaths) ||
            AccessKind != plan.AccessKind ||
            Target != plan.Scope.Field.Target ||
            LookupObject != plan.LookupObject ||
            PrimaryObject != plan.PrimaryObject ||
            IndexName != plan.IndexName ||
            RouteFingerprint != plan.RouteFingerprint)
        {
            return false;
        }

        return PlanFields(plan).All(field =>
            field.Target == Target &&
            field.ObjectName == LookupObject &&
            FieldIdentifiers.TryGetValue(field.Path, out var identifier) &&
            identifier == field.Identifier);
    }

    public bool Equals(PhysicalQueryHandlerCertification? other) =>
        other is not null &&
        Provider == other.Provider &&
        StorageUnit == other.StorageUnit &&
        QueryIdentity == other.QueryIdentity &&
        LogicalIndexIdentity == other.LogicalIndexIdentity &&
        LogicalIndexPaths.SequenceEqual(other.LogicalIndexPaths) &&
        AccessKind == other.AccessKind &&
        Target == other.Target &&
        LookupObject == other.LookupObject &&
        PrimaryObject == other.PrimaryObject &&
        IndexName == other.IndexName &&
        RouteFingerprint == other.RouteFingerprint &&
        FieldIdentifiers.Count == other.FieldIdentifiers.Count &&
        FieldIdentifiers.All(field =>
            other.FieldIdentifiers.TryGetValue(field.Key, out var identifier) && identifier == field.Value);

    public override bool Equals(object? obj) => Equals(obj as PhysicalQueryHandlerCertification);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Provider);
        hash.Add(StorageUnit);
        hash.Add(QueryIdentity, StringComparer.Ordinal);
        hash.Add(LogicalIndexIdentity, StringComparer.Ordinal);
        foreach (var path in LogicalIndexPaths)
            hash.Add(path, StringComparer.Ordinal);
        hash.Add(AccessKind);
        hash.Add(Target);
        hash.Add(LookupObject);
        hash.Add(PrimaryObject);
        hash.Add(IndexName);
        foreach (var field in FieldIdentifiers.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            hash.Add(field.Key, StringComparer.Ordinal);
            hash.Add(field.Value, StringComparer.Ordinal);
        }
        hash.Add(RouteFingerprint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static IEnumerable<PhysicalQueryField> PlanFields(PhysicalQueryPlan plan) =>
        new[] { plan.Scope.Field, plan.Discriminator }
            .Concat(plan.Predicates.Select(predicate => predicate.Field))
            .Concat(plan.Order.Select(order => order.Field));
}

/// <summary>The runtime document-query seam whose requests are always resolved to compiled plans.</summary>
public interface IBoundedDocumentStore
{
    Task<DocumentQueryResult> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<long> CountAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(DocumentQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// One executable provider handler registered under the same identity used by query planning.
/// Provider packages own SQL/BSON generation; this interface keeps those types outside Core.
/// </summary>
public interface IPhysicalDocumentQueryHandler
{
    string Identity { get; }
    PhysicalQuerySourceKind Source { get; }
    IReadOnlySet<PortableQueryOperation> SupportedOperations { get; }
    IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }
    IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; }
    bool SupportsCompoundPredicates { get; }
    bool SupportsDisjunction { get; }
    bool SupportsOffsetPaging { get; }
    bool SupportsKeysetPaging { get; }
    bool SupportsCount { get; }
    bool SupportsAny { get; }
    bool SupportsFirst { get; }
    bool SupportsLatestPerKey { get; }
    Task<DocumentQueryResult> QueryAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken);
    Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken);
    Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken);
    Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Compiles plans during construction, binds runtime query identity to those plans, and dispatches
/// only to registered executable handlers. Invalid scale-bearing declarations fail before a store
/// capable of serving traffic is returned.
/// </summary>
public sealed class PhysicalQueryDocumentStore : IBoundedDocumentStore
{
    private readonly IReadOnlyDictionary<string, PhysicalQueryPlan> plans;
    private readonly IReadOnlyDictionary<string, IPhysicalDocumentQueryHandler> handlers;

    public PhysicalQueryDocumentStore(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities capabilities,
        IReadOnlyList<IPhysicalDocumentQueryHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(handlers);

        var byIdentity = handlers
            .GroupBy(handler => handler.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (byIdentity.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Value.Length != 1))
            throw new ArgumentException("Physical query handler identities must be non-empty and unique.", nameof(handlers));

        foreach (var registration in capabilities.HandlerIdentities)
        {
            if (!byIdentity.TryGetValue(registration.Value, out var matches) ||
                matches[0].Source != registration.Key ||
                !SupportsProfile(matches[0], capabilities))
            {
                throw new ArgumentException(
                    $"Capability source '{registration.Key}' must reference one registered executable handler '{registration.Value}'.",
                    nameof(handlers));
            }
        }

        var compilation = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        foreach (var plan in compilation.Plans)
        {
            var handler = byIdentity[plan.HandlerIdentity][0];
            if (handler.Certifications is null || !handler.Certifications.Any(certification => certification.Certifies(plan)))
            {
                throw new ArgumentException(
                    $"Physical query handler '{handler.Identity}' does not certify provider route '{plan.RouteFingerprint}' " +
                    $"for bounded query '{plan.QueryIdentity}' and physical index '{plan.IndexName?.Identifier ?? "<none>"}'.",
                    nameof(handlers));
            }
        }

        plans = compilation.Plans.ToDictionary(plan => plan.QueryIdentity, StringComparer.Ordinal);
        this.handlers = byIdentity.ToDictionary(group => group.Key, group => group.Value[0], StringComparer.Ordinal);
    }

    private static bool SupportsProfile(
        IPhysicalDocumentQueryHandler handler,
        PhysicalQueryPlannerCapabilities capabilities) =>
        capabilities.SupportedOperations.IsSubsetOf(handler.SupportedOperations) &&
        (!capabilities.SupportsCompoundPredicates || handler.SupportsCompoundPredicates) &&
        (!capabilities.SupportsDisjunction || handler.SupportsDisjunction) &&
        (!capabilities.SupportsOffsetPaging || handler.SupportsOffsetPaging) &&
        (!capabilities.SupportsKeysetPaging || handler.SupportsKeysetPaging) &&
        (!capabilities.SupportsCount || handler.SupportsCount) &&
        (!capabilities.SupportsAny || handler.SupportsAny) &&
        (!capabilities.SupportsFirst || handler.SupportsFirst) &&
        (!capabilities.SupportsLatestPerKey || handler.SupportsLatestPerKey) &&
        (handler.Source != PhysicalQuerySourceKind.NativeDocumentFields ||
         capabilities.NativeFieldIdentifiers.All(field =>
             handler.NativeFieldIdentifiers.TryGetValue(field.Key, out var identifier) && identifier == field.Value));

    public Task<DocumentQueryResult> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        var (plan, handler) = Resolve(query, BoundedQueryResultOperation.Documents);
        return handler.QueryAsync(query, plan, cancellationToken);
    }

    public Task<long> CountAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        var (plan, handler) = Resolve(query, BoundedQueryResultOperation.Count);
        return handler.CountAsync(query, plan, cancellationToken);
    }

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        var (plan, handler) = Resolve(query, BoundedQueryResultOperation.First);
        return handler.FirstOrDefaultAsync(query, plan, cancellationToken);
    }

    public Task<bool> AnyAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        var (plan, handler) = Resolve(query, BoundedQueryResultOperation.Any);
        return handler.AnyAsync(query, plan, cancellationToken);
    }

    private (PhysicalQueryPlan Plan, IPhysicalDocumentQueryHandler Handler) Resolve(
        DocumentQuery query,
        BoundedQueryResultOperation operation)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!plans.TryGetValue(query.QueryIdentity, out var plan) ||
            plan.StorageUnit.Value != query.DocumentKind)
        {
            throw new InvalidOperationException(
                $"Document query '{query.QueryIdentity}' is not bound to storage unit '{query.DocumentKind}'.");
        }
        if (query.ResultOperation != operation || !plan.ResultOperations.Contains(operation))
            throw new InvalidOperationException($"Document query '{query.QueryIdentity}' does not declare result operation '{operation}'.");

        ValidateRuntimeShape(query, plan);
        return (plan, handlers[plan.HandlerIdentity]);
    }

    private static void ValidateRuntimeShape(DocumentQuery query, PhysicalQueryPlan plan)
    {
        foreach (var clause in query.Clauses)
        {
            if (clause.Comparisons.Count > 1 && !plan.SupportsDisjunction)
                throw new InvalidOperationException($"Document query '{query.QueryIdentity}' does not declare disjunction.");

            foreach (var comparison in clause.Comparisons)
            {
                var operation = ToPortableOperation(comparison.Operator);
                if (plan.Predicates.All(predicate =>
                        predicate.Path != comparison.Path || !predicate.Operations.Contains(operation)))
                    throw new InvalidOperationException($"Operation '{operation}' is not bound to query '{query.QueryIdentity}'.");
            }
        }

        var plannedOrder = plan.Order.Where(order => !order.IsIdentityTieBreak).ToArray();
        if (query.Order.Count > plannedOrder.Length || query.Order.Where((order, index) =>
                order.Path != plannedOrder[index].Path || order.Direction != plannedOrder[index].Direction).Any())
            throw new InvalidOperationException($"Compound ordering exceeds query plan '{query.QueryIdentity}'.");
        if (query.Skip is not null && plan.PagingSupport != QueryPagingSupport.Offset)
            throw new InvalidOperationException($"Offset paging is not bound to query '{query.QueryIdentity}'.");
        if (query.Continuation is not null && plan.PagingSupport != QueryPagingSupport.Cursor)
            throw new InvalidOperationException($"Keyset paging is not bound to query '{query.QueryIdentity}'.");
        if (query.LatestPerKeyPath is not null &&
            (plan.LatestPerKeyPath is null || query.LatestPerKeyPath != plan.LatestPerKeyPath))
        {
            throw new InvalidOperationException($"Latest-per-key selection is not bound to query '{query.QueryIdentity}'.");
        }

        foreach (var path in plan.RequiredEqualityPrefixPaths)
        {
            var occurrences = query.Clauses
                .SelectMany(clause => clause.Comparisons.Select(comparison => (Clause: clause, Comparison: comparison)))
                .Where(item => item.Comparison.Path == path)
                .ToArray();
            if (occurrences.Length != 1 ||
                occurrences[0].Clause.Comparisons.Count != 1 ||
                occurrences[0].Comparison.Operator != QueryComparisonOperator.Equal)
            {
                throw new InvalidOperationException(
                    $"Ordered suffix query '{query.QueryIdentity}' requires exactly one standalone equality comparison for prefix path '{path}'.");
            }
        }
    }

    private static PortableQueryOperation ToPortableOperation(QueryComparisonOperator operation) => operation switch
    {
        QueryComparisonOperator.Equal => PortableQueryOperation.Equal,
        QueryComparisonOperator.In => PortableQueryOperation.In,
        QueryComparisonOperator.Contains => PortableQueryOperation.Contains,
        QueryComparisonOperator.NotEqual => PortableQueryOperation.NotEqual,
        QueryComparisonOperator.StartsWith => PortableQueryOperation.StartsWith,
        QueryComparisonOperator.GreaterThan => PortableQueryOperation.GreaterThan,
        QueryComparisonOperator.GreaterThanOrEqual => PortableQueryOperation.GreaterThanOrEqual,
        QueryComparisonOperator.LessThan => PortableQueryOperation.LessThan,
        QueryComparisonOperator.LessThanOrEqual => PortableQueryOperation.LessThanOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };
}

/// <summary>
/// Explicit pre-plan compatibility handler for the old portable provider surface. It deliberately
/// supports only ordinary canonical-JSON queries that the legacy contract can represent.
/// </summary>
public sealed class LegacyPortableDocumentQueryHandler : IPhysicalDocumentQueryHandler
{
    private readonly IDocumentStore store;

    public LegacyPortableDocumentQueryHandler(
        string identity,
        IDocumentStore store,
        IReadOnlyList<PhysicalQueryHandlerCertification> certifications)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("Handler identity is required.", nameof(identity))
            : identity;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Certifications = Array.AsReadOnly(
            certifications?.ToArray() ?? throw new ArgumentNullException(nameof(certifications)));
        if (Certifications.Any(certification => certification.LogicalIndexPaths.Count != 1))
        {
            throw new ArgumentException(
                "The legacy portable handler can certify only single-field logical indexes.",
                nameof(certifications));
        }
    }

    public string Identity { get; }
    public PhysicalQuerySourceKind Source => PhysicalQuerySourceKind.PrimaryCanonicalJson;
    public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
        new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In,
            PortableQueryOperation.Contains
        }.ToFrozenSet();
    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
        FrozenDictionary<string, string>.Empty;
    public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; }
    public bool SupportsCompoundPredicates => false;
    public bool SupportsDisjunction => true;
    public bool SupportsOffsetPaging => true;
    public bool SupportsKeysetPaging => false;
    public bool SupportsCount => true;
    public bool SupportsAny => true;
    public bool SupportsFirst => true;
    public bool SupportsLatestPerKey => false;

    public Task<DocumentQueryResult> QueryAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
#pragma warning disable GW0004
        store.QueryAsync(ToLegacy(query, plan), cancellationToken);
#pragma warning restore GW0004

    public async Task<long> CountAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
        (await QueryAsync(query, plan, cancellationToken)).TotalCount;

    public Task<DocumentEnvelope?> FirstOrDefaultAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
#pragma warning disable GW0004
        store.FirstOrDefaultAsync(ToLegacy(query, plan), cancellationToken);
#pragma warning restore GW0004

    public Task<bool> AnyAsync(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        CancellationToken cancellationToken) =>
#pragma warning disable GW0004
        store.AnyAsync(ToLegacy(query, plan), cancellationToken);
#pragma warning restore GW0004

#pragma warning disable GW0004
    private static PortableDocumentQuery ToLegacy(DocumentQuery query, PhysicalQueryPlan plan)
#pragma warning restore GW0004
    {
        if (plan.IsScaleBearing)
            throw new NotSupportedException("The legacy portable handler cannot execute scale-bearing physical plans.");
        return query.ToPortableDocumentQuery(plan);
    }
}
