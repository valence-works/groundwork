using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Validation;

namespace Groundwork.Core.SchemaEvolution;

public static class PhysicalSchemaDiffPlanner
{
    public static PhysicalSchemaDiffPlan Plan(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        DateTimeOffset plannedAt,
        LegacyPhysicalSchemaHistoryPolicy legacyHistoryPolicy = LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(history);

        if (legacyHistoryPolicy != LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot)
            throw new ArgumentOutOfRangeException(nameof(legacyHistoryPolicy), legacyHistoryPolicy, null);

        if (history.HasLegacyHistory && history.AppliedState is null)
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                [GroundworkDiagnostic.Error(
                    "GW-SCHEMA-001",
                    "Legacy schema history does not contain a typed applied snapshot. Groundwork's greenfield policy rejects adoption or inference; remove the legacy history before applying this target.",
                    "schemaHistory")]);
        }

        var applied = history.AppliedState;
        if (applied is not null &&
            (applied.ManifestIdentity != target.ManifestIdentity ||
             !string.Equals(applied.Provider.Name, target.Provider.Name, StringComparison.Ordinal)))
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                [GroundworkDiagnostic.Error(
                    "GW-SCHEMA-002",
                    $"Applied state '{applied.Provider.Name}@{applied.Provider.Version}:{applied.ManifestIdentity.Value}' does not match target '{target.Identity}'.",
                    "schemaHistory.identity")]);
        }

        var identitySchemaDiagnostics = ValidateIdentitySchema(target, applied);
        if (identitySchemaDiagnostics.Count != 0)
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                identitySchemaDiagnostics,
                expectedAppliedTargetFingerprint: applied?.TargetFingerprint);
        }

        var unsupportedTransforms = target.Routes
            .SelectMany(route => route.ProjectedColumns
                .Where(column => column.Definition.RebuildMode == ProjectionRebuildMode.SemanticMigrationRequired)
                .Select(column => GroundworkDiagnostic.Error(
                    "GW-SCHEMA-005",
                    $"Projected column '{column.Definition.LogicalName}' on storage unit '{route.StorageUnit.Value}' requires a semantic migration. #44 cannot infer or apply semantic transforms.",
                    $"storageUnits.{route.StorageUnit.Value}.projectedColumns.{column.Definition.LogicalName}.rebuildMode")))
            .ToArray();
        if (unsupportedTransforms.Length != 0)
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                unsupportedTransforms,
                expectedAppliedTargetFingerprint: applied?.TargetFingerprint);
        }

        var desiredSemanticOperations = DeriveSemanticOperations(target);
        var snapshot = CreateSnapshot(target, desiredSemanticOperations);
        var diagnostics = ValidateAdditiveDiff(desiredSemanticOperations, applied);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
            return PhysicalSchemaDiffPlan.Invalid(target, plannedAt, diagnostics, snapshot, applied?.TargetFingerprint);

        var appliedIdentities = applied?.Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var pending = desiredSemanticOperations
            .Where(operation => !appliedIdentities.Contains(operation.Identity))
            .ToList();
        if (applied?.TargetFingerprint == target.Fingerprint && pending.Count == 0)
            return PhysicalSchemaDiffPlan.Valid(target, plannedAt, snapshot, [], applied.TargetFingerprint);

        // A changed manifest/provider target is durably validated and recorded even when the
        // structural semantic set is unchanged (for example, a manifest version-only change).
        pending.Add(new ValidatePhysicalSchemaOperation(
            target.Fingerprint,
            target.Routes,
            target.ProviderDefinitions));
        pending.Add(new RecordPhysicalSchemaAppliedStateOperation(target.Fingerprint));

        return PhysicalSchemaDiffPlan.Valid(
            target,
            plannedAt,
            snapshot,
            pending,
            applied?.TargetFingerprint);
    }

    private static IReadOnlyList<PhysicalSchemaOperation> DeriveSemanticOperations(PhysicalSchemaTarget target)
    {
        var operations = new List<PhysicalSchemaOperation>();
        foreach (var route in target.Routes)
        {
            operations.Add(route.Form == PhysicalStorageForm.PhysicalEntityTable
                ? new CreatePhysicalEntityStorageOperation(route)
                : new CreatePrimaryStorageOperation(route));

            if (route.LinkedIndexStorage is not null)
                operations.Add(new CreateLinkedStorageOperation(route));

            operations.AddRange(route.CollectionElementStorages.Select(storage =>
                new CreateCollectionElementStorageOperation(route, storage)));

            foreach (var column in route.ProjectedColumns.Where(column =>
                         column.Definition.Cardinality == ProjectionCardinality.Scalar))
            {
                var addColumn = new AddProjectedColumnOperation(route, column);
                operations.Add(addColumn);
                if (column.Definition.RebuildMode == ProjectionRebuildMode.FromCanonicalJson)
                {
                    operations.Add(new BackfillCanonicalJsonOperation(
                        route,
                        column.Target,
                        CanonicalJsonBackfillSubjectKind.ProjectedColumn,
                        column.Definition.LogicalName,
                        [column.Definition.Path],
                        addColumn.Fingerprint));
                }
                if (!column.Definition.IsNullable)
                    operations.Add(new FinalizeProjectedColumnOperation(route, column));
            }

            foreach (var index in route.Indexes)
            {
                var createIndex = new CreatePhysicalIndexOperation(route, index);
                operations.Add(createIndex);

                var projectedPaths = index.Columns
                    .Select(indexColumn => route.ProjectedColumns.SingleOrDefault(projected =>
                        projected.Target == index.Target &&
                        projected.Column.LogicalName == indexColumn.Column.LogicalName)?.Definition.Path)
                    .Where(path => path is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (projectedPaths.Length != 0 && index.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
                {
                    operations.Add(new BackfillCanonicalJsonOperation(
                        route,
                        index.Target,
                        CanonicalJsonBackfillSubjectKind.PhysicalIndex,
                        index.Identity,
                        projectedPaths,
                        createIndex.Fingerprint));
                }
            }
        }

        operations.AddRange(target.ProviderDefinitions.Select(definition =>
            new ApplyProviderPhysicalSchemaDefinitionOperation(definition)));

        return operations
            .GroupBy(operation => operation.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(OperationOrder)
            .ThenBy(operation => operation.StorageUnit?.Value, StringComparer.Ordinal)
            .ThenBy(operation => operation is ApplyProviderPhysicalSchemaDefinitionOperation providerDefinition
                ? providerDefinition.Definition.Kind
                : string.Empty, StringComparer.Ordinal)
            .ThenBy(operation => operation.SubjectIdentity, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<GroundworkDiagnostic> ValidateAdditiveDiff(
        IReadOnlyList<PhysicalSchemaOperation> desired,
        PhysicalSchemaAppliedState? applied)
    {
        if (applied is null)
            return [];

        var diagnostics = new List<GroundworkDiagnostic>();
        var desiredBySlot = desired.ToDictionary(operation => operation.SlotIdentity, StringComparer.Ordinal);
        var desiredIdentities = desired.Select(operation => operation.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var current in applied.Snapshot.SemanticOperations)
        {
            if (desiredIdentities.Contains(current.Identity))
                continue;

            var slot = current.SlotIdentity;
            if (desiredBySlot.TryGetValue(slot, out var replacement))
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-SCHEMA-003",
                    $"Applied operation '{current.Identity}' conflicts with changed definition '{replacement.Identity}'. #44 supports additive diffs only.",
                    $"schema.operations.{replacement.SubjectIdentity}"));
                continue;
            }

            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-SCHEMA-004",
                $"Applied operation '{current.Identity}' is absent from the desired target. Removing physical schema is outside the additive #44 contract.",
                $"schema.operations.{current.SubjectIdentity}"));
        }

        return diagnostics;
    }

    private static IReadOnlyList<GroundworkDiagnostic> ValidateIdentitySchema(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied)
    {
        if (applied is null)
            return [];

        var appliedRoutes = applied.Snapshot.Routes.ToDictionary(route => route.StorageUnit);
        var diagnostics = new List<GroundworkDiagnostic>();
        foreach (var route in target.Routes)
        {
            if (!appliedRoutes.TryGetValue(route.StorageUnit, out var appliedRoute))
                continue;

            var expected = DocumentIdentitySchemaState.Capture(route);
            if (IsCompatibleIdentityEvolution(appliedRoute.IdentitySchemaState, expected))
                continue;

            var reason = appliedRoute.IdentitySchemaState is null
                ? "does not contain typed identity schema state"
                : "does not match the target identity policy, algorithms, or column mappings";
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-SCHEMA-006",
                $"Applied storage unit '{route.StorageUnit.Value}' {reason}. Groundwork does not infer or automatically re-key identity data; explicitly drop and recreate the schema before applying this target.",
                $"schema.identity.{route.StorageUnit.Value}"));
        }

        return diagnostics;
    }

    private static bool IsCompatibleIdentityEvolution(
        DocumentIdentitySchemaState? applied,
        DocumentIdentitySchemaState expected)
    {
        if (applied is null)
            return false;

        return applied.StringCasePolicy == expected.StringCasePolicy &&
               string.Equals(applied.ComparisonAlgorithmId, expected.ComparisonAlgorithmId, StringComparison.Ordinal) &&
               string.Equals(applied.LookupAlgorithmId, expected.LookupAlgorithmId, StringComparison.Ordinal) &&
               applied.Primary == expected.Primary &&
               (applied.Linked is null || applied.Linked == expected.Linked);
    }

    private static PhysicalSchemaAppliedSnapshot CreateSnapshot(
        PhysicalSchemaTarget target,
        IReadOnlyList<PhysicalSchemaOperation> semanticOperations)
    {
        var routes = target.Routes.Select(route => new AppliedStorageRouteSnapshot(
            route.StorageUnit,
            route.DefinitionFingerprint,
            route.Fingerprint,
            ResolvedNames(route),
            ExecutableStorageRouteSerializer.Serialize(route),
            DocumentIdentitySchemaState.Capture(route))).ToArray();
        var operations = semanticOperations.Select(operation => new AppliedSemanticOperationSnapshot(
            operation.Identity,
            operation.Fingerprint,
            operation.Kind,
            operation.StorageUnit,
            operation.SubjectIdentity,
            operation.SlotIdentity,
            operation.CanonicalPayload)).ToArray();
        return new PhysicalSchemaAppliedSnapshot(routes, operations, target.ProviderDefinitions);
    }

    private static IReadOnlyList<PhysicalSchemaResolvedName> ResolvedNames(ExecutableStorageRoute route)
    {
        var names = new List<PhysicalSchemaResolvedName>
        {
            ObjectName(route.PrimaryStorage.Name, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeId", route.Envelope.Id, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeIdComparisonKey", route.Envelope.Identity.ComparisonKey, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeIdLookupKey", route.Envelope.Identity.LookupKey, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeDocumentKind", route.Envelope.DocumentKind, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeStorageScope", route.Envelope.StorageScope, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeVersion", route.Envelope.Version, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("EnvelopeSchemaVersion", route.Envelope.SchemaVersion, ExecutableStorageObjectRole.PrimaryStorage),
            ColumnName("CanonicalJson", route.Envelope.CanonicalJson, ExecutableStorageObjectRole.PrimaryStorage)
        };

        if (route.LinkedIndexStorage is not null)
            names.Add(ObjectName(route.LinkedIndexStorage.Name, ExecutableStorageObjectRole.LinkedIndexStorage));
        if (route.LinkedRelationship is not null)
        {
            names.Add(ColumnName("LinkedDocumentId", route.LinkedRelationship.DocumentId, ExecutableStorageObjectRole.LinkedIndexStorage));
            names.Add(ColumnName("LinkedDocumentIdComparisonKey", route.LinkedRelationship.Identity.ComparisonKey, ExecutableStorageObjectRole.LinkedIndexStorage));
            names.Add(ColumnName("LinkedDocumentIdLookupKey", route.LinkedRelationship.Identity.LookupKey, ExecutableStorageObjectRole.LinkedIndexStorage));
            names.Add(ColumnName("LinkedDocumentKind", route.LinkedRelationship.DocumentKind, ExecutableStorageObjectRole.LinkedIndexStorage));
            names.Add(ColumnName("LinkedStorageScope", route.LinkedRelationship.StorageScope, ExecutableStorageObjectRole.LinkedIndexStorage));
        }

        names.AddRange(route.ProjectedColumns.Select(column =>
            column.Definition.Cardinality == ProjectionCardinality.Scalar
                ? new PhysicalSchemaResolvedName("ProjectedColumn", column.Definition.LogicalName, column.Column.Identifier, column.Target)
                : null).Where(name => name is not null).Cast<PhysicalSchemaResolvedName>());
        names.AddRange(route.CollectionElementStorages.SelectMany(storage => new[]
        {
            ObjectName(storage.Storage.Name, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionDocumentKind", storage.DocumentKind.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionStorageScope", storage.StorageScope.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionIdComparisonKey", storage.IdComparisonKey.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionIdLookupKey", storage.IdLookupKey.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionOrdinal", storage.Ordinal.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ColumnName("CollectionValue", storage.Value.Column, ExecutableStorageObjectRole.CollectionElementStorage),
            ObjectName(storage.OwnerOrdinalKey.Name, ExecutableStorageObjectRole.CollectionElementStorage)
        }));
        names.AddRange(route.Indexes.Select(index =>
            new PhysicalSchemaResolvedName("PhysicalIndex", index.Identity, index.Name.Identifier, index.Target)));
        return names
            .Distinct()
            .OrderBy(name => name.Target)
            .ThenBy(name => name.Kind, StringComparer.Ordinal)
            .ThenBy(name => name.LogicalName, StringComparer.Ordinal)
            .ToArray();
    }

    private static PhysicalSchemaResolvedName ObjectName(
        ProviderPhysicalObjectName name,
        ExecutableStorageObjectRole target) =>
        new(name.ObjectKind.ToString(), name.LogicalName, name.Identifier, target);

    private static PhysicalSchemaResolvedName ColumnName(
        string kind,
        ExecutableColumnRoute column,
        ExecutableStorageObjectRole target) =>
        new(kind, column.LogicalName, column.Identifier, target);

    private static int OperationOrder(PhysicalSchemaOperation operation) => operation.Kind switch
    {
        PhysicalSchemaOperationKind.CreatePrimaryStorage => 0,
        PhysicalSchemaOperationKind.CreatePhysicalEntityStorage => 0,
        PhysicalSchemaOperationKind.CreateLinkedStorage => 1,
        PhysicalSchemaOperationKind.CreateCollectionElementStorage => 2,
        PhysicalSchemaOperationKind.AddProjectedColumn => 3,
        // Existing canonical documents must be projected and required fields finalized before
        // unique indexes are created. Linked index backfills run after creation only to reconcile
        // the complete aggregate under the now-validated index definition.
        PhysicalSchemaOperationKind.BackfillCanonicalJson => operation is BackfillCanonicalJsonOperation
        {
            SubjectKind: CanonicalJsonBackfillSubjectKind.ProjectedColumn
        } ? 3 : 6,
        PhysicalSchemaOperationKind.FinalizeProjectedColumn => 4,
        PhysicalSchemaOperationKind.CreatePhysicalIndex => 5,
        PhysicalSchemaOperationKind.ApplyProviderDefinition => 7,
        _ => 7
    };

}

public sealed class PhysicalSchemaDiffPlan
{
    private PhysicalSchemaDiffPlan(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IReadOnlyList<GroundworkDiagnostic> diagnostics,
        string? expectedAppliedTargetFingerprint)
    {
        Target = target;
        PlannedAt = plannedAt;
        Snapshot = snapshot;
        Operations = Array.AsReadOnly(operations.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        ExpectedAppliedTargetFingerprint = expectedAppliedTargetFingerprint;
    }

    public PhysicalSchemaTarget Target { get; }

    public DateTimeOffset PlannedAt { get; }

    public IReadOnlyList<PhysicalSchemaOperation> Operations { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public string? ExpectedAppliedTargetFingerprint { get; }

    public bool IsApplicable => Diagnostics.All(diagnostic => !diagnostic.IsError);

    internal PhysicalSchemaAppliedSnapshot Snapshot { get; }

    public PhysicalSchemaAppliedState Complete(
        IReadOnlyList<PhysicalSchemaOperationAcknowledgement> acknowledgements,
        DateTimeOffset appliedAt)
    {
        if (!IsApplicable)
            throw new InvalidOperationException("Cannot complete an inapplicable physical schema plan.");
        if (Operations.Count == 0)
            throw new InvalidOperationException("A no-change physical schema plan has no new applied state to record.");

        var expected = Operations
            .Where(operation => operation is not RecordPhysicalSchemaAppliedStateOperation)
            .ToArray();
        var supplied = acknowledgements?.ToArray() ?? throw new ArgumentNullException(nameof(acknowledgements));
        if (supplied.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} operation acknowledgements but received {supplied.Length}.");

        var acknowledgementsByIdentity = supplied
            .GroupBy(acknowledgement => acknowledgement.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var operation in expected)
        {
            if (!acknowledgementsByIdentity.TryGetValue(operation.Identity, out var matching) || matching.Length != 1)
                throw new InvalidOperationException($"Operation '{operation.Identity}' was not acknowledged exactly once.");
            if (matching[0].Fingerprint != operation.Fingerprint)
                throw new PhysicalSchemaFingerprintConflictException(
                    operation.Identity,
                    operation.Fingerprint,
                    matching[0].Fingerprint);
        }

        var appliedOperations = Operations.Select(operation =>
        {
            var acknowledgement = supplied.SingleOrDefault(item => item.Identity == operation.Identity);
            return new PhysicalSchemaAppliedOperation(
                operation.Identity,
                operation.Fingerprint,
                operation.Kind,
                operation.StorageUnit,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                acknowledgement?.AppliedAt ?? appliedAt,
                operation.CanonicalPayload);
        }).ToArray();
        return new PhysicalSchemaAppliedState(Target, PlannedAt, appliedAt, Snapshot, appliedOperations);
    }

    internal static PhysicalSchemaDiffPlan Valid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        string? expectedAppliedTargetFingerprint) =>
        new(target, plannedAt, snapshot, operations, [], expectedAppliedTargetFingerprint);

    internal static PhysicalSchemaDiffPlan Invalid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        IReadOnlyList<GroundworkDiagnostic> diagnostics,
        PhysicalSchemaAppliedSnapshot? snapshot = null,
        string? expectedAppliedTargetFingerprint = null) =>
        new(target, plannedAt, snapshot ?? new PhysicalSchemaAppliedSnapshot([], []), [], diagnostics, expectedAppliedTargetFingerprint);
}

public sealed class PhysicalSchemaFingerprintConflictException(
    string operationIdentity,
    string expectedFingerprint,
    string actualFingerprint)
    : InvalidOperationException(
        $"Operation '{operationIdentity}' fingerprint conflict: expected '{expectedFingerprint}', received '{actualFingerprint}'.")
{
    public string OperationIdentity { get; } = operationIdentity;

    public string ExpectedFingerprint { get; } = expectedFingerprint;

    public string ActualFingerprint { get; } = actualFingerprint;
}
