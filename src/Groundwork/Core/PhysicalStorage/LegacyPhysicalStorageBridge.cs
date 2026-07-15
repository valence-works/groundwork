using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

public sealed record LegacyPhysicalStorageConversionResult(
    StorageUnitPhysicalStorage? PhysicalStorage,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsValid => PhysicalStorage is not null && Diagnostics.All(x => !x.IsError);
}

/// <summary>
/// Converts the pre-1.0 physicalization declarations without silently changing their storage
/// semantics. This bridge is additive and can be removed with the announced breaking cleanup.
/// </summary>
public static class LegacyPhysicalStorageBridge
{
    public static LegacyPhysicalStorageConversionResult Convert(
        StorageUnit unit,
        SharedStorageBinding sharedStorage,
        Func<StorageUnit, StorageUnitPhysicalStorage>? specializedAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(sharedStorage);

        if (unit.Physicalization.Kind == PhysicalizationKind.Specialized)
        {
            if (specializedAdapter is not null)
                return new LegacyPhysicalStorageConversionResult(specializedAdapter(unit), []);

            return new LegacyPhysicalStorageConversionResult(
                null,
                [
                    GroundworkDiagnostic.Error(
                        "GW-PHYSICAL-LEGACY-001",
                        $"Legacy Specialized storage unit '{unit.Identity.Value}' has no honest three-form mapping; supply an explicit adapter.",
                        $"storageUnits.{unit.Identity.Value}.physicalization")
                ]);
        }

        var diagnostics = ValidateLegacyQueries(unit);
        var logicalIndexes = unit.Indexes
            .Select(index => new LogicalIndexDeclaration(
                index.Identity,
                index.Fields.ToArray(),
                index.ValueKind,
                index.IsUnique,
                index.MissingValueBehavior))
            .ToArray();
        var boundedQueries = unit.Queries
            .Select(query => new BoundedQueryDeclaration(
                query.Identity,
                query.IndexIdentity,
                query.Operations.ToHashSet(),
                query.SortSupport,
                query.PagingSupport,
                BoundedQueryExecutionClass.Ordinary,
                query.SupportsDisjunction,
                query.SupportsTotalCount))
            .ToArray();

        // Eligible legacy fields use MissingValueBehavior.Excluded. An absent canonical path must
        // therefore become a null/omitted projection value instead of rejecting the document write.
        var projectedColumns = PhysicalizationProjection.EligibleFields(unit)
            .Select(field => new ProjectedColumnDefinition(
                field.Name,
                field.Path,
                ToPortableType(field.ValueKind),
                IsNullable: true))
            .ToArray();
        var projectedNames = projectedColumns.Select(x => x.LogicalName).ToHashSet(StringComparer.Ordinal);
        var physicalIndexes = unit.Indexes
            .Where(index => projectedNames.Contains(index.Identity))
            .Select(index => new PhysicalIndexDefinition(
                index.Identity,
                CreateLegacyPhysicalIndexColumns(unit, index),
                index.IsUnique,
                missingValueBehavior: index.MissingValueBehavior))
            .ToArray();

        var definition = PhysicalTableDefinition.SharedDocuments(
            sharedStorage,
            projectedColumns,
            physicalIndexes,
            linkedProjectionLogicalName: projectedColumns.Length == 0
                ? null
                : $"{unit.Identity.Value}_projection");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Dynamic,
            PhysicalStoragePolicy.Explicit(definition),
            logicalIndexes,
            boundedQueries);

        return new LegacyPhysicalStorageConversionResult(storage, diagnostics.ToArray());
    }

    public static StorageUnit Apply(
        StorageUnit unit,
        SharedStorageBinding sharedStorage,
        Func<StorageUnit, StorageUnitPhysicalStorage>? specializedAdapter = null)
    {
        var result = Convert(unit, sharedStorage, specializedAdapter);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(x => $"{x.Code}: {x.Message}")));
        }

        return unit with { PhysicalStorage = result.PhysicalStorage };
    }

    private static IReadOnlyList<GroundworkDiagnostic> ValidateLegacyQueries(StorageUnit unit)
    {
        var diagnostics = new List<GroundworkDiagnostic>();
        var indexes = unit.Indexes
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);

        foreach (var query in unit.Queries)
        {
            if (!indexes.TryGetValue(query.IndexIdentity, out var matching) || matching.Length != 1)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-LEGACY-002",
                    $"Legacy query '{query.Identity}' must reference exactly one index '{query.IndexIdentity}'.",
                    $"storageUnits.{unit.Identity.Value}.queries.{query.Identity}"));
                continue;
            }

            var index = matching[0];
            var unsupported = query.Operations.Except(index.SupportedOperations).ToArray();
            if (unsupported.Length != 0)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-LEGACY-003",
                    $"Legacy query '{query.Identity}' requests operations outside index '{query.IndexIdentity}': {string.Join(", ", unsupported)}.",
                    $"storageUnits.{unit.Identity.Value}.queries.{query.Identity}.operations"));
            }

            if (query.SortSupport != QuerySortSupport.None && !index.IsSortable)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-PHYSICAL-LEGACY-004",
                    $"Legacy query '{query.Identity}' declares ordering but index '{query.IndexIdentity}' is not sortable.",
                    $"storageUnits.{unit.Identity.Value}.queries.{query.Identity}.sortSupport"));
            }
        }

        return diagnostics;
    }

    private static PortablePhysicalType ToPortableType(IndexValueKind kind) => kind switch
    {
        IndexValueKind.String or IndexValueKind.Keyword => PortablePhysicalType.String,
        IndexValueKind.Number => PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => PortablePhysicalType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static IReadOnlyList<PhysicalIndexColumnDefinition> CreateLegacyPhysicalIndexColumns(
        StorageUnit unit,
        IndexDeclaration index)
    {
        var columns = new List<PhysicalIndexColumnDefinition>();
        if (unit.Tenancy.Kind == TenancyKind.Scoped)
        {
            columns.Add(new PhysicalIndexColumnDefinition(
                new DocumentEnvelopeDefinition().StorageScopeColumn,
                columns.Count));
        }

        columns.Add(new PhysicalIndexColumnDefinition(index.Identity, columns.Count));
        return columns;
    }
}
