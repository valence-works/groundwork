using System.Collections.Frozen;
using Groundwork.Core.Indexing;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Compiles bounded query declarations against immutable executable storage routes. Compilation
/// never produces client-evaluation plans and is atomic across the unit's declarations.
/// </summary>
public static class PhysicalQueryPlanCompiler
{
    public static PhysicalQueryPlanCompilationResult Compile(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(capabilities);

        var diagnostics = new List<GroundworkDiagnostic>();
        var plans = new List<PhysicalQueryPlan>();
        var indexes = storage.LogicalIndexes
            .GroupBy(index => index.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var query in storage.BoundedQueries.OrderBy(query => query.Identity, StringComparer.Ordinal))
        {
            var target = $"physicalQueries.{route.StorageUnit.Value}.{query.Identity}";
            if (!indexes.TryGetValue(query.IndexIdentity, out var matches) || matches.Length != 1)
            {
                diagnostics.Add(Error(
                    "GW-QUERY-001",
                    $"Bounded query '{query.Identity}' must reference exactly one logical index '{query.IndexIdentity}'.",
                    target));
                continue;
            }

            var initialErrors = diagnostics.Count(diagnostic => diagnostic.IsError);
            var plan = CompileOne(route, matches[0], query, capabilities, target, diagnostics);
            if (plan is not null && diagnostics.Count(diagnostic => diagnostic.IsError) == initialErrors)
                plans.Add(plan);
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError)
            ? new([], diagnostics)
            : new(plans, diagnostics);
    }

    private static PhysicalQueryPlan? CompileOne(
        ExecutableStorageRoute route,
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query,
        PhysicalQueryPlannerCapabilities capabilities,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var predicateDeclarations = ResolvePredicates(logicalIndex, query, target, diagnostics);
        var hasMixedIdentityDemand = HasMixedIdentityDemand(predicateDeclarations);
        if (query.LatestPerKeyPath is not null &&
            logicalIndex.Fields.All(field => field.Path != query.LatestPerKeyPath))
        {
            diagnostics.Add(Error(
                "GW-QUERY-002",
                $"Latest-per-key path '{query.LatestPerKeyPath}' is not part of logical index '{logicalIndex.Identity}'.",
                target));
        }
        ValidateOperations(predicateDeclarations, query, capabilities, target, diagnostics);
        ValidateIdentityOperations(predicateDeclarations, query, hasMixedIdentityDemand, target, diagnostics);
        ValidateShape(query, predicateDeclarations.Count, capabilities, target, diagnostics);
        ValidateEnvelopeKinds(logicalIndex, target, diagnostics);
        if (query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing && hasMixedIdentityDemand)
            return null;

        var physicalIndex = route.Indexes.SingleOrDefault(index => index.Identity == logicalIndex.Identity);
        var certifiedPhysicalIndex = hasMixedIdentityDemand ? null : physicalIndex;
        var selectedSource = SelectSource(route, logicalIndex, physicalIndex, query, capabilities);
        var hasBoundScalePath = route.CandidateQueryPaths.Any(path =>
            path.Kind == ExecutableQueryPathKind.PhysicalIndex &&
            path.Identity == logicalIndex.Identity &&
            path.IsScaleBearing &&
            path.QueryIdentities.Contains(query.Identity, StringComparer.Ordinal));
        if (query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing &&
            (physicalIndex is null ||
             selectedSource is null ||
             !HasIndexedAccess(selectedSource.Value, certifiedPhysicalIndex) ||
             !hasBoundScalePath))
        {
            diagnostics.Add(Error(
                "GW-QUERY-005",
                $"Scale-bearing query '{query.Identity}' has no executable indexed server-side route for provider '{capabilities.Provider}'.",
                target));
            return null;
        }

        if (selectedSource is null)
        {
            diagnostics.Add(Error(
                "GW-QUERY-004",
                $"Provider '{capabilities.Provider}' has no executable server-side source for bounded query '{query.Identity}'.",
                target));
            return null;
        }

        var identityFields = ResolveDocumentIdentityFields(
            route,
            selectedSource.Value,
            capabilities);
        var documentIdentity = identityFields.Binding;
        var predicates = predicateDeclarations
            .Select(predicate => new PhysicalQueryPredicate(
                predicate.Path,
                identityFields.Resolve(
                    predicate.Path,
                    logicalIndex.GetValueKind(predicate.Path)),
                predicate.Operations.ToFrozenSet()))
            .ToArray();
        ValidateExecutableCompatibility(route, predicates, target, diagnostics);

        IReadOnlyList<string> requiredEqualityPrefixPaths = [];
        if (HasIndexedAccess(selectedSource.Value, certifiedPhysicalIndex) &&
            !ValidatePhysicalCompatibility(
                certifiedPhysicalIndex!,
                identityFields,
                predicateDeclarations,
                query,
                target,
                diagnostics,
                out requiredEqualityPrefixPaths))
        {
            return null;
        }

        if (diagnostics.Any(diagnostic => diagnostic.IsError && diagnostic.Target == target))
            return null;

        var access = ToAccessKind(selectedSource.Value);
        var lookupObject = access == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? route.LinkedIndexStorage!.Name
            : route.PrimaryStorage.Name;
        var lookupTarget = access == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? ExecutableStorageObjectRole.LinkedIndexStorage
            : ExecutableStorageObjectRole.PrimaryStorage;
        var scopeColumn = access == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? route.LinkedRelationship!.StorageScope
            : route.ScopeKey.Column;
        var scopeIdentifier = access == PhysicalQueryAccessKind.NativeDocumentFields
            ? capabilities.NativeFieldIdentifiers[PhysicalDocumentFieldPaths.StorageScope]
            : scopeColumn.Identifier;
        var scope = new PhysicalQueryScope(
            new PhysicalQueryField(
                PhysicalDocumentFieldPaths.StorageScope,
                scopeIdentifier,
                access switch
                {
                    PhysicalQueryAccessKind.LinkedIndexThenPrimary => PhysicalQueryFieldSource.LinkedRelationship,
                    PhysicalQueryAccessKind.NativeDocumentFields => PhysicalQueryFieldSource.NativeDocumentField,
                    _ => PhysicalQueryFieldSource.Envelope
                },
                lookupTarget,
                lookupObject,
                IndexValueKind.Keyword),
            route.ScopePolicy,
            IsMandatory: true,
            route.ScopeKey.UsesGlobalSentinel);
        var discriminatorColumn = access == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? route.LinkedRelationship!.DocumentKind
            : route.Discriminator.Column;
        var discriminatorIdentifier = access == PhysicalQueryAccessKind.NativeDocumentFields
            ? capabilities.NativeFieldIdentifiers[PhysicalDocumentFieldPaths.DocumentKind]
            : discriminatorColumn.Identifier;
        var discriminator = new PhysicalQueryField(
            PhysicalDocumentFieldPaths.DocumentKind,
            discriminatorIdentifier,
            access switch
            {
                PhysicalQueryAccessKind.LinkedIndexThenPrimary => PhysicalQueryFieldSource.LinkedRelationship,
                PhysicalQueryAccessKind.NativeDocumentFields => PhysicalQueryFieldSource.NativeDocumentField,
                _ => PhysicalQueryFieldSource.Envelope
            },
            lookupTarget,
            lookupObject,
            IndexValueKind.Keyword);
        var order = ResolveOrder(
            route,
            selectedSource.Value,
            logicalIndex,
            query,
            capabilities,
            identityFields);

        var draft = new PhysicalQueryPlan(
            route.StorageUnit,
            query.Identity,
            logicalIndex.Identity,
            Array.AsReadOnly(logicalIndex.Fields.Select(field => field.Path).ToArray()),
            capabilities.HandlerIdentities[selectedSource.Value],
            capabilities.Provider,
            route.Form,
            access,
            lookupObject,
            route.PrimaryStorage.Name,
            HasIndexedAccess(selectedSource.Value, certifiedPhysicalIndex) ? certifiedPhysicalIndex?.Name : null,
            scope,
            discriminator,
            documentIdentity,
            predicates,
            order,
            requiredEqualityPrefixPaths,
            query.PagingSupport,
            query.ResultOperations,
            query.SupportsDisjunction,
            query.LatestPerKeyPath,
            access == PhysicalQueryAccessKind.LinkedIndexThenPrimary,
            query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing,
            route.Fingerprint,
            string.Empty);
        return draft.WithFingerprint(PhysicalQueryPlanSerializer.CreateFingerprint(draft));
    }

    private static IReadOnlyList<BoundedQueryPredicateField> ResolvePredicates(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var predicates = query.PredicateFields.Count == 0
            ? logicalIndex.Fields.Take(1)
                .Select(field => new BoundedQueryPredicateField(field.Path, query.Operations))
                .ToArray()
            : query.PredicateFields.ToArray();
        var declaredPaths = logicalIndex.Fields.Select(field => field.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var predicate in predicates.Where(predicate => !declaredPaths.Contains(predicate.Path)))
        {
            diagnostics.Add(Error(
                "GW-QUERY-002",
                $"Predicate path '{predicate.Path}' is not part of logical index '{logicalIndex.Identity}'.",
                target));
        }
        if (predicates.Select(predicate => predicate.Path).Distinct(StringComparer.Ordinal).Count() != predicates.Length)
            diagnostics.Add(Error("GW-QUERY-002", "Predicate paths must be unique.", target));
        if (predicates.Any(predicate => predicate.Path == PhysicalDocumentFieldPaths.StorageScope))
            diagnostics.Add(Error("GW-QUERY-002", "Storage scope is injected by the session and cannot be a caller predicate.", target));
        if (predicates.Any(predicate => predicate.Operations.Count == 0))
            diagnostics.Add(Error("GW-QUERY-002", "Every predicate path must declare at least one operation.", target));
        return predicates;
    }

    private static void ValidateOperations(
        IReadOnlyList<BoundedQueryPredicateField> predicates,
        BoundedQueryDeclaration query,
        PhysicalQueryPlannerCapabilities capabilities,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var predicate in predicates)
        {
            var outsideDeclaration = predicate.Operations.Except(query.Operations).ToArray();
            var unsupported = predicate.Operations.Except(capabilities.SupportedOperations).ToArray();
            if (outsideDeclaration.Length != 0)
            {
                diagnostics.Add(Error(
                    "GW-QUERY-002",
                    $"Predicate path '{predicate.Path}' requests undeclared operations: {string.Join(", ", outsideDeclaration)}.",
                    target));
            }
            if (unsupported.Length != 0)
            {
                diagnostics.Add(Error(
                    "GW-QUERY-003",
                    $"Provider '{capabilities.Provider}' cannot execute operations: {string.Join(", ", unsupported)}.",
                    target));
            }
        }
    }

    private static void ValidateShape(
        BoundedQueryDeclaration query,
        int predicateCount,
        PhysicalQueryPlannerCapabilities capabilities,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (predicateCount > 1 && !capabilities.SupportsCompoundPredicates)
            diagnostics.Add(Error("GW-QUERY-003", "Provider cannot execute compound predicates.", target));
        if (query.ResultOperations.Count == 0)
            diagnostics.Add(Error("GW-QUERY-002", "A bounded query must declare at least one result operation.", target));
        if (query.SupportsDisjunction && !capabilities.SupportsDisjunction)
            diagnostics.Add(Error("GW-QUERY-003", "Provider cannot execute declared disjunctions.", target));
        if (query.PagingSupport == QueryPagingSupport.Offset && !capabilities.SupportsOffsetPaging)
            diagnostics.Add(Error("GW-QUERY-007", "Provider cannot execute declared offset paging.", target));
        if (query.PagingSupport == QueryPagingSupport.Cursor && !capabilities.SupportsKeysetPaging)
            diagnostics.Add(Error("GW-QUERY-007", "Provider cannot execute declared keyset paging.", target));
        if (query.LatestPerKeyPath is not null && !capabilities.SupportsLatestPerKey)
            diagnostics.Add(Error("GW-QUERY-008", "Provider cannot execute declared latest-per-key selection.", target));
        if (query.ResultOperations.Contains(BoundedQueryResultOperation.Count) && !capabilities.SupportsCount)
            diagnostics.Add(Error("GW-QUERY-003", "Provider cannot execute declared count results.", target));
        if (query.ResultOperations.Contains(BoundedQueryResultOperation.Any) && !capabilities.SupportsAny)
            diagnostics.Add(Error("GW-QUERY-003", "Provider cannot execute declared any results.", target));
        if (query.ResultOperations.Contains(BoundedQueryResultOperation.First) && !capabilities.SupportsFirst)
            diagnostics.Add(Error("GW-QUERY-003", "Provider cannot execute declared first results.", target));
    }

    private static void ValidateIdentityOperations(
        IReadOnlyList<BoundedQueryPredicateField> predicates,
        BoundedQueryDeclaration query,
        bool hasMixedIdentityDemand,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (predicates.Any(predicate =>
                predicate.Path == PhysicalDocumentFieldPaths.Id &&
                predicate.Operations.Contains(PortableQueryOperation.Contains)))
        {
            diagnostics.Add(Error(
                "GW-QUERY-011",
                "Document identity does not support Contains because no bounded identity projection preserves substring semantics.",
                target));
        }
        if (query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing && hasMixedIdentityDemand)
        {
            diagnostics.Add(Error(
                "GW-QUERY-012",
                "Document identity has mixed exact and ordered demand; no single certified physical index order serves both evidence shapes.",
                target));
        }
    }

    private static bool HasMixedIdentityDemand(IEnumerable<BoundedQueryPredicateField> predicates) =>
        predicates.Any(predicate =>
            predicate.Path == PhysicalDocumentFieldPaths.Id &&
            PhysicalQueryIdentityDemand.Resolve(predicate.Operations) ==
            PhysicalQueryIdentityEvidenceDemand.Mixed);

    private static void ValidateExecutableCompatibility(
        ExecutableStorageRoute route,
        IReadOnlyList<PhysicalQueryPredicate> predicates,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var predicate in predicates)
        {
            var projection = predicate.Field.Source == PhysicalQueryFieldSource.ProjectedColumn
                ? route.ProjectedColumns.Single(column =>
                    column.Target == predicate.Field.Target &&
                    column.Definition.Path == predicate.Path)
                : null;
            if (projection is not null &&
                !PortableQueryOperationCompatibility.Supports(
                    predicate.Field.ValueKind,
                    projection.Definition.Type))
            {
                diagnostics.Add(Error(
                    "GW-QUERY-009",
                    $"Logical value kind '{predicate.Field.ValueKind}' cannot be represented by projected physical type " +
                    $"'{projection.Definition.Type}' on predicate path '{predicate.Path}' without changing query semantics.",
                    target));
                continue;
            }
            foreach (var operation in predicate.Operations)
            {
                var supported = PortableQueryOperationCompatibility.Supports(predicate.Field.ValueKind, operation) &&
                                (projection is null ||
                                 PortableQueryOperationCompatibility.Supports(projection.Definition.Type, operation));
                if (!supported)
                {
                    var typeDescription = projection is null
                        ? $"value kind '{predicate.Field.ValueKind}'"
                        : $"projected physical type '{projection.Definition.Type}' (value kind '{predicate.Field.ValueKind}')";
                    diagnostics.Add(Error(
                        "GW-QUERY-009",
                        $"Operation '{operation}' cannot execute against {typeDescription} on predicate path '{predicate.Path}'.",
                        target));
                }
            }
        }
    }

    private static PhysicalQuerySourceKind? SelectSource(
        ExecutableStorageRoute route,
        LogicalIndexDeclaration logicalIndex,
        ExecutablePhysicalIndexRoute? physicalIndex,
        BoundedQueryDeclaration query,
        PhysicalQueryPlannerCapabilities capabilities)
    {
        var available = new HashSet<PhysicalQuerySourceKind>();
        if (physicalIndex?.Target == ExecutableStorageObjectRole.LinkedIndexStorage &&
            capabilities.HandlerIdentities.ContainsKey(PhysicalQuerySourceKind.LinkedIndex))
            available.Add(PhysicalQuerySourceKind.LinkedIndex);
        if (physicalIndex?.Target == ExecutableStorageObjectRole.PrimaryStorage)
        {
            if (logicalIndex.Fields.All(field => PhysicalDocumentFieldPaths.IsEnvelope(field.Path)) &&
                capabilities.HandlerIdentities.ContainsKey(PhysicalQuerySourceKind.PrimaryEnvelope))
                available.Add(PhysicalQuerySourceKind.PrimaryEnvelope);
            if (logicalIndex.Fields.Any(field => !PhysicalDocumentFieldPaths.IsEnvelope(field.Path)) &&
                logicalIndex.Fields.All(field => PhysicalDocumentFieldPaths.IsEnvelope(field.Path) ||
                    route.ProjectedColumns.Any(column => column.Definition.Path == field.Path)) &&
                capabilities.HandlerIdentities.ContainsKey(PhysicalQuerySourceKind.PrimaryProjectedColumns))
                available.Add(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        }
        if (capabilities.HandlerIdentities.ContainsKey(PhysicalQuerySourceKind.PrimaryCanonicalJson))
            available.Add(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var requiredNativePaths = logicalIndex.Fields.Select(field => field.Path)
            .Concat([
                PhysicalDocumentFieldPaths.Id,
                PhysicalDocumentFieldPaths.StorageScope,
                PhysicalDocumentFieldPaths.DocumentKind
            ])
            .Distinct(StringComparer.Ordinal);
        if (requiredNativePaths.All(capabilities.NativeFieldIdentifiers.ContainsKey) &&
            capabilities.HandlerIdentities.ContainsKey(PhysicalQuerySourceKind.NativeDocumentFields))
        {
            available.Add(PhysicalQuerySourceKind.NativeDocumentFields);
        }

        var candidates = query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing
            ? capabilities.SourcePreference.Where(source => HasIndexedAccess(source, physicalIndex)).ToArray()
            : capabilities.SourcePreference;
        foreach (var source in candidates)
        {
            if (available.Contains(source) &&
                logicalIndex.Fields.All(field => capabilities.Supports(source, logicalIndex.GetValueKind(field))))
            {
                return source;
            }
        }
        return null;
    }

    private static bool ValidatePhysicalCompatibility(
        ExecutablePhysicalIndexRoute physicalIndex,
        PhysicalQueryIdentityFieldResolution identityFields,
        IReadOnlyList<BoundedQueryPredicateField> predicates,
        BoundedQueryDeclaration query,
        string target,
        List<GroundworkDiagnostic> diagnostics,
        out IReadOnlyList<string> requiredEqualityPrefixPaths)
    {
        requiredEqualityPrefixPaths = [];
        var paths = physicalIndex.Columns
            .Select(column => identityFields.ResolveIndexPath(physicalIndex.Target, column.Column))
            .Where(path => path != PhysicalDocumentFieldPaths.StorageScope)
            .ToArray();
        var predicatePaths = predicates
            .SelectMany(identityFields.ResolvePredicateEvidencePaths)
            .ToArray();
        if (!paths.Take(predicatePaths.Length).SequenceEqual(predicatePaths))
        {
            diagnostics.Add(Error(
                "GW-QUERY-006",
                $"Query predicate evidence [{string.Join(", ", predicatePaths)}] is not a compound prefix of physical index '{physicalIndex.Identity}'.",
                target));
            return false;
        }

        if (query.SortFields.Count == 0)
            return true;

        var sortPaths = query.SortFields
            .Select(field => identityFields.ResolveOrderPath(field.Path))
            .ToArray();
        var start = paths.Take(sortPaths.Length).SequenceEqual(sortPaths)
            ? 0
            : predicatePaths.Length;
        if (!paths.Skip(start).Take(sortPaths.Length).SequenceEqual(sortPaths))
        {
            diagnostics.Add(Error(
                "GW-QUERY-006",
                $"Query ordering [{string.Join(", ", sortPaths)}] is incompatible with physical index '{physicalIndex.Identity}'.",
                target));
            return false;
        }

        if (start != 0 && predicates.Any(predicate =>
                predicate.Operations.Count != 1 ||
                !predicate.Operations.Contains(PortableQueryOperation.Equal)))
        {
            diagnostics.Add(Error(
                "GW-QUERY-006",
                "An ordered compound-index suffix requires single-value equality on every skipped predicate-prefix field.",
                target));
            return false;
        }
        if (start != 0)
            requiredEqualityPrefixPaths = Array.AsReadOnly(
                predicates.Select(predicate => predicate.Path).ToArray());

        var indexDirections = physicalIndex.Columns
            .Where(column => paths.Contains(
                identityFields.ResolveIndexPath(physicalIndex.Target, column.Column),
                StringComparer.Ordinal))
            .Skip(start)
            .Take(sortPaths.Length)
            .Select(column => column.Direction)
            .ToArray();
        var requested = query.SortFields.Select(field => field.Direction).ToArray();
        if (!indexDirections.SequenceEqual(requested) &&
            !indexDirections.Select(Opposite).SequenceEqual(requested))
        {
            diagnostics.Add(Error(
                "GW-QUERY-006",
                $"Query ordering directions are incompatible with physical index '{physicalIndex.Identity}'.",
                target));
            return false;
        }
        return true;
    }

    private static IReadOnlyList<PhysicalQueryOrder> ResolveOrder(
        ExecutableStorageRoute route,
        PhysicalQuerySourceKind source,
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query,
        PhysicalQueryPlannerCapabilities capabilities,
        PhysicalQueryIdentityFieldResolution identityFields)
    {
        var declared = query.SortFields.Count != 0
            ? query.SortFields
            : query.SortSupport == QuerySortSupport.None
                ? []
                : logicalIndex.Fields.Select(field => new BoundedQuerySortField(
                    field.Path,
                    query.SortSupport == QuerySortSupport.Descending
                        ? PhysicalSortDirection.Descending
                        : PhysicalSortDirection.Ascending)).ToArray();
        var order = declared.Select(field => new PhysicalQueryOrder(
            field.Path,
            identityFields.Resolve(field.Path, logicalIndex.GetValueKind(field.Path)),
            field.Direction,
            IsIdentityTieBreak: false)).ToList();
        if (route.ScopePolicy == StorageScopePolicy.Scoped &&
            order.All(item => item.Path != PhysicalDocumentFieldPaths.StorageScope))
        {
            order.Add(new PhysicalQueryOrder(
                PhysicalDocumentFieldPaths.StorageScope,
                ResolveField(
                    route,
                    source,
                    PhysicalDocumentFieldPaths.StorageScope,
                    IndexValueKind.Keyword,
                    capabilities),
                PhysicalSortDirection.Ascending,
                IsIdentityTieBreak: true));
        }
        if (order.All(item => item.Path != PhysicalDocumentFieldPaths.Id))
        {
            order.Add(new PhysicalQueryOrder(
                PhysicalDocumentFieldPaths.Id,
                identityFields.Binding.Comparison,
                PhysicalSortDirection.Ascending,
                IsIdentityTieBreak: true));
        }
        return order;
    }

    private static PhysicalQueryIdentityFieldResolution ResolveDocumentIdentityFields(
        ExecutableStorageRoute route,
        PhysicalQuerySourceKind source,
        PhysicalQueryPlannerCapabilities capabilities)
    {
        var linked = source == PhysicalQuerySourceKind.LinkedIndex;
        var identity = linked
            ? route.LinkedRelationship!.Identity
            : route.Envelope.Identity;
        var target = linked
            ? ExecutableStorageObjectRole.LinkedIndexStorage
            : ExecutableStorageObjectRole.PrimaryStorage;
        var objectName = linked
            ? route.LinkedIndexStorage!.Name
            : route.PrimaryStorage.Name;
        var fieldSource = source switch
        {
            PhysicalQuerySourceKind.LinkedIndex => PhysicalQueryFieldSource.LinkedRelationship,
            PhysicalQuerySourceKind.NativeDocumentFields => PhysicalQueryFieldSource.NativeDocumentField,
            _ => PhysicalQueryFieldSource.Envelope
        };

        PhysicalQueryField Field(
            string path,
            ExecutableColumnRoute column,
            string? identifier = null) =>
            new(
                path,
                identifier ?? column.Identifier,
                fieldSource,
                target,
                objectName,
                IndexValueKind.Keyword);

        var binding = new PhysicalQueryDocumentIdentityBinding(
            identity.StringCasePolicy,
            identity.ComparisonAlgorithmId,
            identity.LookupAlgorithmId,
            Field(
                PhysicalDocumentIdentityFieldPaths.Original,
                identity.OriginalId,
                source == PhysicalQuerySourceKind.NativeDocumentFields
                    ? capabilities.NativeFieldIdentifiers[PhysicalDocumentFieldPaths.Id]
                    : null),
            Field(PhysicalDocumentIdentityFieldPaths.Comparison, identity.ComparisonKey),
            Field(PhysicalDocumentIdentityFieldPaths.Lookup, identity.LookupKey));
        return new PhysicalQueryIdentityFieldResolution(
            route,
            source,
            capabilities,
            binding);
    }

    private sealed class PhysicalQueryIdentityFieldResolution(
        ExecutableStorageRoute route,
        PhysicalQuerySourceKind source,
        PhysicalQueryPlannerCapabilities capabilities,
        PhysicalQueryDocumentIdentityBinding binding)
    {
        public PhysicalQueryDocumentIdentityBinding Binding { get; } = binding;

        public PhysicalQueryField Resolve(string path, IndexValueKind valueKind) =>
            path == PhysicalDocumentFieldPaths.Id
                ? Binding.Comparison
                : PhysicalQueryPlanCompiler.ResolveField(route, source, path, valueKind, capabilities);

        public IReadOnlyList<string> ResolvePredicateEvidencePaths(BoundedQueryPredicateField predicate)
        {
            if (predicate.Path != PhysicalDocumentFieldPaths.Id)
                return [predicate.Path];

            return PhysicalQueryIdentityDemand.Resolve(predicate.Operations) switch
            {
                PhysicalQueryIdentityEvidenceDemand.Exact =>
                [PhysicalDocumentIdentityFieldPaths.Lookup, PhysicalDocumentIdentityFieldPaths.Comparison],
                PhysicalQueryIdentityEvidenceDemand.Ordered => [PhysicalDocumentIdentityFieldPaths.Comparison],
                PhysicalQueryIdentityEvidenceDemand.None => [PhysicalDocumentIdentityFieldPaths.Comparison],
                PhysicalQueryIdentityEvidenceDemand.Mixed => [],
                _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate, null)
            };
        }

        public string ResolveOrderPath(string path) => path == PhysicalDocumentFieldPaths.Id
            ? PhysicalDocumentIdentityFieldPaths.Comparison
            : path;

        public string ResolveIndexPath(
            ExecutableStorageObjectRole target,
            ExecutableColumnRoute column)
        {
            var identity = target == ExecutableStorageObjectRole.LinkedIndexStorage
                ? route.LinkedRelationship!.Identity
                : route.Envelope.Identity;
            if (column.LogicalName == identity.OriginalId.LogicalName)
                return PhysicalDocumentIdentityFieldPaths.Original;
            if (column.LogicalName == identity.LookupKey.LogicalName)
                return PhysicalDocumentIdentityFieldPaths.Lookup;
            if (column.LogicalName == identity.ComparisonKey.LogicalName)
                return PhysicalDocumentIdentityFieldPaths.Comparison;
            return ResolveNonIdentityIndexPath(route, target, column);
        }
    }

    private static PhysicalQueryField ResolveField(
        ExecutableStorageRoute route,
        PhysicalQuerySourceKind source,
        string path,
        IndexValueKind logicalValueKind,
        PhysicalQueryPlannerCapabilities capabilities)
    {
        var linked = source == PhysicalQuerySourceKind.LinkedIndex;
        var target = linked ? ExecutableStorageObjectRole.LinkedIndexStorage : ExecutableStorageObjectRole.PrimaryStorage;
        var objectName = linked ? route.LinkedIndexStorage!.Name : route.PrimaryStorage.Name;
        if (source == PhysicalQuerySourceKind.NativeDocumentFields)
        {
            return new PhysicalQueryField(
                path,
                capabilities.NativeFieldIdentifiers[path],
                PhysicalQueryFieldSource.NativeDocumentField,
                target,
                objectName,
                logicalValueKind);
        }

        if (PhysicalDocumentFieldPaths.IsEnvelope(path))
        {
            var column = linked
                ? LinkedColumn(route, path)
                : EnvelopeColumn(route, path);
            return new PhysicalQueryField(
                path,
                column.Identifier,
                linked ? PhysicalQueryFieldSource.LinkedRelationship : PhysicalQueryFieldSource.Envelope,
                target,
                objectName,
                logicalValueKind);
        }

        if (source is PhysicalQuerySourceKind.LinkedIndex or PhysicalQuerySourceKind.PrimaryProjectedColumns)
        {
            var projection = route.ProjectedColumns.Single(column => column.Definition.Path == path);
            return new PhysicalQueryField(
                path,
                projection.Column.Identifier,
                PhysicalQueryFieldSource.ProjectedColumn,
                target,
                objectName,
                logicalValueKind);
        }

        return new PhysicalQueryField(
            path,
            route.Envelope.CanonicalJson.Identifier,
            PhysicalQueryFieldSource.CanonicalJsonPath,
            target,
            objectName,
            logicalValueKind);
    }

    private static IndexValueKind EnvelopeValueKind(string path) => path == PhysicalDocumentFieldPaths.Version
        ? IndexValueKind.Number
        : IndexValueKind.Keyword;

    private static void ValidateEnvelopeKinds(
        LogicalIndexDeclaration logicalIndex,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        foreach (var field in logicalIndex.Fields.Where(field => PhysicalDocumentFieldPaths.IsEnvelope(field.Path)))
        {
            var declared = logicalIndex.GetValueKind(field);
            var intrinsic = EnvelopeValueKind(field.Path);
            if (declared == intrinsic)
                continue;
            diagnostics.Add(Error(
                "GW-QUERY-010",
                $"Envelope path '{field.Path}' has intrinsic value kind '{intrinsic}' and cannot be declared as '{declared}'.",
                target));
        }
    }

    private static string ResolveNonIdentityIndexPath(
        ExecutableStorageRoute route,
        ExecutableStorageObjectRole target,
        ExecutableColumnRoute column)
    {
        var projection = route.ProjectedColumns.SingleOrDefault(candidate =>
            candidate.Target == target && candidate.Column.LogicalName == column.LogicalName);
        if (projection is not null)
            return projection.Definition.Path;
        if (column.LogicalName == route.Envelope.Id.LogicalName ||
            column.LogicalName == route.LinkedRelationship?.DocumentId.LogicalName)
            return PhysicalDocumentFieldPaths.Id;
        if (column.LogicalName == route.Envelope.DocumentKind.LogicalName ||
            column.LogicalName == route.LinkedRelationship?.DocumentKind.LogicalName)
            return PhysicalDocumentFieldPaths.DocumentKind;
        if (column.LogicalName == route.Envelope.StorageScope.LogicalName ||
            column.LogicalName == route.LinkedRelationship?.StorageScope.LogicalName)
            return PhysicalDocumentFieldPaths.StorageScope;
        if (column.LogicalName == route.Envelope.Version.LogicalName)
            return PhysicalDocumentFieldPaths.Version;
        if (column.LogicalName == route.Envelope.SchemaVersion.LogicalName)
            return PhysicalDocumentFieldPaths.SchemaVersion;
        return column.LogicalName;
    }

    private static ExecutableColumnRoute EnvelopeColumn(ExecutableStorageRoute route, string path) => path switch
    {
        PhysicalDocumentFieldPaths.Id => route.Envelope.Id,
        PhysicalDocumentFieldPaths.DocumentKind => route.Envelope.DocumentKind,
        PhysicalDocumentFieldPaths.StorageScope => route.Envelope.StorageScope,
        PhysicalDocumentFieldPaths.Version => route.Envelope.Version,
        PhysicalDocumentFieldPaths.SchemaVersion => route.Envelope.SchemaVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
    };

    private static ExecutableColumnRoute LinkedColumn(ExecutableStorageRoute route, string path) => path switch
    {
        PhysicalDocumentFieldPaths.Id => route.LinkedRelationship!.DocumentId,
        PhysicalDocumentFieldPaths.DocumentKind => route.LinkedRelationship!.DocumentKind,
        PhysicalDocumentFieldPaths.StorageScope => route.LinkedRelationship!.StorageScope,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
    };

    private static bool HasIndexedAccess(
        PhysicalQuerySourceKind source,
        ExecutablePhysicalIndexRoute? physicalIndex) =>
        physicalIndex is not null && source switch
        {
            PhysicalQuerySourceKind.LinkedIndex =>
                physicalIndex.Target == ExecutableStorageObjectRole.LinkedIndexStorage,
            PhysicalQuerySourceKind.PrimaryEnvelope or
            PhysicalQuerySourceKind.PrimaryProjectedColumns or
            PhysicalQuerySourceKind.NativeDocumentFields =>
                physicalIndex.Target == ExecutableStorageObjectRole.PrimaryStorage,
            _ => false
        };

    private static PhysicalQueryAccessKind ToAccessKind(PhysicalQuerySourceKind source) => source switch
    {
        PhysicalQuerySourceKind.LinkedIndex => PhysicalQueryAccessKind.LinkedIndexThenPrimary,
        PhysicalQuerySourceKind.PrimaryEnvelope => PhysicalQueryAccessKind.PrimaryEnvelope,
        PhysicalQuerySourceKind.PrimaryCanonicalJson => PhysicalQueryAccessKind.PrimaryCanonicalJson,
        PhysicalQuerySourceKind.PrimaryProjectedColumns => PhysicalQueryAccessKind.PrimaryProjectedColumns,
        PhysicalQuerySourceKind.NativeDocumentFields => PhysicalQueryAccessKind.NativeDocumentFields,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    private static PhysicalSortDirection Opposite(PhysicalSortDirection direction) => direction switch
    {
        PhysicalSortDirection.Ascending => PhysicalSortDirection.Descending,
        PhysicalSortDirection.Descending => PhysicalSortDirection.Ascending,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };

    private static GroundworkDiagnostic Error(string code, string message, string target) =>
        GroundworkDiagnostic.Error(code, message, target);
}
