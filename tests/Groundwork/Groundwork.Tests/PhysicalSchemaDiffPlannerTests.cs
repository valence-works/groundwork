using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalSchemaDiffPlannerTests
{
    private static readonly ProviderIdentity Provider = new("schema-test-provider", "1.0.0");
    private static readonly DateTimeOffset PlannedAt = new(2026, 7, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AppliedAt = PlannedAt.AddMinutes(1);

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, typeof(CreatePrimaryStorageOperation))]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, typeof(CreatePrimaryStorageOperation))]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, typeof(CreatePhysicalEntityStorageOperation))]
    public void EmptyProviderStateProducesStableOperationsForEveryPhysicalForm(
        PhysicalStorageForm form,
        Type creationOperationType)
    {
        var target = CreateTarget(form, includeSecondProjection: false);

        var first = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);
        var restart = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt.AddHours(1));

        Assert.True(first.IsApplicable, JoinDiagnostics(first));
        Assert.Contains(first.Operations, operation => operation.GetType() == creationOperationType);
        Assert.Contains(first.Operations, operation => operation is AddProjectedColumnOperation);
        Assert.Contains(first.Operations, operation => operation is CreatePhysicalIndexOperation);
        Assert.Contains(first.Operations, operation => operation is BackfillCanonicalJsonOperation);
        Assert.IsType<ValidatePhysicalSchemaOperation>(first.Operations[^2]);
        Assert.IsType<RecordPhysicalSchemaAppliedStateOperation>(first.Operations[^1]);
        Assert.Equal(
            first.Operations.Select(operation => (operation.Identity, operation.Fingerprint)),
            restart.Operations.Select(operation => (operation.Identity, operation.Fingerprint)));
    }

    [Fact]
    public void SharedAndLinkedStorageCreationAreDistinctSemanticOperations()
    {
        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(PhysicalStorageForm.SharedDocuments, includeSecondProjection: false),
            PhysicalSchemaHistoryState.Empty,
            PlannedAt);

        var primary = Assert.Single(plan.Operations.OfType<CreatePrimaryStorageOperation>());
        var linked = Assert.Single(plan.Operations.OfType<CreateLinkedStorageOperation>());

        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, primary.Storage.Role);
        Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, linked.Storage.Role);
        Assert.Equal(primary.Storage, primary.Route.PrimaryStorage);
        Assert.Equal(linked.Storage, linked.Route.LinkedIndexStorage);
        Assert.NotEqual(primary.Identity, linked.Identity);
    }

    [Fact]
    public void SharedPrimaryStorageIsCreatedOnceAcrossMultipleUnitRoutes()
    {
        var binding = new SharedStorageBinding("runtime-documents");
        var template = SampleManifests.MetadataManifest();
        var first = template.StorageUnits.Single();
        var shared = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Dynamic,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.SharedDocuments(binding)));
        var manifest = template with
        {
            StorageUnits =
            [
                first with { PhysicalStorage = shared },
                first with
                {
                    Identity = new StorageUnitIdentity("otherDocument"),
                    PhysicalStorage = shared
                }
            ],
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        var target = new PhysicalSchemaTarget(manifest.Identity, manifest.Version, Provider, compilation.Routes);

        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        var creation = Assert.Single(plan.Operations.OfType<CreatePrimaryStorageOperation>());
        Assert.Null(creation.StorageUnit);
        Assert.Equal("documents", creation.Storage.Name.Identifier);
        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, creation.Route.PrimaryKey.Target);
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public void ExecutableOperationsCarryTheirOwningRouteAndResolvedStorage(PhysicalStorageForm form)
    {
        var target = CreateTarget(form, includeSecondProjection: false);

        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        var route = Assert.Single(target.Routes);
        var column = Assert.Single(plan.Operations.OfType<AddProjectedColumnOperation>());
        var index = Assert.Single(plan.Operations.OfType<CreatePhysicalIndexOperation>());
        Assert.Same(route, column.Route);
        Assert.Same(route, index.Route);
        Assert.Equal(column.Column.Target, column.Storage.Role);
        Assert.Equal(index.Index.Target, index.Storage.Role);
        Assert.All(plan.Operations.OfType<BackfillCanonicalJsonOperation>(), operation =>
        {
            Assert.Same(route, operation.Route);
            Assert.Equal(operation.Target, operation.Storage!.Role);
        });
        Assert.Equal(target.Routes, Assert.Single(plan.Operations.OfType<ValidatePhysicalSchemaOperation>()).Routes);
    }

    [Fact]
    public void SameManifestVersionWithAdditiveDefinitionChangeProducesOnlyPendingSemanticWork()
    {
        var initialTarget = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var initialPlan = PhysicalSchemaDiffPlanner.Plan(initialTarget, PhysicalSchemaHistoryState.Empty, PlannedAt);
        var applied = Complete(initialPlan);
        var changedTarget = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: true);

        var changedPlan = PhysicalSchemaDiffPlanner.Plan(
            changedTarget,
            PhysicalSchemaHistoryState.FromApplied(applied),
            PlannedAt.AddHours(1));

        Assert.True(changedPlan.IsApplicable, JoinDiagnostics(changedPlan));
        Assert.Equal(initialTarget.ManifestVersion, changedTarget.ManifestVersion);
        Assert.DoesNotContain(changedPlan.Operations, operation => operation is CreatePhysicalEntityStorageOperation);
        var column = Assert.Single(changedPlan.Operations.OfType<AddProjectedColumnOperation>());
        Assert.Equal("priority", column.Column.Definition.LogicalName);
        var index = Assert.Single(changedPlan.Operations.OfType<CreatePhysicalIndexOperation>());
        Assert.Equal("by-priority", index.Index.Identity);
        Assert.Contains(changedPlan.Operations, operation => operation is BackfillCanonicalJsonOperation backfill && backfill.SubjectIdentity == "priority");
        Assert.IsType<RecordPhysicalSchemaAppliedStateOperation>(changedPlan.Operations[^1]);
    }

    [Fact]
    public void IdenticalRestartProducesNoOperations()
    {
        var target = CreateTarget(PhysicalStorageForm.DedicatedDocumentTable, includeSecondProjection: false);
        var initial = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);
        var applied = Complete(initial);

        var restart = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(applied),
            PlannedAt.AddDays(1));

        Assert.True(restart.IsApplicable, JoinDiagnostics(restart));
        Assert.Empty(restart.Operations);
        Assert.Equal(applied.TargetFingerprint, restart.Target.Fingerprint);
    }

    [Fact]
    public void SemanticMigrationRequiredProjectionIsRejectedInsteadOfCanonicalJsonBackfilled()
    {
        var target = CreateTarget(
            PhysicalStorageForm.DedicatedDocumentTable,
            includeSecondProjection: false,
            rebuildMode: ProjectionRebuildMode.SemanticMigrationRequired);

        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        Assert.False(plan.IsApplicable);
        Assert.Empty(plan.Operations);
        Assert.Equal("GW-SCHEMA-005", Assert.Single(plan.Diagnostics).Code);
    }

    [Fact]
    public void BackfillSubjectsWithTheSameLogicalNameRemainDistinctAcrossVersionOnlyChanges()
    {
        var initialTarget = CreateTarget(
            PhysicalStorageForm.DedicatedDocumentTable,
            includeSecondProjection: false,
            firstIndexName: "category");
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(
            initialTarget,
            PhysicalSchemaHistoryState.Empty,
            PlannedAt));
        var changedTarget = CreateTarget(
            PhysicalStorageForm.DedicatedDocumentTable,
            includeSecondProjection: false,
            firstIndexName: "category",
            manifestVersion: new StorageManifestVersion("2"));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            changedTarget,
            PhysicalSchemaHistoryState.FromApplied(applied),
            PlannedAt.AddHours(1));

        Assert.True(plan.IsApplicable, JoinDiagnostics(plan));
        Assert.Collection(
            plan.Operations,
            operation => Assert.IsType<ValidatePhysicalSchemaOperation>(operation),
            operation => Assert.IsType<RecordPhysicalSchemaAppliedStateOperation>(operation));
    }

    [Fact]
    public void ProviderVersionChangeUsesTheSameExclusionKeyAndProducesAValidationPlan()
    {
        var initialTarget = CreateTarget(
            PhysicalStorageForm.DedicatedDocumentTable,
            includeSecondProjection: false,
            provider: new ProviderIdentity("schema-test-provider", "1.0.0"));
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(
            initialTarget,
            PhysicalSchemaHistoryState.Empty,
            PlannedAt));
        var upgradedTarget = CreateTarget(
            PhysicalStorageForm.DedicatedDocumentTable,
            includeSecondProjection: false,
            provider: new ProviderIdentity("schema-test-provider", "2.0.0"));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            upgradedTarget,
            PhysicalSchemaHistoryState.FromApplied(applied),
            PlannedAt.AddHours(1));

        Assert.Equal(initialTarget.Identity, upgradedTarget.Identity);
        Assert.True(plan.IsApplicable, JoinDiagnostics(plan));
        Assert.Collection(
            plan.Operations,
            operation => Assert.IsType<ValidatePhysicalSchemaOperation>(operation),
            operation => Assert.IsType<RecordPhysicalSchemaAppliedStateOperation>(operation));
    }

    [Fact]
    public void AppliedStateCarriesDurableIdentityFingerprintsNamesOperationsTimestampsAndSnapshot()
    {
        var target = CreateTarget(PhysicalStorageForm.SharedDocuments, includeSecondProjection: false);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        var applied = Complete(plan);

        Assert.Equal(target.ManifestIdentity, applied.ManifestIdentity);
        Assert.Equal(target.ManifestVersion, applied.ManifestVersion);
        Assert.Equal(target.Provider, applied.Provider);
        Assert.Equal(target.Fingerprint, applied.TargetFingerprint);
        Assert.Equal(PlannedAt, applied.PlannedAt);
        Assert.Equal(AppliedAt, applied.AppliedAt);
        var route = Assert.Single(applied.Snapshot.Routes);
        Assert.Equal(target.Routes.Single().DefinitionFingerprint, route.DefinitionFingerprint);
        Assert.Equal(target.Routes.Single().Fingerprint, route.RouteFingerprint);
        Assert.Contains(route.ResolvedNames, name => name.Identifier == target.Routes.Single().PrimaryStorage.Name.Identifier);
        Assert.Equal(
            plan.Operations.Select(operation => operation.Identity),
            applied.AppliedOperations.Select(operation => operation.Identity));
        Assert.False(string.IsNullOrWhiteSpace(applied.Snapshot.CanonicalJson));
    }

    [Fact]
    public void AppliedStateRoundTripsThroughCanonicalDurableSerialization()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: true);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);
        var applied = Complete(plan);

        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(json);

        Assert.Equal(applied.ManifestIdentity, restored.ManifestIdentity);
        Assert.Equal(applied.ManifestVersion, restored.ManifestVersion);
        Assert.Equal(applied.Provider, restored.Provider);
        Assert.Equal(applied.TargetFingerprint, restored.TargetFingerprint);
        Assert.Equal(applied.PlannedAt, restored.PlannedAt);
        Assert.Equal(applied.AppliedAt, restored.AppliedAt);
        Assert.Equal(applied.Snapshot.CanonicalJson, restored.Snapshot.CanonicalJson);
        Assert.Equal(applied.AppliedOperations, restored.AppliedOperations);
        Assert.Equal(json, PhysicalSchemaAppliedStateSerializer.Serialize(restored));

        var restart = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(restored),
            PlannedAt.AddDays(1));
        Assert.Empty(restart.Operations);
    }

    [Fact]
    public void DurableSerializationRejectsARewrittenTargetFingerprint()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var corrupted = json.Replace(applied.TargetFingerprint, new string('a', 64), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaAppliedStateSerializer.Deserialize(corrupted));
    }

    [Fact]
    public void DurableSerializationRejectsARewrittenRouteFingerprint()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var routeFingerprint = Assert.Single(applied.Snapshot.Routes).RouteFingerprint;
        var corrupted = json.Replace(routeFingerprint, new string('b', 64), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaAppliedStateSerializer.Deserialize(corrupted));
    }

    [Fact]
    public void DurableSerializationRejectsRewrittenResolvedNameSnapshotData()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var resolvedIdentifier = Assert.Single(applied.Snapshot.Routes).ResolvedNames[0].Identifier;
        var corrupted = json.Replace(
            $"\"identifier\":\"{resolvedIdentifier}\"",
            "\"identifier\":\"corrupted_identifier\"",
            StringComparison.Ordinal);

        Assert.NotEqual(json, corrupted);
        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaAppliedStateSerializer.Deserialize(corrupted));
    }

    [Fact]
    public void DurableSerializationRejectsARewrittenOperationFingerprint()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var operationFingerprint = applied.Snapshot.SemanticOperations[0].Fingerprint;
        var corrupted = json.Replace(operationFingerprint, new string('c', 64), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaAppliedStateSerializer.Deserialize(corrupted));
    }

    [Fact]
    public void DurableSerializationRejectsACoordinatedSlotPayloadFingerprintAndIdentityRewrite()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var operation = applied.Snapshot.SemanticOperations.First(candidate =>
            candidate.Kind == PhysicalSchemaOperationKind.BackfillCanonicalJson);
        var rewrittenSlot = $"{operation.SlotIdentity}-rewritten";
        var rewrittenPayload = operation.CanonicalPayload.Replace(
            $"{operation.SlotIdentity.Length}:{operation.SlotIdentity};",
            $"{rewrittenSlot.Length}:{rewrittenSlot};",
            StringComparison.Ordinal);
        var rewrittenFingerprint = Fingerprint(rewrittenPayload);
        var rewrittenIdentity = operation.Identity[..^16] + rewrittenFingerprint[..16];
        var rewritten = PhysicalSchemaAppliedStateSerializer.Serialize(applied)
            .Replace(operation.SlotIdentity, rewrittenSlot, StringComparison.Ordinal)
            .Replace(
                $"{operation.SlotIdentity.Length}:{rewrittenSlot};",
                $"{rewrittenSlot.Length}:{rewrittenSlot};",
                StringComparison.Ordinal)
            .Replace(operation.Fingerprint, rewrittenFingerprint, StringComparison.Ordinal)
            .Replace(operation.Identity, rewrittenIdentity, StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(rewritten);
        var rewrittenSnapshot = document.RootElement.GetProperty("snapshot").GetRawText();
        var rewrittenSnapshotFingerprint = Fingerprint(rewrittenSnapshot);
        rewritten = rewritten.Replace(applied.Snapshot.Fingerprint, rewrittenSnapshotFingerprint, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaAppliedStateSerializer.Deserialize(rewritten));
    }

    [Fact]
    public void ExactTargetFingerprintDoesNotHideMissingDurableSemanticOperationEvidence()
    {
        var target = CreateTarget(PhysicalStorageForm.PhysicalEntityTable, includeSecondProjection: false);
        var applied = Complete(PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt));
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var snapshot = document.RootElement.GetProperty("snapshot").GetRawText();
        using var snapshotDocument = System.Text.Json.JsonDocument.Parse(snapshot);
        var omittedOperation = snapshotDocument.RootElement
            .GetProperty("semanticOperations")
            .EnumerateArray()
            .First(operation => operation.GetProperty("kind").GetString() == PhysicalSchemaOperationKind.AddProjectedColumn.ToString());
        var omittedOperationJson = omittedOperation.GetRawText();
        var rewrittenSnapshot = snapshot.Replace($"{omittedOperationJson},", string.Empty, StringComparison.Ordinal);
        if (rewrittenSnapshot == snapshot)
            rewrittenSnapshot = snapshot.Replace($",{omittedOperationJson}", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(snapshot, rewrittenSnapshot);
        var rewrittenSnapshotFingerprint = Fingerprint(rewrittenSnapshot);
        var rewritten = json
            .Replace(snapshot, rewrittenSnapshot, StringComparison.Ordinal)
            .Replace(applied.Snapshot.Fingerprint, rewrittenSnapshotFingerprint, StringComparison.Ordinal);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(rewritten);

        var plan = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(restored),
            PlannedAt.AddDays(1));

        Assert.True(plan.IsApplicable, JoinDiagnostics(plan));
        Assert.Contains(plan.Operations, operation => operation.Identity == omittedOperation.GetProperty("identity").GetString());
    }

    [Fact]
    public void GreenfieldPolicyRejectsLegacyHistoryWithoutAnAppliedSnapshotDeterministically()
    {
        var target = CreateTarget(PhysicalStorageForm.DedicatedDocumentTable, includeSecondProjection: false);

        var first = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.LegacyHistoryDetected,
            PlannedAt,
            LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot);
        var second = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.LegacyHistoryDetected,
            PlannedAt.AddDays(1),
            LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot);

        Assert.False(first.IsApplicable);
        Assert.Empty(first.Operations);
        Assert.Equal("GW-SCHEMA-001", Assert.Single(first.Diagnostics).Code);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    private static PhysicalSchemaAppliedState Complete(PhysicalSchemaDiffPlan plan)
    {
        var acknowledgements = plan.Operations
            .Where(operation => operation is not RecordPhysicalSchemaAppliedStateOperation)
            .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                operation.Identity,
                operation.Fingerprint,
                AppliedAt))
            .ToArray();
        return plan.Complete(acknowledgements, AppliedAt);
    }

    private static PhysicalSchemaTarget CreateTarget(
        PhysicalStorageForm form,
        bool includeSecondProjection,
        ProjectionRebuildMode rebuildMode = ProjectionRebuildMode.FromCanonicalJson,
        string firstIndexName = "by-category",
        StorageManifestVersion? manifestVersion = null,
        ProviderIdentity? provider = null)
    {
        var template = SampleManifests.MetadataManifest();
        var projectedColumns = new List<ProjectedColumnDefinition>
        {
            new(
                "category",
                "category",
                PortablePhysicalType.String,
                Length: 200,
                IsNullable: false,
                RebuildMode: rebuildMode)
        };
        var indexes = new List<PhysicalIndexDefinition>
        {
            new(
                firstIndexName,
                [new PhysicalIndexColumnDefinition("storage_scope", 0), new PhysicalIndexColumnDefinition("category", 1)])
        };

        if (includeSecondProjection)
        {
            projectedColumns.Add(new ProjectedColumnDefinition("priority", "priority", PortablePhysicalType.Int32));
            indexes.Add(new PhysicalIndexDefinition(
                "by-priority",
                [new PhysicalIndexColumnDefinition("storage_scope", 0), new PhysicalIndexColumnDefinition("priority", 1)]));
        }

        var binding = new SharedStorageBinding("runtime-documents");
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                projectedColumns,
                indexes,
                linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "configuration_documents",
                indexes: indexes,
                linkedProjectedColumns: projectedColumns,
                linkedProjectionLogicalName: "configuration_projection"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "configuration_entities",
                projectedColumns,
                indexes: indexes),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var manifest = template with
        {
            Version = manifestVersion ?? template.Version,
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition))
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            provider ?? Provider,
            compilation.Routes);
    }

    private static string JoinDiagnostics(PhysicalSchemaDiffPlan plan) =>
        string.Join("; ", plan.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
