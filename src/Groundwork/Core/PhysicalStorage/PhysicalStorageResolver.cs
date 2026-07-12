using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

public sealed record ResolvedPhysicalTableDefinition(
    StorageUnitIdentity StorageUnit,
    StorageUnitProvisioningMode ProvisioningMode,
    PhysicalTableDefinition Definition,
    SharedDocumentStorageDefinition? SharedStorageDefinition,
    IReadOnlyList<ScaleBearingPathDemand> ScaleBearingDemand,
    IReadOnlyList<ResolvedPhysicalObjectName> Names)
{
    public StorageScopePolicy ScopePolicy { get; init; }

    public ResolvedPhysicalObjectName PrimaryName =>
        Names.Single(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage);

    public bool Equals(ResolvedPhysicalTableDefinition? other) =>
        other is not null &&
        StorageUnit == other.StorageUnit &&
        ProvisioningMode == other.ProvisioningMode &&
        Definition.Equals(other.Definition) &&
        Equals(SharedStorageDefinition, other.SharedStorageDefinition) &&
        ScaleBearingDemand.SequenceEqual(other.ScaleBearingDemand) &&
        Names.SequenceEqual(other.Names) &&
        ScopePolicy == other.ScopePolicy;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StorageUnit);
        hash.Add(ProvisioningMode);
        hash.Add(Definition);
        hash.Add(SharedStorageDefinition);
        foreach (var demand in ScaleBearingDemand)
            hash.Add(demand);
        foreach (var name in Names)
            hash.Add(name);
        hash.Add(ScopePolicy);
        return hash.ToHashCode();
    }
}

public sealed record ProviderPhysicalTableDefinition(
    ResolvedPhysicalTableDefinition Resolved,
    IReadOnlyList<ProviderPhysicalObjectName> Names,
    string Fingerprint)
{
    public PhysicalTableDefinition Definition => Resolved.Definition;

    public ProviderPhysicalObjectName PrimaryName =>
        Names.Single(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage);

    public bool Equals(ProviderPhysicalTableDefinition? other) =>
        other is not null &&
        Resolved.Equals(other.Resolved) &&
        Names.SequenceEqual(other.Names) &&
        Fingerprint == other.Fingerprint;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Resolved);
        foreach (var name in Names)
            hash.Add(name);
        hash.Add(Fingerprint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed record PhysicalStorageResolutionResult(
    IReadOnlyList<ProviderPhysicalTableDefinition> Definitions,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => !x.IsError);

    public bool Equals(PhysicalStorageResolutionResult? other) =>
        other is not null &&
        Definitions.SequenceEqual(other.Definitions) &&
        Diagnostics.SequenceEqual(other.Diagnostics);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var definition in Definitions)
            hash.Add(definition);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Resolves provider-neutral manifest intent through host naming and provider normalization. This
/// module does not execute DDL or route runtime document operations.
/// </summary>
public static class PhysicalStorageResolver
{
    public static PhysicalStorageResolutionResult Resolve(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        IProviderPhysicalNameNormalizer providerNameNormalizer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(namePolicy);
        ArgumentNullException.ThrowIfNull(providerNameNormalizer);

        var diagnostics = new List<GroundworkDiagnostic>();
        var definitions = new List<ProviderPhysicalTableDefinition>();
        var sharedPrimaryNames = new Dictionary<string, ResolvedPhysicalObjectName>(StringComparer.Ordinal);
        var providerNamesByInput = new Dictionary<
            (string NamingOwner, PhysicalObjectKind ObjectKind, string LogicalName),
            ProviderPhysicalObjectName>();

        foreach (var unit in manifest.StorageUnits)
        {
            if (unit.PhysicalStorage is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-001",
                    $"Storage unit '{unit.Identity.Value}' uses the legacy physicalization model; convert it explicitly through LegacyPhysicalStorageBridge.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage"));
                continue;
            }

            if (!TryResolveScopePolicy(unit, diagnostics, out var scopePolicy))
                continue;

            if (!ValidateDeclarations(unit, diagnostics))
                continue;

            var demand = ResolveScaleBearingDemand(unit.PhysicalStorage, unit.Identity, diagnostics);
            var definition = ResolveDefinition(unit, manifest, demand, diagnostics);
            if (definition is null)
                continue;

            if (!ValidateDefinition(unit, manifest, definition, demand, diagnostics))
                continue;

            SharedDocumentStorageDefinition? sharedStorageDefinition = null;
            if (definition.Form == PhysicalStorageForm.SharedDocuments &&
                !TryGetSharedDefinition(
                    manifest,
                    definition.SharedStorage!,
                    unit.Identity,
                    diagnostics,
                    out sharedStorageDefinition))
            {
                continue;
            }

            var names = ResolveHostNames(
                unit,
                definition,
                sharedStorageDefinition,
                namePolicy,
                sharedPrimaryNames,
                diagnostics);
            if (names.Count(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage) != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-029",
                    $"Storage unit '{unit.Identity.Value}' must resolve exactly one primary storage logical name.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            var resolved = new ResolvedPhysicalTableDefinition(
                unit.Identity,
                unit.PhysicalStorage.ProvisioningMode,
                definition,
                sharedStorageDefinition,
                demand.ToArray(),
                names.ToArray())
            {
                ScopePolicy = scopePolicy
            };
            var providerNames = NormalizeNames(
                resolved,
                providerNameNormalizer,
                providerNamesByInput,
                diagnostics);
            if (providerNames.Count(x => x.ObjectKind == PhysicalObjectKind.PrimaryStorage) != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-029",
                    $"Storage unit '{unit.Identity.Value}' must normalize exactly one provider primary storage identifier.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            definitions.Add(new ProviderPhysicalTableDefinition(
                resolved,
                providerNames.ToArray(),
                PhysicalStorageDefinitionSerializer.CreateFingerprint(resolved, providerNames)));
        }

        AddProviderNameCollisions(definitions, diagnostics);
        return new PhysicalStorageResolutionResult(definitions.ToArray(), diagnostics.ToArray());
    }

    private static bool TryResolveScopePolicy(
        StorageUnit unit,
        List<GroundworkDiagnostic> diagnostics,
        out StorageScopePolicy scopePolicy)
    {
        switch (unit.Tenancy.Kind)
        {
            case TenancyKind.Global:
                scopePolicy = StorageScopePolicy.Global;
                return true;
            case TenancyKind.Scoped:
                scopePolicy = StorageScopePolicy.Scoped;
                return true;
            default:
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-030",
                    $"Storage unit '{unit.Identity.Value}' uses unsupported tenancy kind '{unit.Tenancy.Kind}' and cannot resolve physical storage.",
                    $"storageUnits.{unit.Identity.Value}.tenancy"));
                scopePolicy = default;
                return false;
        }
    }

    private static bool ValidateDeclarations(
        StorageUnit unit,
        List<GroundworkDiagnostic> diagnostics)
    {
        var storage = unit.PhysicalStorage!;
        var target = $"storageUnits.{unit.Identity.Value}.physicalStorage";
        var valid = true;
        var indexGroups = storage.LogicalIndexes
            .GroupBy(x => x.Identity ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        if (indexGroups.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value.Length != 1))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-021",
                "Logical index identities must be non-empty and unique within a storage unit.",
                $"{target}.logicalIndexes"));
            valid = false;
        }

        foreach (var index in storage.LogicalIndexes)
        {
            if (index.Fields.Count == 0 ||
                index.Fields.Any(x => string.IsNullOrWhiteSpace(x.Path)) ||
                index.Fields.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != index.Fields.Count)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-021",
                    $"Logical index '{index.Identity}' requires one or more unique, non-empty stable serialized paths.",
                    $"{target}.logicalIndexes.{index.Identity}"));
                valid = false;
            }
        }

        var queryGroups = storage.BoundedQueries
            .GroupBy(x => x.Identity ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        if (queryGroups.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value.Length != 1))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-021",
                "Bounded query identities must be non-empty and unique within a storage unit.",
                $"{target}.boundedQueries"));
            valid = false;
        }

        foreach (var query in storage.BoundedQueries)
        {
            if (!indexGroups.TryGetValue(query.IndexIdentity ?? string.Empty, out var matching) || matching.Length != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-020",
                    $"Bounded query '{query.Identity}' must reference exactly one logical index '{query.IndexIdentity}'.",
                    $"{target}.boundedQueries.{query.Identity}.indexIdentity"));
                valid = false;
            }
            else if (query.SortFields.Count != 0 &&
                     (query.SortSupport == QuerySortSupport.None ||
                      query.SortFields.Count != matching[0].Fields.Count ||
                      !query.SortFields.Select(x => x.Path).SequenceEqual(matching[0].Fields.Select(x => x.Path))))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-027",
                    $"Bounded query '{query.Identity}' sort fields must match its logical index paths in compound order and require declared sort support.",
                    $"{target}.boundedQueries.{query.Identity}.sortFields"));
                valid = false;
            }

            if (query.Operations.Count == 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-021",
                    $"Bounded query '{query.Identity}' must declare at least one allowed operation.",
                    $"{target}.boundedQueries.{query.Identity}.operations"));
                valid = false;
            }
        }

        foreach (var queryGroup in storage.BoundedQueries
                     .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
                     .GroupBy(x => x.IndexIdentity, StringComparer.Ordinal))
        {
            if (!indexGroups.TryGetValue(queryGroup.Key, out var matching) || matching.Length != 1)
                continue;

            var directionShapes = queryGroup
                .Select(query => CanonicalizeSortDirections(ResolveSortDirections(query, matching[0])))
                .Select(DirectionShape)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (directionShapes.Length <= 1)
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-028",
                $"Scale-bearing queries for logical index '{queryGroup.Key}' require incompatible compound sort directions.",
                $"{target}.boundedQueries"));
            valid = false;
        }

        return valid;
    }

    private static IReadOnlyList<ScaleBearingPathDemand> ResolveScaleBearingDemand(
        StorageUnitPhysicalStorage storage,
        StorageUnitIdentity unitIdentity,
        List<GroundworkDiagnostic> diagnostics)
    {
        var indexes = storage.LogicalIndexes
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var demand = new List<ScaleBearingPathDemand>();

        foreach (var query in storage.BoundedQueries.Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            if (!indexes.TryGetValue(query.IndexIdentity, out var matching) || matching.Count != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-006",
                    $"Scale-bearing query '{query.Identity}' must reference exactly one declared logical index '{query.IndexIdentity}'.",
                    $"storageUnits.{unitIdentity.Value}.physicalStorage.boundedQueries.{query.Identity}"));
                continue;
            }

            var sortDirections = ResolveSortDirections(query, matching[0]);
            foreach (var (field, order) in matching[0].Fields.Select((field, order) => (field, order)))
            {
                if (string.IsNullOrWhiteSpace(field.Path))
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-007",
                        $"Scale-bearing query '{query.Identity}' references an index with an empty serialized path.",
                        $"storageUnits.{unitIdentity.Value}.physicalStorage.logicalIndexes.{query.IndexIdentity}"));
                    continue;
                }

                demand.Add(new ScaleBearingPathDemand(
                    query.Identity,
                    query.IndexIdentity,
                    field.Path,
                    sortDirections[order],
                    matching[0].ValueKind,
                    matching[0].MissingValueBehavior,
                    query.Operations.Order().ToArray(),
                    query.SortSupport,
                    query.PagingSupport,
                    query.SupportsDisjunction,
                    query.SupportsTotalCount));
            }
        }

        return demand
            .Distinct()
            .OrderBy(x => x.QueryIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.IndexIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static PhysicalTableDefinition? ResolveDefinition(
        StorageUnit unit,
        StorageManifest manifest,
        IReadOnlyList<ScaleBearingPathDemand> demand,
        List<GroundworkDiagnostic> diagnostics)
    {
        var storage = unit.PhysicalStorage!;
        if (storage.Policy is PhysicalStoragePolicy.ExplicitPolicy explicitPolicy)
            return ValidateExplicit(unit, manifest, explicitPolicy.Definition, diagnostics)
                ? explicitPolicy.Definition
                : null;

        var defaultPolicy = (PhysicalStoragePolicy.DefaultPolicy)storage.Policy;
        if (storage.ProvisioningMode == StorageUnitProvisioningMode.Dynamic)
        {
            if (defaultPolicy.SharedStorage is null)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-002",
                    "Dynamic storage using the default policy requires a shared-storage binding.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
                return null;
            }

            if (!TryGetSharedDefinition(
                    manifest,
                    defaultPolicy.SharedStorage,
                    unit.Identity,
                    diagnostics,
                    out var sharedDefinition))
                return null;

            var projected = SynthesizeProjectedColumns(demand);
            var physicalIndexes = SynthesizePhysicalIndexes(
                unit,
                storage,
                projected,
                sharedDefinition!.Envelope);
            var hasLinkedStructures = projected.Count != 0 || physicalIndexes.Count != 0;
            return PhysicalTableDefinition.SharedDocuments(
                defaultPolicy.SharedStorage,
                projected,
                physicalIndexes,
                linkedProjectionLogicalName: hasLinkedStructures
                    ? $"{unit.Identity.Value}_projection"
                    : null);
        }

        if (defaultPolicy.SharedStorage is not null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-003",
                "Declared storage using the default policy cannot supply a shared-storage binding.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            return null;
        }

        var envelope = new DocumentEnvelopeDefinition();
        var projectedColumns = SynthesizeProjectedColumns(demand);
        var indexes = SynthesizePhysicalIndexes(unit, storage, projectedColumns, envelope);
        return projectedColumns.Count == 0
            ? PhysicalTableDefinition.DedicatedDocumentTable(
                unit.Identity.Value,
                envelope,
                indexes)
            : PhysicalTableDefinition.PhysicalEntityTable(
                unit.Identity.Value,
                projectedColumns,
                envelope,
                indexes);
    }

    private static IReadOnlyList<ProjectedColumnDefinition> SynthesizeProjectedColumns(
        IReadOnlyList<ScaleBearingPathDemand> demand) =>
        demand
            .Where(x => !IsEnvelopePath(x.Path))
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Select(group => new ProjectedColumnDefinition(
                FeatureDefaultColumnName(group.Key),
                group.Key,
                ToPortableType(group.First().ValueKind)))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<PhysicalIndexDefinition> SynthesizePhysicalIndexes(
        StorageUnit unit,
        StorageUnitPhysicalStorage storage,
        IReadOnlyList<ProjectedColumnDefinition> projectedColumns,
        DocumentEnvelopeDefinition envelope)
    {
        var projectedNames = projectedColumns.ToDictionary(
            x => x.Path,
            x => x.LogicalName,
            StringComparer.Ordinal);
        var scaleBearingQueries = storage.BoundedQueries
            .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing)
            .GroupBy(x => x.IndexIdentity, StringComparer.Ordinal);
        var physicalIndexes = new List<PhysicalIndexDefinition>();
        foreach (var queryGroup in scaleBearingQueries)
        {
            var logicalIndex = storage.LogicalIndexes.SingleOrDefault(x => x.Identity == queryGroup.Key);
            if (logicalIndex is null)
                continue;

            var sortDirections = ResolveCanonicalSortDirections(queryGroup, logicalIndex);
            var columns = new List<PhysicalIndexColumnDefinition>();
            if (RequiresStorageScope(unit, logicalIndex))
            {
                columns.Add(new PhysicalIndexColumnDefinition(
                    envelope.StorageScopeColumn,
                    columns.Count));
            }

            var firstFieldOrder = columns.Count;
            columns.AddRange(logicalIndex.Fields.Select((field, order) => new PhysicalIndexColumnDefinition(
                IsEnvelopePath(field.Path)
                    ? EnvelopeColumnName(envelope, field.Path)
                    : projectedNames[field.Path],
                firstFieldOrder + order,
                sortDirections[order])));
            physicalIndexes.Add(new PhysicalIndexDefinition(
                logicalIndex.Identity,
                columns,
                logicalIndex.IsUnique));
        }

        return physicalIndexes;
    }

    private static bool ValidateExplicit(
        StorageUnit unit,
        StorageManifest manifest,
        PhysicalTableDefinition definition,
        List<GroundworkDiagnostic> diagnostics)
    {
        var valid = true;
        if (unit.PhysicalStorage!.ProvisioningMode == StorageUnitProvisioningMode.Dynamic &&
            definition.Form != PhysicalStorageForm.SharedDocuments)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-004",
                "Dynamic storage requires an explicit shared-documents definition.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            valid = false;
        }

        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            if (definition.SharedStorage is null ||
                !TryGetSharedDefinition(manifest, definition.SharedStorage, unit.Identity, diagnostics, out _))
                valid = false;
        }
        else if (string.IsNullOrWhiteSpace(definition.FeatureDefaultLogicalName) || definition.Envelope is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-005",
                "Dedicated and entity definitions require a primary logical name and canonical document envelope.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.policy"));
            valid = false;
        }

        return valid;
    }

    private static bool ValidateDefinition(
        StorageUnit unit,
        StorageManifest manifest,
        PhysicalTableDefinition definition,
        IReadOnlyList<ScaleBearingPathDemand> demand,
        List<GroundworkDiagnostic> diagnostics)
    {
        var valid = true;
        var target = $"storageUnits.{unit.Identity.Value}.physicalStorage.definition";
        if (definition.SchemaVersion <= 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Physical table schema version must be greater than zero.",
                $"{target}.schemaVersion"));
            valid = false;
        }

        SharedDocumentStorageDefinition? sharedDefinition = null;
        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            if (definition.SharedStorage is null ||
                !TryGetSharedDefinition(manifest, definition.SharedStorage, unit.Identity, diagnostics, out sharedDefinition))
            {
                valid = false;
            }
            else if (sharedDefinition!.SchemaVersion <= 0 ||
                     string.IsNullOrWhiteSpace(sharedDefinition.FeatureDefaultLogicalName) ||
                     !HasCanonicalEnvelope(sharedDefinition.Envelope))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-019",
                    "Shared document storage requires a positive schema version and complete canonical document envelope.",
                    $"{target}.sharedStorage"));
                valid = false;
            }
        }
        else if (definition.Envelope is null || !HasCanonicalEnvelope(definition.Envelope))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-019",
                "Dedicated and entity storage require a complete canonical document envelope.",
                $"{target}.envelope"));
            valid = false;
        }

        if (definition.Form != PhysicalStorageForm.SharedDocuments &&
            string.IsNullOrWhiteSpace(definition.FeatureDefaultLogicalName))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Dedicated and entity storage require a non-empty feature-default logical name.",
                $"{target}.featureDefaultLogicalName"));
            valid = false;
        }

        var hasLinkedStructures = definition.ProjectedColumns.Count != 0 || definition.Indexes.Count != 0;
        var hasLinkedName = !string.IsNullOrWhiteSpace(definition.LinkedProjectionLogicalName);
        if ((definition.Form == PhysicalStorageForm.SharedDocuments && hasLinkedStructures != hasLinkedName) ||
            (definition.Form == PhysicalStorageForm.DedicatedDocumentTable &&
             ((definition.ProjectedColumns.Count != 0 && !hasLinkedName) || (hasLinkedName && !hasLinkedStructures))) ||
            (definition.Form == PhysicalStorageForm.PhysicalEntityTable && hasLinkedName))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Linked projected/index structures require exactly one auxiliary table logical name and entity projections remain in-primary.",
                $"{target}.linkedProjectionLogicalName"));
            valid = false;
        }

        if (definition.Form == PhysicalStorageForm.PhysicalEntityTable && definition.ProjectedColumns.Count == 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-013",
                "Physical entity tables require at least one projected column.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        var duplicateColumnNames = definition.ProjectedColumns
            .GroupBy(x => x.LogicalName, StringComparer.Ordinal)
            .Any(x => x.Count() > 1);
        var duplicatePaths = definition.ProjectedColumns
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .Any(x => x.Count() > 1);
        if (duplicateColumnNames || duplicatePaths)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-016",
                "Projected column logical names and serialized paths must be unique within a definition.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        foreach (var column in definition.ProjectedColumns)
        {
            if (string.IsNullOrWhiteSpace(column.LogicalName) ||
                string.IsNullOrWhiteSpace(column.Path) ||
                column.Length is <= 0 ||
                column.Precision is <= 0 ||
                column.Scale is < 0 ||
                (column.Scale is not null && column.Precision is null) ||
                (column.Scale is not null && column.Precision is not null && column.Scale > column.Precision))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-018",
                    $"Projected column '{column.LogicalName}' has invalid portable metadata.",
                    $"{target}.projectedColumns"));
                valid = false;
            }
        }

        var envelope = definition.Envelope ?? sharedDefinition?.Envelope;
        var availableColumns = definition.ProjectedColumns
            .Select(x => x.LogicalName)
            .Concat(envelope is null
                ? []
                :
                [
                    envelope.IdColumn,
                    envelope.DocumentKindColumn,
                    envelope.StorageScopeColumn,
                    envelope.VersionColumn,
                    envelope.SchemaVersionColumn,
                    envelope.CanonicalJsonColumn
                ])
            .ToHashSet(StringComparer.Ordinal);
        if (definition.Indexes.GroupBy(x => x.LogicalName, StringComparer.Ordinal).Any(x => x.Count() > 1))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-016",
                "Physical index logical names must be unique within a definition.",
                $"{target}.indexes"));
            valid = false;
        }

        foreach (var index in definition.Indexes)
        {
            if (string.IsNullOrWhiteSpace(index.LogicalName) ||
                index.SchemaVersion <= 0 ||
                index.Columns.Count == 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-013",
                    $"Physical index '{index.LogicalName}' requires a name, positive schema version, and at least one column.",
                    $"{target}.indexes"));
                valid = false;
                continue;
            }

            var expectedOrder = Enumerable.Range(0, index.Columns.Count);
            if (!index.Columns.Select(x => x.Order).Order().SequenceEqual(expectedOrder))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-015",
                    $"Physical index '{index.LogicalName}' column order must be unique and contiguous from zero.",
                    $"{target}.indexes.{index.LogicalName}.columns"));
                valid = false;
            }

            if (unit.Tenancy.Kind == TenancyKind.Scoped &&
                envelope is not null &&
                index.Columns.All(x => x.ColumnLogicalName != envelope.StorageScopeColumn))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-026",
                    $"Scoped index '{index.LogicalName}' must include envelope scope column '{envelope.StorageScopeColumn}'.",
                    $"{target}.indexes.{index.LogicalName}.columns"));
                valid = false;
            }

            foreach (var indexColumn in index.Columns)
            {
                if (!availableColumns.Contains(indexColumn.ColumnLogicalName))
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-014",
                        $"Physical index '{index.LogicalName}' references unknown column '{indexColumn.ColumnLogicalName}'.",
                        $"{target}.indexes.{index.LogicalName}.columns"));
                    valid = false;
                }
            }
        }

        foreach (var query in unit.PhysicalStorage!.BoundedQueries
                     .Where(x => x.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing))
        {
            var indexIdentity = query.IndexIdentity;
            var logicalIndex = unit.PhysicalStorage.LogicalIndexes.Single(x => x.Identity == indexIdentity);
            var expectedColumns = ResolveExpectedIndexColumns(
                unit,
                logicalIndex,
                query,
                definition,
                sharedDefinition);
            var physicalIndex = definition.Indexes.SingleOrDefault(x => x.LogicalName == indexIdentity);
            if (expectedColumns is not null &&
                physicalIndex is not null &&
                physicalIndex.IsUnique == logicalIndex.IsUnique &&
                PhysicalIndexFulfills(
                    physicalIndex.Columns,
                    expectedColumns,
                    RequiresStorageScope(unit, logicalIndex)))
            {
                continue;
            }

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-025",
                $"Scale-bearing logical index '{indexIdentity}' requires a matching ordered physical index.",
                $"{target}.indexes"));
            valid = false;
        }

        var projectedPaths = definition.ProjectedColumns.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var unmetDemand = demand
            .Where(x => !IsEnvelopePath(x.Path) && !projectedPaths.Contains(x.Path))
            .Select(x => x.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (unmetDemand.Length != 0)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-017",
                $"Scale-bearing paths must be projected by the selected physical definition: {string.Join(", ", unmetDemand)}.",
                $"{target}.projectedColumns"));
            valid = false;
        }

        return valid;
    }

    private static bool HasCanonicalEnvelope(DocumentEnvelopeDefinition envelope) =>
        !string.IsNullOrWhiteSpace(envelope.IdColumn) &&
        !string.IsNullOrWhiteSpace(envelope.DocumentKindColumn) &&
        !string.IsNullOrWhiteSpace(envelope.StorageScopeColumn) &&
        !string.IsNullOrWhiteSpace(envelope.VersionColumn) &&
        !string.IsNullOrWhiteSpace(envelope.SchemaVersionColumn) &&
        !string.IsNullOrWhiteSpace(envelope.CanonicalJsonColumn);

    private static IReadOnlyList<ResolvedPhysicalObjectName> ResolveHostNames(
        StorageUnit unit,
        PhysicalTableDefinition definition,
        SharedDocumentStorageDefinition? sharedStorageDefinition,
        IPhysicalNamePolicy namePolicy,
        Dictionary<string, ResolvedPhysicalObjectName> sharedPrimaryNames,
        List<GroundworkDiagnostic> diagnostics)
    {
        var defaultNames = new List<(
            PhysicalObjectKind Kind,
            string Name,
            StorageUnitIdentity NamingOwner,
            bool AllowsUnitOverride)>();
        if (definition.Form == PhysicalStorageForm.SharedDocuments)
        {
            defaultNames.Add((
                PhysicalObjectKind.PrimaryStorage,
                sharedStorageDefinition!.FeatureDefaultLogicalName,
                new StorageUnitIdentity($"shared:{sharedStorageDefinition.Binding.Value}"),
                false));
        }
        else
        {
            defaultNames.Add((
                PhysicalObjectKind.PrimaryStorage,
                definition.FeatureDefaultLogicalName!,
                unit.Identity,
                true));
        }

        if (definition.LinkedProjectionLogicalName is not null)
        {
            defaultNames.Add((
                PhysicalObjectKind.LinkedIndexStorage,
                definition.LinkedProjectionLogicalName,
                unit.Identity,
                true));
        }

        defaultNames.AddRange(definition.ProjectedColumns.Select(x => (
            PhysicalObjectKind.ProjectedField,
            x.LogicalName,
            unit.Identity,
            true)));
        defaultNames.AddRange(definition.Indexes.Select(x => (
            PhysicalObjectKind.PhysicalIndex,
            x.LogicalName,
            unit.Identity,
            true)));

        var overrides = unit.PhysicalStorage!.NameOverrides
            .GroupBy(x => (x.ObjectKind, x.FeatureDefaultLogicalName))
            .ToDictionary(x => x.Key, x => x.ToArray());
        var result = new List<ResolvedPhysicalObjectName>();
        foreach (var item in defaultNames)
        {
            if (overrides.TryGetValue((item.Kind, item.Name), out var matching))
            {
                if (matching.Length != 1)
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-008",
                        $"Physical object '{item.Name}' has conflicting per-unit name overrides.",
                        $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
                    continue;
                }

                if (!item.AllowsUnitOverride)
                {
                    diagnostics.Add(GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-022",
                        $"Shared primary storage '{item.Name}' is manifest-owned and cannot be renamed by one storage unit.",
                        $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
                }
            }

            if (!item.AllowsUnitOverride &&
                sharedPrimaryNames.TryGetValue(sharedStorageDefinition!.Binding.Value, out var sharedPrimaryName))
            {
                result.Add(sharedPrimaryName);
                continue;
            }

            var logicalName = namePolicy.ResolveName(new PhysicalNameContext(
                item.NamingOwner,
                item.Kind,
                item.Name));
            if (item.AllowsUnitOverride && matching is not null)
                logicalName = matching[0].LogicalName;

            if (string.IsNullOrWhiteSpace(logicalName))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-009",
                    $"Physical object '{item.Name}' resolved to an empty logical name.",
                    $"storageUnits.{unit.Identity.Value}.physicalStorage.names"));
                continue;
            }

            var resolvedName = new ResolvedPhysicalObjectName(
                item.Kind,
                item.Name,
                logicalName,
                item.NamingOwner);
            result.Add(resolvedName);
            if (!item.AllowsUnitOverride)
                sharedPrimaryNames[sharedStorageDefinition!.Binding.Value] = resolvedName;
        }

        var knownObjects = defaultNames
            .Select(x => (x.Kind, x.Name))
            .ToHashSet();
        foreach (var nameOverride in unit.PhysicalStorage!.NameOverrides)
        {
            if (knownObjects.Contains((nameOverride.ObjectKind, nameOverride.FeatureDefaultLogicalName)))
                continue;

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-023",
                $"Name override references unknown physical object '{nameOverride.FeatureDefaultLogicalName}'.",
                $"storageUnits.{unit.Identity.Value}.physicalStorage.nameOverrides"));
        }

        return result;
    }

    private static IReadOnlyList<ProviderPhysicalObjectName> NormalizeNames(
        ResolvedPhysicalTableDefinition definition,
        IProviderPhysicalNameNormalizer normalizer,
        Dictionary<
            (string NamingOwner, PhysicalObjectKind ObjectKind, string LogicalName),
            ProviderPhysicalObjectName> namesByInput,
        List<GroundworkDiagnostic> diagnostics)
    {
        var result = new List<ProviderPhysicalObjectName>();
        foreach (var name in definition.Names)
        {
            var key = (name.NamingOwner.Value, name.ObjectKind, name.LogicalName);
            if (namesByInput.TryGetValue(key, out var cachedName))
            {
                result.Add(cachedName);
                continue;
            }

            var context = new ProviderPhysicalNameContext(
                name.NamingOwner,
                name.ObjectKind,
                name.LogicalName);
            var identifier = normalizer.Normalize(context);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-010",
                    $"Provider normalization produced an empty identifier for '{name.LogicalName}'.",
                    $"storageUnits.{definition.StorageUnit.Value}.physicalStorage.names"));
                continue;
            }

            var collisionScope = normalizer.GetCollisionScope(context);
            if (string.IsNullOrWhiteSpace(collisionScope))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-024",
                    $"Provider normalization produced an empty collision scope for '{name.LogicalName}'.",
                    $"storageUnits.{definition.StorageUnit.Value}.physicalStorage.names"));
                continue;
            }

            var providerName = new ProviderPhysicalObjectName(
                name.ObjectKind,
                name.FeatureDefaultLogicalName,
                name.LogicalName,
                identifier,
                collisionScope,
                name.NamingOwner);
            result.Add(providerName);
            namesByInput[key] = providerName;
        }

        return result;
    }

    private static void AddProviderNameCollisions(
        IReadOnlyList<ProviderPhysicalTableDefinition> definitions,
        List<GroundworkDiagnostic> diagnostics)
    {
        var collisions = definitions
            .SelectMany(definition => definition.Names.Select(name => (Definition: definition, Name: name)))
            .GroupBy(
                x => (
                    Scope: x.Name.CollisionScope,
                    x.Name.Identifier),
                PhysicalNameCollisionKeyComparer.Instance)
            .Where(group => !IsExactSharedPrimary(group));

        foreach (var collision in collisions)
        {
            var objects = collision
                .Select(x => $"{x.Name.NamingOwner.Value}:{x.Name.ObjectKind}:{x.Name.FeatureDefaultLogicalName}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-011",
                $"Provider identifier '{collision.Key.Identifier}' is produced by multiple physical objects in the same namespace: {string.Join(", ", objects)}.",
                "physicalStorage.providerNames"));
        }
    }

    private static bool IsExactSharedPrimary(
        IEnumerable<(ProviderPhysicalTableDefinition Definition, ProviderPhysicalObjectName Name)> group)
    {
        var entries = group.ToArray();
        if (entries.Length < 2)
            return true;

        var first = entries[0];
        var binding = first.Definition.Resolved.SharedStorageDefinition?.Binding;
        return binding is not null &&
               first.Name.ObjectKind == PhysicalObjectKind.PrimaryStorage &&
               entries.All(entry =>
                   entry.Definition.Definition.Form == PhysicalStorageForm.SharedDocuments &&
                   entry.Definition.Resolved.SharedStorageDefinition?.Binding == binding &&
                   entry.Name.ObjectKind == first.Name.ObjectKind &&
                   entry.Name.NamingOwner == first.Name.NamingOwner &&
                   entry.Name.FeatureDefaultLogicalName == first.Name.FeatureDefaultLogicalName &&
                   entry.Name.LogicalName == first.Name.LogicalName);
    }

    private sealed class PhysicalNameCollisionKeyComparer : IEqualityComparer<(string Scope, string Identifier)>
    {
        public static PhysicalNameCollisionKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Scope, string Identifier) x,
            (string Scope, string Identifier) y) =>
            StringComparer.Ordinal.Equals(x.Scope, y.Scope) &&
            StringComparer.Ordinal.Equals(x.Identifier, y.Identifier);

        public int GetHashCode((string Scope, string Identifier) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Scope),
                StringComparer.Ordinal.GetHashCode(obj.Identifier));
    }

    private static bool TryGetSharedDefinition(
        StorageManifest manifest,
        SharedStorageBinding binding,
        StorageUnitIdentity unitIdentity,
        List<GroundworkDiagnostic> diagnostics,
        out SharedDocumentStorageDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-012",
                "Shared-storage binding identity is required.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.sharedStorage"));
            definition = null;
            return false;
        }

        var matches = manifest.SharedDocumentStorages
            .Where(x => StringComparer.Ordinal.Equals(x.Binding.Value, binding.Value))
            .ToArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-PHYSICAL-012",
                matches.Length == 0
                    ? $"Shared-storage binding '{binding.Value}' is not declared by the manifest."
                    : $"Shared-storage binding '{binding.Value}' has conflicting manifest-owned definitions.",
                $"storageUnits.{unitIdentity.Value}.physicalStorage.sharedStorage"));
            definition = null;
            return false;
        }

        definition = matches[0];
        return true;
    }

    private static bool IsEnvelopePath(string path) => path is
        "id" or "documentKind" or "storageScope" or "version" or "schemaVersion";

    private static bool RequiresStorageScope(StorageUnit unit, LogicalIndexDeclaration index) =>
        unit.Tenancy.Kind == TenancyKind.Scoped &&
        index.Fields.All(field => field.Path != "storageScope");

    private static IReadOnlyList<PhysicalSortDirection> ResolveSortDirections(
        BoundedQueryDeclaration query,
        LogicalIndexDeclaration index)
    {
        if (query.SortFields.Count != 0)
            return query.SortFields.Select(x => x.Direction).ToArray();

        var direction = query.SortSupport == QuerySortSupport.Descending
            ? PhysicalSortDirection.Descending
            : PhysicalSortDirection.Ascending;
        return Enumerable.Repeat(direction, index.Fields.Count).ToArray();
    }

    private static IReadOnlyList<PhysicalSortDirection> ResolveCanonicalSortDirections(
        IEnumerable<BoundedQueryDeclaration> queries,
        LogicalIndexDeclaration index) =>
        queries
            .OrderBy(query => query.Identity, StringComparer.Ordinal)
            .Select(query => ResolveSortDirections(query, index))
            .First();

    private static IReadOnlyList<PhysicalSortDirection> CanonicalizeSortDirections(
        IReadOnlyList<PhysicalSortDirection> directions)
    {
        var forward = directions.ToArray();
        var reverse = directions.Select(Opposite).ToArray();
        return StringComparer.Ordinal.Compare(DirectionShape(forward), DirectionShape(reverse)) <= 0
            ? forward
            : reverse;
    }

    private static string DirectionShape(IEnumerable<PhysicalSortDirection> directions) =>
        string.Join(",", directions.Select(x => (int)x));

    private static PhysicalSortDirection Opposite(PhysicalSortDirection direction) => direction switch
    {
        PhysicalSortDirection.Ascending => PhysicalSortDirection.Descending,
        PhysicalSortDirection.Descending => PhysicalSortDirection.Ascending,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };

    private static bool PhysicalIndexFulfills(
        IReadOnlyList<PhysicalIndexColumnDefinition> actual,
        IReadOnlyList<PhysicalIndexColumnDefinition> expected,
        bool hasScopePrefix)
    {
        if (actual.Count != expected.Count ||
            !actual.Select(x => (x.ColumnLogicalName, x.Order))
                .SequenceEqual(expected.Select(x => (x.ColumnLogicalName, x.Order))))
        {
            return false;
        }

        var offset = hasScopePrefix ? 1 : 0;
        var actualDirections = actual.Skip(offset).Select(x => x.Direction).ToArray();
        var expectedDirections = expected.Skip(offset).Select(x => x.Direction).ToArray();
        return actualDirections.SequenceEqual(expectedDirections) ||
               actualDirections.SequenceEqual(expectedDirections.Select(Opposite));
    }

    private static IReadOnlyList<PhysicalIndexColumnDefinition>? ResolveExpectedIndexColumns(
        StorageUnit unit,
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query,
        PhysicalTableDefinition definition,
        SharedDocumentStorageDefinition? sharedDefinition)
    {
        var envelope = definition.Envelope ?? sharedDefinition?.Envelope;
        if (envelope is null)
            return null;

        var projectedColumns = definition.ProjectedColumns.ToDictionary(
            x => x.Path,
            x => x.LogicalName,
            StringComparer.Ordinal);
        var result = new List<PhysicalIndexColumnDefinition>();
        if (RequiresStorageScope(unit, logicalIndex))
            result.Add(new PhysicalIndexColumnDefinition(envelope.StorageScopeColumn, result.Count));

        var sortDirections = ResolveSortDirections(query, logicalIndex);
        foreach (var (field, fieldOrder) in logicalIndex.Fields.Select((field, order) => (field, order)))
        {
            string logicalName;
            if (IsEnvelopePath(field.Path))
            {
                logicalName = EnvelopeColumnName(envelope, field.Path);
            }
            else if (!projectedColumns.TryGetValue(field.Path, out logicalName!))
            {
                return null;
            }

            result.Add(new PhysicalIndexColumnDefinition(
                logicalName,
                result.Count,
                sortDirections[fieldOrder]));
        }

        return result;
    }

    private static string EnvelopeColumnName(DocumentEnvelopeDefinition envelope, string path) => path switch
    {
        "id" => envelope.IdColumn,
        "documentKind" => envelope.DocumentKindColumn,
        "storageScope" => envelope.StorageScopeColumn,
        "version" => envelope.VersionColumn,
        "schemaVersion" => envelope.SchemaVersionColumn,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
    };

    private static string FeatureDefaultColumnName(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var character in path)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.ToString();
    }

    private static PortablePhysicalType ToPortableType(IndexValueKind kind) => kind switch
    {
        IndexValueKind.String or IndexValueKind.Keyword => PortablePhysicalType.String,
        IndexValueKind.Number => PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => PortablePhysicalType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
