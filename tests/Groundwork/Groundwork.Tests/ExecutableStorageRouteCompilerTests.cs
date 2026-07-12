using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using System.Text.Json;
using Xunit;

namespace Groundwork.Tests;

public sealed class ExecutableStorageRouteCompilerTests
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, StorageScopePolicy.Global)]
    [InlineData(PhysicalStorageForm.SharedDocuments, StorageScopePolicy.Scoped)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, StorageScopePolicy.Global)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, StorageScopePolicy.Scoped)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, StorageScopePolicy.Global)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, StorageScopePolicy.Scoped)]
    public void EveryFormCompilesBothExplicitScopeModes(
        PhysicalStorageForm form,
        StorageScopePolicy scopePolicy)
    {
        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(ManifestFor(form, scopePolicy))));

        Assert.Equal(form, route.Form);
        Assert.Equal(scopePolicy, route.ScopePolicy);
        Assert.Equal(scopePolicy, route.ScopeKey.Policy);
        Assert.Contains(
            scopePolicy == StorageScopePolicy.Scoped
                ? ExecutableStorageCapability.ScopedStorageKey
                : ExecutableStorageCapability.GlobalStorageKey,
            route.CapabilityRequirements);
    }

    [Fact]
    public void SharedDocumentsCompilePrimaryLinkedMaintenanceAndScaleQueryRoutes()
    {
        var providerDefinition = Resolve(SharedScaleBearingManifest());

        var result = ExecutableStorageRouteCompiler.Compile(providerDefinition);

        var route = AssertRoute(result);
        Assert.Equal(PhysicalStorageForm.SharedDocuments, route.Form);
        Assert.Equal("runtime-documents", route.SharedStorage!.Value);
        Assert.Equal("documents", route.PrimaryStorage.Name.Identifier);
        Assert.Equal("configurationDocument_projection", route.LinkedIndexStorage!.Name.Identifier);
        Assert.Equal("document", route.Envelope.CanonicalJson.Identifier);
        Assert.Equal("configurationDocument", route.Discriminator.Value);
        Assert.True(route.Discriminator.ParticipatesInPrimaryKey);
        Assert.Equal(StorageScopePolicy.Scoped, route.ScopeKey.Policy);
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "id" },
            route.PrimaryKey.Columns.Select(column => column.LogicalName));
        Assert.Equal(
            new[] { "document_kind", "storage_scope", "document_id" },
            route.AuxiliaryKey!.Columns.Select(column => column.LogicalName));
        Assert.Equal("document_id", route.LinkedRelationship!.DocumentId.Identifier);
        Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, Assert.Single(route.ProjectedColumns).Target);

        var index = Assert.Single(route.Indexes);
        Assert.Equal("by-category", index.Name.Identifier);
        Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, index.Target);
        Assert.Equal(new[] { "storage_scope", "category" }, index.Columns.Select(column => column.Column.LogicalName));
        Assert.All(route.MaintenanceRoutes, maintenance =>
            Assert.Equal(
                new[] { ExecutableStorageObjectRole.PrimaryStorage, ExecutableStorageObjectRole.LinkedIndexStorage },
                maintenance.Targets));
        var query = route.CandidateQueryPaths.Single(path => path.Kind == ExecutableQueryPathKind.PhysicalIndex);
        Assert.Equal("by-category", query.Identity);
        Assert.Equal(new[] { "list-by-category" }, query.QueryIdentities);
        Assert.True(query.IsScaleBearing);
        Assert.Contains(ExecutableStorageCapability.ScaleBearingQuery, route.CapabilityRequirements);
    }

    [Fact]
    public void DedicatedDocumentsCompilePrimaryOnlyEnvelopeAndGlobalScopeRoute()
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = WithStorage(
            template with
            {
                StorageUnits = [template.StorageUnits.Single() with { Tenancy = TenancyPolicy.Global }]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(manifest)));

        Assert.Equal(PhysicalStorageForm.DedicatedDocumentTable, route.Form);
        Assert.Null(route.LinkedIndexStorage);
        Assert.Null(route.AuxiliaryKey);
        Assert.False(route.Discriminator.ParticipatesInPrimaryKey);
        Assert.Equal(StorageScopePolicy.Global, route.ScopeKey.Policy);
        Assert.True(route.ScopeKey.UsesGlobalSentinel);
        Assert.Equal(new[] { "storage_scope", "id" }, route.PrimaryKey.Columns.Select(column => column.LogicalName));
        Assert.All(route.MaintenanceRoutes, maintenance =>
            Assert.Equal(new[] { ExecutableStorageObjectRole.PrimaryStorage }, maintenance.Targets));
        Assert.Single(route.CandidateQueryPaths, path => path.Kind == ExecutableQueryPathKind.PrimaryIdentity);
    }

    [Fact]
    public void LinkedRelationshipWithoutLinkedStorageIsRejected()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            linkedKey: new LinkedDocumentKeyDefinition());
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-013");
    }

    [Fact]
    public void DedicatedDocumentsCanRouteProjectedFieldsAndIndexesThroughLinkedStorage()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            linkedProjectedColumns:
            [
                new ProjectedColumnDefinition(
                    "category",
                    "category",
                    PortablePhysicalType.String,
                    Length: 200,
                    Collation: "ordinal")
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-version",
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("version", 1)
                    ],
                    target: PhysicalIndexStorageTarget.PrimaryStorage),
                new PhysicalIndexDefinition(
                    "by-category",
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("category", 1)
                    ],
                    schemaVersion: 3,
                    evolution: new PhysicalEvolutionMetadata(RequiresBackfill: true),
                    target: PhysicalIndexStorageTarget.LinkedIndexStorage)
            ],
            schemaVersion: 2,
            evolution: new PhysicalEvolutionMetadata(SemanticMigrationIdentity: "configuration-v2"),
            linkedProjectionLogicalName: "configurationDocument_lookup",
            linkedKey: new LinkedDocumentKeyDefinition("document_fk", "kind_fk", "scope_fk"));
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(manifest)));

        Assert.Equal("configurationDocument_lookup", route.LinkedIndexStorage!.Name.Identifier);
        Assert.Equal(2, route.PrimaryStorage.SchemaVersion);
        Assert.Equal(2, route.LinkedIndexStorage.SchemaVersion);
        Assert.Equal("configuration-v2", route.PrimaryStorage.Evolution!.SemanticMigrationIdentity);
        var projection = Assert.Single(route.ProjectedColumns);
        Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, projection.Target);
        Assert.Equal(200, projection.Definition.Length);
        Assert.Equal("ordinal", projection.Definition.Collation);
        var primaryIndex = route.Indexes.Single(index => index.Identity == "by-version");
        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, primaryIndex.Target);
        Assert.Equal("version", primaryIndex.Columns[1].Column.Identifier);
        var linkedIndex = route.Indexes.Single(index => index.Identity == "by-category");
        Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, linkedIndex.Target);
        Assert.Equal(3, linkedIndex.Definition.SchemaVersion);
        Assert.True(linkedIndex.Definition.Evolution!.RequiresBackfill);
        Assert.Equal(new[] { "scope_fk", "document_fk" }, route.AuxiliaryKey!.Columns.Select(column => column.LogicalName));
        Assert.Equal("document_fk", route.LinkedRelationship!.DocumentId.Identifier);
        Assert.Equal("scope_fk", linkedIndex.Columns[0].Column.Identifier);
        Assert.DoesNotContain(ExecutableStorageCapability.CompoundIndexLookup, route.CapabilityRequirements);
    }

    [Fact]
    public void LinkedRelationshipAndProjectionNamesHonorOverridesAndProviderNormalization()
    {
        var manifest = SharedScaleBearingManifest();
        var unit = manifest.StorageUnits.Single();
        var storage = unit.PhysicalStorage!;
        manifest = manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        [
                            new PhysicalObjectNameOverride(
                                PhysicalObjectKind.LinkedIndexField,
                                "document_id",
                                "doc_fk"),
                            new PhysicalObjectNameOverride(
                                PhysicalObjectKind.LinkedIndexField,
                                "storage_scope",
                                "scope_fk"),
                            new PhysicalObjectNameOverride(
                                PhysicalObjectKind.LinkedProjectedField,
                                "category",
                                "category_lookup")
                        ])
                }
            ]
        };
        var resolved = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            new DelegateProviderPhysicalNameNormalizer(context => context.LogicalName.ToUpperInvariant()));
        Assert.True(resolved.IsValid, string.Join("; ", resolved.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Assert.Single(resolved.Definitions)));

        Assert.Equal("DOC_FK", route.LinkedRelationship!.DocumentId.Identifier);
        Assert.Equal("SCOPE_FK", route.LinkedRelationship.StorageScope.Identifier);
        Assert.Equal("DOC_FK", route.AuxiliaryKey!.Columns.Last().Identifier);
        Assert.Equal("CATEGORY_LOOKUP", Assert.Single(route.ProjectedColumns).Column.Identifier);
        Assert.Equal("SCOPE_FK", Assert.Single(route.Indexes).Columns[0].Column.Identifier);
        Assert.DoesNotContain(ExecutableStorageCapability.CompoundIndexLookup, route.CapabilityRequirements);
    }

    [Fact]
    public void DedicatedLinkedProjectionMayReusePrimaryEnvelopeIdentifierInAnotherTable()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            linkedProjectedColumns:
            [new ProjectedColumnDefinition("version", "payloadVersion", PortablePhysicalType.Int64)],
            linkedProjectionLogicalName: "configurationDocument_lookup");
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        var resolved = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(resolved.IsValid, string.Join("; ", resolved.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Assert.Single(resolved.Definitions)));
        Assert.Equal("version", route.Envelope.Version.Identifier);
        Assert.Equal("version", Assert.Single(route.ProjectedColumns).Column.Identifier);
        Assert.NotEqual(
            resolved.Definitions.Single().Names.Single(name => name.ObjectKind == PhysicalObjectKind.EnvelopeField && name.FeatureDefaultLogicalName == "version").CollisionScope,
            resolved.Definitions.Single().Names.Single(name => name.ObjectKind == PhysicalObjectKind.LinkedProjectedField).CollisionScope);
    }

    [Fact]
    public void LinkedRelationshipLogicalNamesCannotBeReusedByProjectedFieldsEvenWhenOverridesHideThePhysicalCollision()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            linkedProjectedColumns:
            [new ProjectedColumnDefinition("document_fk", "category", PortablePhysicalType.String)],
            linkedProjectionLogicalName: "configurationDocument_lookup",
            linkedKey: new LinkedDocumentKeyDefinition("document_fk", "kind_fk", "scope_fk"));
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                nameOverrides:
                [
                    new PhysicalObjectNameOverride(PhysicalObjectKind.LinkedIndexField, "document_fk", "relationship_id"),
                    new PhysicalObjectNameOverride(PhysicalObjectKind.LinkedProjectedField, "document_fk", "projected_document_id")
                ]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-016");
    }

    [Fact]
    public void DedicatedEnvelopeOnlyIndexRoutesThroughPrimaryStorageWithoutLinkedInference()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-kind",
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("document_kind", 1)
                    ])
            ]);
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(manifest)));

        Assert.Null(route.LinkedIndexStorage);
        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, Assert.Single(route.Indexes).Target);
        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, route.CandidateQueryPaths.Single(
            path => path.Kind == ExecutableQueryPathKind.PhysicalIndex).Target);
    }

    [Fact]
    public void EntityRouteKeepsProjectedCompoundIndexInPrimaryStorageAndPreservesOrder()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-customer-created",
            [new IndexField("customerId"), new IndexField("createdAt")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-customer",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("customerId", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [logicalIndex],
            [query]);

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(WithStorage(SampleManifests.MetadataManifest(), storage))));

        Assert.Equal(PhysicalStorageForm.PhysicalEntityTable, route.Form);
        Assert.Null(route.LinkedIndexStorage);
        Assert.All(route.ProjectedColumns, column => Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, column.Target));
        var index = Assert.Single(route.Indexes);
        Assert.Equal(ExecutableStorageObjectRole.PrimaryStorage, index.Target);
        Assert.Collection(
            index.Columns,
            column => Assert.Equal("storage_scope", column.Column.LogicalName),
            column =>
            {
                Assert.Equal("customerId", column.Column.LogicalName);
                Assert.Equal(PhysicalSortDirection.Ascending, column.Direction);
            },
            column =>
            {
                Assert.Equal("createdAt", column.Column.LogicalName);
                Assert.Equal(PhysicalSortDirection.Descending, column.Direction);
            });
        Assert.Contains(ExecutableStorageCapability.InPrimaryProjection, route.CapabilityRequirements);
        Assert.Contains(ExecutableStorageCapability.CompoundIndexLookup, route.CapabilityRequirements);
    }

    [Fact]
    public void RouteSerializationEqualityAndFingerprintAreDeterministicAcrossInputOrder()
    {
        var definition = Resolve(SharedScaleBearingManifest());
        var reversed = definition with
        {
            Resolved = definition.Resolved with { Names = definition.Resolved.Names.Reverse().ToArray() },
            Names = definition.Names.Reverse().ToArray()
        };

        var first = AssertRoute(ExecutableStorageRouteCompiler.Compile(definition));
        var second = AssertRoute(ExecutableStorageRouteCompiler.Compile(reversed));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            ExecutableStorageRouteSerializer.Serialize(first),
            ExecutableStorageRouteSerializer.Serialize(second));
        Assert.Equal(definition.Fingerprint, first.DefinitionFingerprint);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<ExecutablePhysicalIndexRoute>>(first.Indexes).Clear());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<ExecutableProjectedColumnRoute>>(first.ProjectedColumns).Clear());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<PhysicalIndexColumnDefinition>>(first.Indexes[0].Definition.Columns)[0] =
                new PhysicalIndexColumnDefinition("tampered", 0));
    }

    [Fact]
    public void CanonicalSerializationCarriesEveryExecutableSurfaceAndBothFingerprints()
    {
        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(SharedScaleBearingManifest())));

        using var json = JsonDocument.Parse(ExecutableStorageRouteSerializer.Serialize(route));
        var root = json.RootElement;

        Assert.Equal(
            new[]
            {
                "storageUnit", "provisioningMode", "form", "sharedStorage", "scopePolicy", "primaryStorage", "linkedIndexStorage",
                "envelope", "linkedRelationship", "discriminator", "scopeKey", "primaryKey", "auxiliaryKey",
                "projectedColumns", "indexes", "maintenance", "queryPaths", "capabilities",
                "definitionFingerprint", "fingerprint"
            },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(route.DefinitionFingerprint, root.GetProperty("definitionFingerprint").GetString());
        Assert.Equal(route.Fingerprint, root.GetProperty("fingerprint").GetString());
        Assert.Equal(6, root.GetProperty("envelope").EnumerateObject().Count());
        Assert.Equal(3, root.GetProperty("maintenance").GetArrayLength());
        Assert.Equal(2, root.GetProperty("queryPaths").GetArrayLength());
        Assert.NotEmpty(root.GetProperty("capabilities").EnumerateArray());
    }

    [Fact]
    public void EnvelopeAndProjectedProviderNameCollisionFailsDuringResolution()
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)]);
        var manifest = WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => context.ObjectKind is PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField
                ? "same-column"
                : context.LogicalName);

        var result = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, normalizer);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-011");
    }

    [Fact]
    public void ScopePolicyChangesKeyBehaviorRouteEqualityAndFingerprint()
    {
        var scoped = Resolve(WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default())));
        var template = SampleManifests.MetadataManifest();
        var global = Resolve(WithStorage(
            template with { StorageUnits = [template.StorageUnits.Single() with { Tenancy = TenancyPolicy.Global }] },
            new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default())));

        var scopedRoute = AssertRoute(ExecutableStorageRouteCompiler.Compile(scoped));
        var globalRoute = AssertRoute(ExecutableStorageRouteCompiler.Compile(global));

        Assert.NotEqual(scopedRoute, globalRoute);
        Assert.NotEqual(scopedRoute.Fingerprint, globalRoute.Fingerprint);
        Assert.False(scopedRoute.ScopeKey.UsesGlobalSentinel);
        Assert.True(globalRoute.ScopeKey.UsesGlobalSentinel);
    }

    [Fact]
    public void MissingNameMappingIsRejectedInsteadOfInferred()
    {
        var definition = Resolve(SharedScaleBearingManifest());
        var incomplete = definition with
        {
            Names = definition.Names.Where(name => name.ObjectKind != PhysicalObjectKind.LinkedProjectedField).ToArray()
        };

        var result = ExecutableStorageRouteCompiler.Compile(incomplete);

        Assert.Empty(result.Routes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-ROUTE-002");
    }

    [Fact]
    public void ProviderNameCollisionsAreRejectedAcrossRouteCompilationSet()
    {
        var first = Resolve(WithStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default())));
        var template = SampleManifests.MetadataManifest();
        var secondManifest = WithStorage(
            template with
            {
                StorageUnits =
                [
                    template.StorageUnits.Single() with
                    {
                        Identity = new StorageUnitIdentity("otherDocument")
                    }
                ]
            },
            new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default()));
        var second = Resolve(secondManifest);
        var collidedName = second.Names.Single(name => name.ObjectKind == PhysicalObjectKind.PrimaryStorage) with
        {
            Identifier = first.PrimaryName.Identifier,
            CollisionScope = first.PrimaryName.CollisionScope
        };
        second = second with
        {
            Names = second.Names.Select(name => name.ObjectKind == PhysicalObjectKind.PrimaryStorage ? collidedName : name).ToArray()
        };

        var result = ExecutableStorageRouteCompiler.Compile([first, second]);

        Assert.Empty(result.Routes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-ROUTE-004");
    }

    [Fact]
    public void ScaleBearingDemandWithoutExecutablePhysicalIndexIsRejected()
    {
        var definition = Resolve(SharedScaleBearingManifest());
        var invalid = definition with
        {
            Resolved = definition.Resolved with
            {
                Definition = PhysicalTableDefinition.SharedDocuments(
                    definition.Definition.SharedStorage!,
                    definition.Definition.ProjectedColumns,
                    linkedProjectionLogicalName: definition.Definition.LinkedProjectionLogicalName)
            }
        };

        var result = ExecutableStorageRouteCompiler.Compile(invalid);

        Assert.Empty(result.Routes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-ROUTE-006");
    }

    [Fact]
    public void LegacyOptimizedDeclarationCompilesToSharedLinkedBehavior()
    {
        var binding = new SharedStorageBinding("runtime-documents");
        var template = SampleManifests.MetadataManifest();
        var legacyUnit = template.StorageUnits.Single() with { Physicalization = PhysicalizationPolicy.Optimized };
        var converted = LegacyPhysicalStorageBridge.Apply(legacyUnit, binding);
        var manifest = template with
        {
            StorageUnits = [converted],
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
        };

        var route = AssertRoute(ExecutableStorageRouteCompiler.Compile(Resolve(manifest)));

        Assert.Equal(PhysicalStorageForm.SharedDocuments, route.Form);
        Assert.NotNull(route.LinkedIndexStorage);
        Assert.Equal("document_id", route.LinkedRelationship!.DocumentId.Identifier);
        Assert.All(route.ProjectedColumns, column => Assert.Equal(ExecutableStorageObjectRole.LinkedIndexStorage, column.Target));
    }

    private static ExecutableStorageRoute AssertRoute(ExecutableStorageRouteCompilationResult result)
    {
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.Single(result.Routes);
    }

    private static ProviderPhysicalTableDefinition Resolve(StorageManifest manifest)
    {
        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.Single(result.Definitions);
    }

    private static StorageManifest SharedScaleBearingManifest()
    {
        var binding = new SharedStorageBinding("runtime-documents");
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-category",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing);
        return WithStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default(binding),
                [logicalIndex],
                [query]));
    }

    private static StorageManifest ManifestFor(PhysicalStorageForm form, StorageScopePolicy scopePolicy)
    {
        var binding = new SharedStorageBinding("runtime-documents");
        var template = SampleManifests.MetadataManifest();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = scopePolicy == StorageScopePolicy.Scoped
                        ? TenancyPolicy.Scoped
                        : TenancyPolicy.Global
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => null,
            PhysicalStorageForm.DedicatedDocumentTable =>
                PhysicalTableDefinition.DedicatedDocumentTable("configurationDocument"),
            PhysicalStorageForm.PhysicalEntityTable =>
                PhysicalTableDefinition.PhysicalEntityTable(
                    "configurationDocument",
                    [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var storage = definition is null
            ? new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default(binding))
            : new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition));
        return WithStorage(manifest, storage);
    }

    private static StorageManifest WithStorage(StorageManifest manifest, StorageUnitPhysicalStorage storage) =>
        manifest with
        {
            StorageUnits = [manifest.StorageUnits.Single() with { PhysicalStorage = storage }]
        };
}
