using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Compiles provider physical definitions into immutable execution mappings without provider SDK,
/// DDL, runtime I/O, or query translation dependencies.
/// </summary>
public static class ExecutableStorageRouteCompiler
{
    public static ExecutableStorageRouteCompilationResult Compile(ProviderPhysicalTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Compile([definition]);
    }

    public static ExecutableStorageRouteCompilationResult Compile(
        IReadOnlyList<ProviderPhysicalTableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var diagnostics = new List<GroundworkDiagnostic>();
        if (definitions.Count == 0)
        {
            diagnostics.Add(Error("GW-ROUTE-001", "At least one provider physical definition is required.", "physicalRoutes"));
            return new([], diagnostics);
        }

        ValidateDefinitionSet(definitions, diagnostics);
        var routes = new List<ExecutableStorageRoute>();
        foreach (var definition in definitions.OrderBy(x => x.Resolved.StorageUnit.Value, StringComparer.Ordinal))
        {
            var route = CompileOne(definition, diagnostics);
            if (route is not null)
                routes.Add(route);
        }

        return diagnostics.Any(diagnostic => diagnostic.IsError)
            ? new([], diagnostics.ToArray())
            : new(routes.ToArray(), diagnostics.ToArray());
    }

    private static ExecutableStorageRoute? CompileOne(
        ProviderPhysicalTableDefinition providerDefinition,
        List<GroundworkDiagnostic> diagnostics)
    {
        var target = $"physicalRoutes.{providerDefinition.Resolved.StorageUnit.Value}";
        var initialErrorCount = diagnostics.Count(diagnostic => diagnostic.IsError);
        if (string.IsNullOrWhiteSpace(providerDefinition.Fingerprint))
        {
            diagnostics.Add(Error(
                "GW-ROUTE-001",
                "Provider physical definition fingerprint is required.",
                $"{target}.definitionFingerprint"));
        }

        var expectedFingerprint = PhysicalStorageDefinitionSerializer.CreateFingerprint(
            providerDefinition.Resolved,
            providerDefinition.Names);
        if (!StringComparer.Ordinal.Equals(expectedFingerprint, providerDefinition.Fingerprint))
        {
            diagnostics.Add(Error(
                "GW-ROUTE-001",
                "Provider physical definition fingerprint does not match its resolved definition and names.",
                $"{target}.definitionFingerprint"));
        }

        var definition = providerDefinition.Definition;
        ValidateForm(definition, providerDefinition.Resolved.SharedStorageDefinition, target, diagnostics);
        var envelopeDefinition = definition.Envelope ?? providerDefinition.Resolved.SharedStorageDefinition?.Envelope;
        if (envelopeDefinition is null)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-003",
                "Executable document routes require one complete envelope mapping.",
                $"{target}.envelope"));
            return null;
        }

        var primaryName = RequireName(
            providerDefinition,
            PhysicalObjectKind.PrimaryStorage,
            providerDefinition.PrimaryName.FeatureDefaultLogicalName,
            target,
            diagnostics);
        ProviderPhysicalObjectName? linkedName = null;
        if (definition.LinkedProjectionLogicalName is not null)
        {
            linkedName = RequireName(
                providerDefinition,
                PhysicalObjectKind.LinkedIndexStorage,
                definition.LinkedProjectionLogicalName,
                target,
                diagnostics);
        }

        var expectedNames = new HashSet<(PhysicalObjectKind Kind, string FeatureDefault)>
        {
            (PhysicalObjectKind.PrimaryStorage, providerDefinition.PrimaryName.FeatureDefaultLogicalName)
        };
        if (definition.LinkedProjectionLogicalName is not null)
            expectedNames.Add((PhysicalObjectKind.LinkedIndexStorage, definition.LinkedProjectionLogicalName));

        var envelope = CompileEnvelope(providerDefinition, envelopeDefinition, target, diagnostics, expectedNames);
        var linkedRelationship = definition.LinkedKey is null
            ? null
            : CompileLinkedRelationship(providerDefinition, definition.LinkedKey, target, diagnostics, expectedNames);
        var projectedTarget = linkedName is null
            ? ExecutableStorageObjectRole.PrimaryStorage
            : ExecutableStorageObjectRole.LinkedIndexStorage;
        if (definition.Form == PhysicalStorageForm.SharedDocuments &&
            linkedName is null &&
            (definition.ProjectedColumns.Count != 0 || definition.Indexes.Count != 0))
        {
            diagnostics.Add(Error(
                "GW-ROUTE-003",
                "Shared projected fields and indexes require linked index storage.",
                $"{target}.linkedIndexStorage"));
        }

        var projections = new List<ExecutableProjectedColumnRoute>();
        var primaryColumns = new Dictionary<string, ExecutableColumnRoute>(StringComparer.Ordinal);
        if (envelope is not null)
        {
            foreach (var column in EnvelopeColumns(envelope))
                primaryColumns.TryAdd(column.LogicalName, column);
        }

        var linkedColumns = new Dictionary<string, ExecutableColumnRoute>(StringComparer.Ordinal);
        if (linkedRelationship is not null)
        {
            linkedColumns.TryAdd(envelopeDefinition.IdColumn, linkedRelationship.DocumentId);
            linkedColumns.TryAdd(envelopeDefinition.IdComparisonKeyColumn, linkedRelationship.Identity.ComparisonKey);
            linkedColumns.TryAdd(envelopeDefinition.IdLookupKeyColumn, linkedRelationship.Identity.LookupKey);
            linkedColumns.TryAdd(envelopeDefinition.DocumentKindColumn, linkedRelationship.DocumentKind);
            linkedColumns.TryAdd(envelopeDefinition.StorageScopeColumn, linkedRelationship.StorageScope);
        }
        var targetColumns = linkedName is null ? primaryColumns : linkedColumns;
        var projectedFieldKind = linkedName is null
            ? PhysicalObjectKind.ProjectedField
            : PhysicalObjectKind.LinkedProjectedField;

        foreach (var projection in definition.ProjectedColumns)
        {
            expectedNames.Add((projectedFieldKind, projection.LogicalName));
            var name = RequireName(
                providerDefinition,
                projectedFieldKind,
                projection.LogicalName,
                target,
                diagnostics);
            if (name is null)
                continue;

            var column = new ExecutableColumnRoute(projection.LogicalName, name.Identifier);
            if (!targetColumns.TryAdd(column.LogicalName, column))
            {
                diagnostics.Add(Error(
                    "GW-ROUTE-003",
                    $"Projected column '{projection.LogicalName}' collides with another executable column mapping.",
                    $"{target}.projectedColumns"));
                continue;
            }
            projections.Add(new ExecutableProjectedColumnRoute(projection, column, projectedTarget, name));
        }

        var indexes = new List<ExecutablePhysicalIndexRoute>();
        foreach (var index in definition.Indexes)
        {
            var indexTarget = ResolveIndexTarget(definition, index);
            var indexTargetColumns = indexTarget == ExecutableStorageObjectRole.LinkedIndexStorage
                ? linkedColumns
                : primaryColumns;
            expectedNames.Add((PhysicalObjectKind.PhysicalIndex, index.LogicalName));
            var name = RequireName(
                providerDefinition,
                PhysicalObjectKind.PhysicalIndex,
                index.LogicalName,
                target,
                diagnostics);
            var indexColumns = new List<ExecutableIndexColumnRoute>();
            foreach (var indexColumn in index.Columns.OrderBy(column => column.Order))
            {
                if (!indexTargetColumns.TryGetValue(indexColumn.ColumnLogicalName, out var column))
                {
                    diagnostics.Add(Error(
                        "GW-ROUTE-003",
                        $"Physical index '{index.LogicalName}' references unmapped column '{indexColumn.ColumnLogicalName}'.",
                        $"{target}.indexes.{index.LogicalName}"));
                    continue;
                }
                indexColumns.Add(new ExecutableIndexColumnRoute(column, indexColumn.Order, indexColumn.Direction));
            }

            if (name is not null && indexColumns.Count == index.Columns.Count)
            {
                indexes.Add(new ExecutablePhysicalIndexRoute(
                    index,
                    name,
                    indexTarget,
                    indexColumns));
            }
        }

        ValidateUnexpectedNames(providerDefinition, expectedNames, target, diagnostics);
        ValidateScaleBearingRoutes(providerDefinition, indexes, projections, envelope, target, diagnostics);
        if (primaryName is null ||
            envelope is null ||
            (linkedName is null) != (linkedRelationship is null) ||
            diagnostics.Count(diagnostic => diagnostic.IsError) != initialErrorCount)
            return null;

        var shared = definition.Form == PhysicalStorageForm.SharedDocuments;
        var primaryStorage = new ExecutableStorageObjectRoute(
            ExecutableStorageObjectRole.PrimaryStorage,
            primaryName,
            shared
                ? providerDefinition.Resolved.SharedStorageDefinition!.SchemaVersion
                : definition.SchemaVersion,
            shared
                ? providerDefinition.Resolved.SharedStorageDefinition!.Evolution
                : definition.Evolution);
        var linkedStorage = linkedName is null
            ? null
            : new ExecutableStorageObjectRoute(
                ExecutableStorageObjectRole.LinkedIndexStorage,
                linkedName,
                definition.SchemaVersion,
                definition.Evolution);
        var primaryKeyColumns = shared
            ? new[] { envelope.DocumentKind, envelope.StorageScope, envelope.Identity.LookupKey }
            : [envelope.StorageScope, envelope.Identity.LookupKey];
        var primaryKey = new ExecutableKeyRoute(ExecutableStorageObjectRole.PrimaryStorage, primaryKeyColumns);
        var auxiliaryKey = linkedRelationship is null
            ? null
            : new ExecutableKeyRoute(
                ExecutableStorageObjectRole.LinkedIndexStorage,
                shared
                    ? [linkedRelationship.DocumentKind, linkedRelationship.StorageScope, linkedRelationship.Identity.LookupKey]
                    : [linkedRelationship.StorageScope, linkedRelationship.Identity.LookupKey]);
        var discriminator = new ExecutableDiscriminatorRoute(
            envelope.DocumentKind,
            providerDefinition.Resolved.StorageUnit.Value,
            shared);
        var scopeKey = new ExecutableScopeKeyRoute(
            envelope.StorageScope,
            providerDefinition.Resolved.ScopePolicy,
            ParticipatesInPrimaryKey: true,
            ParticipatesInAuxiliaryKey: auxiliaryKey is not null);
        var maintenanceTargets = linkedStorage is null
            ? new[] { ExecutableStorageObjectRole.PrimaryStorage }
            : [ExecutableStorageObjectRole.PrimaryStorage, ExecutableStorageObjectRole.LinkedIndexStorage];
        var maintenance = Enum.GetValues<ExecutableMaintenanceOperation>()
            .Select(operation => new ExecutableMaintenanceRoute(operation, maintenanceTargets))
            .ToArray();
        var queryPaths = CompileQueryPaths(primaryKey, indexes, providerDefinition.Resolved.ScaleBearingDemand);
        var capabilities = ResolveCapabilities(
            definition.Form,
            providerDefinition.Resolved.ScopePolicy,
            linkedStorage is not null,
            envelope,
            linkedRelationship,
            projections,
            indexes,
            providerDefinition.Resolved.ScaleBearingDemand);
        var route = new ExecutableStorageRoute(
            providerDefinition.Resolved.StorageUnit,
            providerDefinition.Resolved.ProvisioningMode,
            definition.Form,
            definition.SharedStorage,
            providerDefinition.Resolved.ScopePolicy,
            primaryStorage,
            linkedStorage,
            envelope,
            linkedRelationship,
            discriminator,
            scopeKey,
            primaryKey,
            auxiliaryKey,
            projections,
            indexes,
            maintenance,
            queryPaths,
            capabilities,
            providerDefinition.Fingerprint,
            fingerprint: string.Empty);
        return route.WithFingerprint(ExecutableStorageRouteSerializer.CreateFingerprint(route));
    }

    private static ExecutableDocumentEnvelopeRoute? CompileEnvelope(
        ProviderPhysicalTableDefinition definition,
        DocumentEnvelopeDefinition envelope,
        string target,
        List<GroundworkDiagnostic> diagnostics,
        HashSet<(PhysicalObjectKind Kind, string FeatureDefault)> expectedNames)
    {
        ExecutableColumnRoute? Resolve(string logicalName)
        {
            expectedNames.Add((PhysicalObjectKind.EnvelopeField, logicalName));
            var name = RequireName(
                definition,
                PhysicalObjectKind.EnvelopeField,
                logicalName,
                target,
                diagnostics);
            return name is null ? null : new ExecutableColumnRoute(logicalName, name.Identifier);
        }

        var id = Resolve(envelope.IdColumn);
        var idComparison = Resolve(envelope.IdComparisonKeyColumn);
        var idLookup = Resolve(envelope.IdLookupKeyColumn);
        var kind = Resolve(envelope.DocumentKindColumn);
        var scope = Resolve(envelope.StorageScopeColumn);
        var version = Resolve(envelope.VersionColumn);
        var schemaVersion = Resolve(envelope.SchemaVersionColumn);
        var canonicalJson = Resolve(envelope.CanonicalJsonColumn);
        var resolved = new[] { id, idComparison, idLookup, kind, scope, version, schemaVersion, canonicalJson };
        var distinctColumnCount = resolved
            .Where(column => column is not null)
            .Select(column => column!.LogicalName)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctColumnCount != 8)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-003",
                "Envelope roles require eight distinct executable column mappings.",
                $"{target}.envelope"));
        }
        return resolved.Any(column => column is null) || distinctColumnCount != 8
            ? null
            : new ExecutableDocumentEnvelopeRoute(
                CompileIdentity(definition.Resolved.IdentityPolicy, id!, idComparison!, idLookup!),
                kind!,
                scope!,
                version!,
                schemaVersion!,
                canonicalJson!);
    }

    private static ExecutableLinkedRelationshipRoute? CompileLinkedRelationship(
        ProviderPhysicalTableDefinition definition,
        LinkedDocumentKeyDefinition linkedKey,
        string target,
        List<GroundworkDiagnostic> diagnostics,
        HashSet<(PhysicalObjectKind Kind, string FeatureDefault)> expectedNames)
    {
        ExecutableColumnRoute? Resolve(string logicalName)
        {
            expectedNames.Add((PhysicalObjectKind.LinkedIndexField, logicalName));
            var name = RequireName(
                definition,
                PhysicalObjectKind.LinkedIndexField,
                logicalName,
                target,
                diagnostics);
            return name is null ? null : new ExecutableColumnRoute(logicalName, name.Identifier);
        }

        var documentId = Resolve(linkedKey.DocumentIdColumn);
        var documentIdComparison = Resolve(linkedKey.DocumentIdComparisonKeyColumn);
        var documentIdLookup = Resolve(linkedKey.DocumentIdLookupKeyColumn);
        var documentKind = Resolve(linkedKey.DocumentKindColumn);
        var storageScope = Resolve(linkedKey.StorageScopeColumn);
        var resolved = new[] { documentId, documentIdComparison, documentIdLookup, documentKind, storageScope };
        var distinctColumnCount = resolved
            .Where(column => column is not null)
            .Select(column => column!.LogicalName)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctColumnCount != 5)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-003",
                "Linked document relationship roles require five distinct executable column mappings.",
                $"{target}.linkedRelationship"));
        }
        return resolved.Any(column => column is null) || distinctColumnCount != 5
            ? null
            : new ExecutableLinkedRelationshipRoute(
                CompileIdentity(
                    definition.Resolved.IdentityPolicy,
                    documentId!,
                    documentIdComparison!,
                    documentIdLookup!),
                documentKind!,
                storageScope!);
    }

    private static ExecutableDocumentIdentityRoute CompileIdentity(
        IdentityPolicy policy,
        ExecutableColumnRoute originalId,
        ExecutableColumnRoute comparisonKey,
        ExecutableColumnRoute lookupKey)
    {
        var portablePolicy = PortableStringComparison.ForIdentityPolicy(policy.StringCasePolicy);
        return new ExecutableDocumentIdentityRoute(
            policy.StringCasePolicy,
            PortableStringComparison.GetAlgorithmId(portablePolicy),
            PortableStringComparison.LookupHashAlgorithmId,
            originalId,
            comparisonKey,
            lookupKey);
    }

    private static void ValidateForm(
        PhysicalTableDefinition definition,
        SharedDocumentStorageDefinition? sharedDefinition,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var hasLinked = !string.IsNullOrWhiteSpace(definition.LinkedProjectionLogicalName);
        var hasLinkedStructures = definition.ProjectedColumns.Count != 0 ||
                                  definition.Indexes.Any(index =>
                                      ResolveIndexTarget(definition, index) == ExecutableStorageObjectRole.LinkedIndexStorage);
        var hasLinkedKey = definition.LinkedKey is not null;
        var invalid = definition.Form switch
        {
            PhysicalStorageForm.SharedDocuments => sharedDefinition is null || hasLinkedStructures != hasLinked || hasLinked != hasLinkedKey,
            PhysicalStorageForm.DedicatedDocumentTable =>
                definition.Envelope is null ||
                (definition.ProjectedColumns.Count != 0 && !hasLinked) ||
                (hasLinked && !hasLinkedStructures) ||
                hasLinked != hasLinkedKey,
            PhysicalStorageForm.PhysicalEntityTable =>
                definition.Envelope is null || hasLinked || hasLinkedKey || definition.ProjectedColumns.Count == 0,
            _ => true
        } || definition.Indexes.Any(index => !PhysicalIndexStorageTargetResolver.IsValid(definition, index));
        if (invalid)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-005",
                $"Physical form '{definition.Form}' has no executable primary/linked storage placement.",
                $"{target}.form"));
        }
    }

    private static ProviderPhysicalObjectName? RequireName(
        ProviderPhysicalTableDefinition definition,
        PhysicalObjectKind kind,
        string featureDefault,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var resolved = definition.Resolved.Names
            .Where(name => name.ObjectKind == kind && name.FeatureDefaultLogicalName == featureDefault)
            .ToArray();
        var provider = definition.Names
            .Where(name => name.ObjectKind == kind && name.FeatureDefaultLogicalName == featureDefault)
            .ToArray();
        if (resolved.Length != 1 || provider.Length != 1)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-002",
                $"Physical object '{kind}:{featureDefault}' requires exactly one resolved and provider name mapping.",
                $"{target}.names"));
            return null;
        }

        if (provider[0].LogicalName != resolved[0].LogicalName ||
            provider[0].NamingOwner != resolved[0].NamingOwner ||
            string.IsNullOrWhiteSpace(provider[0].Identifier) ||
            string.IsNullOrWhiteSpace(provider[0].CollisionScope))
        {
            diagnostics.Add(Error(
                "GW-ROUTE-002",
                $"Physical object '{kind}:{featureDefault}' has an inconsistent or empty provider name mapping.",
                $"{target}.names"));
            return null;
        }
        return provider[0];
    }

    private static void ValidateUnexpectedNames(
        ProviderPhysicalTableDefinition definition,
        HashSet<(PhysicalObjectKind Kind, string FeatureDefault)> expected,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var unexpected = definition.Names
            .Where(name => !expected.Contains((name.ObjectKind, name.FeatureDefaultLogicalName)))
            .Select(name => $"{name.ObjectKind}:{name.FeatureDefaultLogicalName}")
            .Concat(definition.Resolved.Names
                .Where(name => !expected.Contains((name.ObjectKind, name.FeatureDefaultLogicalName)))
                .Select(name => $"{name.ObjectKind}:{name.FeatureDefaultLogicalName}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (unexpected.Length != 0)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-002",
                $"Provider definition contains names with no executable mapping: {string.Join(", ", unexpected)}.",
                $"{target}.names"));
        }
    }

    private static void ValidateScaleBearingRoutes(
        ProviderPhysicalTableDefinition definition,
        IReadOnlyList<ExecutablePhysicalIndexRoute> indexes,
        IReadOnlyList<ExecutableProjectedColumnRoute> projections,
        ExecutableDocumentEnvelopeRoute? envelope,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        var projectedPaths = projections.Select(projection => projection.Definition.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var demand in definition.Resolved.ScaleBearingDemand)
        {
            if (envelope is null ||
                (!PhysicalDocumentFieldPaths.IsEnvelope(demand.Path) && !projectedPaths.Contains(demand.Path)))
            {
                diagnostics.Add(Error(
                    "GW-ROUTE-003",
                    $"Scale-bearing path '{demand.Path}' has no executable envelope or projected-column mapping.",
                    $"{target}.scaleBearingDemand"));
            }

            if (indexes.All(index => index.Identity != demand.IndexIdentity))
            {
                diagnostics.Add(Error(
                    "GW-ROUTE-006",
                    $"Scale-bearing query '{demand.QueryIdentity}' has no executable physical index route '{demand.IndexIdentity}'.",
                    $"{target}.queryPaths"));
            }
        }
    }

    private static IReadOnlyList<ExecutableQueryPathRoute> CompileQueryPaths(
        ExecutableKeyRoute primaryKey,
        IReadOnlyList<ExecutablePhysicalIndexRoute> indexes,
        IReadOnlyList<ScaleBearingPathDemand> demand)
    {
        var paths = new List<ExecutableQueryPathRoute>
        {
            new(
                "primary-identity",
                ExecutableQueryPathKind.PrimaryIdentity,
                primaryKey.Target,
                null,
                primaryKey.Columns.Select((column, order) =>
                    new ExecutableIndexColumnRoute(column, order, PhysicalSortDirection.Ascending)).ToArray(),
                [],
                false)
        };
        paths.AddRange(indexes.Select(index =>
        {
            var queries = demand
                .Where(item => item.IndexIdentity == index.Identity)
                .Select(item => item.QueryIdentity)
                .ToArray();
            return new ExecutableQueryPathRoute(
                index.Identity,
                ExecutableQueryPathKind.PhysicalIndex,
                index.Target,
                index.Name,
                index.Columns,
                queries,
                queries.Length != 0);
        }));
        return paths;
    }

    private static IReadOnlyList<ExecutableStorageCapability> ResolveCapabilities(
        PhysicalStorageForm form,
        StorageScopePolicy scopePolicy,
        bool hasLinkedStorage,
        ExecutableDocumentEnvelopeRoute envelope,
        ExecutableLinkedRelationshipRoute? linkedRelationship,
        IReadOnlyList<ExecutableProjectedColumnRoute> projections,
        IReadOnlyList<ExecutablePhysicalIndexRoute> indexes,
        IReadOnlyList<ScaleBearingPathDemand> demand)
    {
        var capabilities = new List<ExecutableStorageCapability>
        {
            ExecutableStorageCapability.PrimaryDocumentStorage,
            scopePolicy == StorageScopePolicy.Scoped
                ? ExecutableStorageCapability.ScopedStorageKey
                : ExecutableStorageCapability.GlobalStorageKey
        };
        if (form == PhysicalStorageForm.SharedDocuments)
            capabilities.Add(ExecutableStorageCapability.SharedDocumentDiscriminator);
        if (hasLinkedStorage)
            capabilities.Add(ExecutableStorageCapability.LinkedStorageMaintenance);
        if (projections.Count != 0)
            capabilities.Add(ExecutableStorageCapability.ProjectedColumnMaintenance);
        if (form == PhysicalStorageForm.PhysicalEntityTable)
            capabilities.Add(ExecutableStorageCapability.InPrimaryProjection);
        if (indexes.Count != 0)
            capabilities.Add(ExecutableStorageCapability.PhysicalIndexLookup);
        if (indexes.Any(index =>
            {
                var scopeColumn = index.Target == ExecutableStorageObjectRole.LinkedIndexStorage
                    ? linkedRelationship?.StorageScope.LogicalName
                    : envelope.StorageScope.LogicalName;
                return index.Columns.Count(column => column.Column.LogicalName != scopeColumn) > 1;
            }))
            capabilities.Add(ExecutableStorageCapability.CompoundIndexLookup);
        if (demand.Count != 0)
            capabilities.Add(ExecutableStorageCapability.ScaleBearingQuery);
        return capabilities;
    }

    private static ExecutableStorageObjectRole ResolveIndexTarget(
        PhysicalTableDefinition definition,
        PhysicalIndexDefinition index) => PhysicalIndexStorageTargetResolver.Resolve(definition, index) switch
        {
            PhysicalIndexStorageTarget.PrimaryStorage => ExecutableStorageObjectRole.PrimaryStorage,
            PhysicalIndexStorageTarget.LinkedIndexStorage => ExecutableStorageObjectRole.LinkedIndexStorage,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index.Target, null)
        };

    private static IEnumerable<ExecutableColumnRoute> EnvelopeColumns(ExecutableDocumentEnvelopeRoute envelope)
    {
        yield return envelope.Id;
        yield return envelope.Identity.ComparisonKey;
        yield return envelope.Identity.LookupKey;
        yield return envelope.DocumentKind;
        yield return envelope.StorageScope;
        yield return envelope.Version;
        yield return envelope.SchemaVersion;
        yield return envelope.CanonicalJson;
    }

    private static void ValidateDefinitionSet(
        IReadOnlyList<ProviderPhysicalTableDefinition> definitions,
        List<GroundworkDiagnostic> diagnostics)
    {
        var duplicateUnits = definitions
            .GroupBy(definition => definition.Resolved.StorageUnit.Value, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (duplicateUnits.Length != 0)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-001",
                $"Storage units must compile exactly once: {string.Join(", ", duplicateUnits)}.",
                "physicalRoutes"));
        }

        var collisions = definitions
            .SelectMany(definition => definition.Names)
            .GroupBy(name => (name.CollisionScope, name.Identifier), ProviderNameCollisionComparer.Instance)
            .Where(group => group.Select(PhysicalObjectIdentity).Distinct().Count() > 1)
            .ToArray();
        foreach (var collision in collisions)
        {
            diagnostics.Add(Error(
                "GW-ROUTE-004",
                $"Provider identifier '{collision.Key.Identifier}' collides between executable physical objects in scope '{collision.Key.CollisionScope}'.",
                "physicalRoutes.names"));
        }
    }

    private static string PhysicalObjectIdentity(ProviderPhysicalObjectName name) =>
        $"{name.NamingOwner.Value}|{name.ObjectKind}|{name.FeatureDefaultLogicalName}|{name.LogicalName}";

    private static GroundworkDiagnostic Error(string code, string message, string target) =>
        GroundworkDiagnostic.Error(code, message, target);

    private sealed class ProviderNameCollisionComparer : IEqualityComparer<(string CollisionScope, string Identifier)>
    {
        public static ProviderNameCollisionComparer Instance { get; } = new();

        public bool Equals(
            (string CollisionScope, string Identifier) x,
            (string CollisionScope, string Identifier) y) =>
            StringComparer.Ordinal.Equals(x.CollisionScope, y.CollisionScope) &&
            StringComparer.Ordinal.Equals(x.Identifier, y.Identifier);

        public int GetHashCode((string CollisionScope, string Identifier) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.CollisionScope),
                StringComparer.Ordinal.GetHashCode(obj.Identifier));
    }
}
