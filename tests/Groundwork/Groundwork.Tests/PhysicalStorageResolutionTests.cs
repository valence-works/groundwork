using Groundwork.Core.Intents;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalStorageResolutionTests
{
    [Fact]
    public void Resolver_rejects_non_string_identity_with_a_non_ordinal_string_case_policy()
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = WithPhysicalStorage(
            template with
            {
                StorageUnits =
                [
                    template.StorageUnits.Single() with
                    {
                        IdentityPolicy = new IdentityPolicy(
                            StorageIdentityKind.Guid,
                            "id",
                            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase)
                    }
                ]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-UNIT-013" &&
            diagnostic.Target == "storageUnits.configurationDocument.identityPolicy.stringCasePolicy");
    }

    [Fact]
    public void Resolver_reports_a_missing_identity_policy_structurally()
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = WithPhysicalStorage(
            template with
            {
                StorageUnits = [template.StorageUnits.Single() with { IdentityPolicy = null! }]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-UNIT-007" &&
            diagnostic.Target == "storageUnits.configurationDocument.identityPolicy");
    }

    [Fact]
    public void BoundedMutationMustReferenceOneScaleBearingPredicateDeclaration()
    {
        var index = new LogicalIndexDeclaration(
            "by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var ordinaryQuery = new BoundedQueryDeclaration(
            "by-status",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [index],
            [ordinaryQuery],
            boundedMutations:
            [
                new BoundedMutationDeclaration("revoke", ordinaryQuery.Identity,
                    BoundedMutationAction.Transition("status", ["active"], "revoked")),
                new BoundedMutationDeclaration("missing", "undeclared", BoundedMutationAction.Delete())
            ]);

        var result = PhysicalStorageResolver.Resolve(
            WithPhysicalStorage(SampleManifests.MetadataManifest(), storage),
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-032");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-033");
    }

    [Fact]
    public void BoundedTransitionPathMustBeAnExactlyMatchablePredicate()
    {
        var index = new LogicalIndexDeclaration(
            "by-status",
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "by-status",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [index],
            [query],
            boundedMutations:
            [
                new BoundedMutationDeclaration("revoke", query.Identity,
                    BoundedMutationAction.Transition("status", ["active"], "revoked"))
            ]);

        var result = PhysicalStorageResolver.Resolve(
            WithPhysicalStorage(SampleManifests.MetadataManifest(), storage),
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-034");
    }

    [Fact]
    public void UnsupportedTenancyDoesNotResolveOrFingerprintAsGlobal()
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = WithPhysicalStorage(template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = new TenancyPolicy(TenancyKind.CustomPartition)
                }
            ]
        }, new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default()));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-030");
    }

    [Fact]
    public void TenantIdIsAPayloadPathRatherThanAStorageScopeAlias()
    {
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-tenant-id",
                    [new IndexField("tenantId")],
                    IndexValueKind.Keyword,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "by-tenant-id",
                    "by-tenant-id",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), storage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var definition = Assert.Single(result.Definitions).Definition;
        Assert.Equal("tenantId", Assert.Single(definition.ProjectedColumns).Path);
        Assert.Collection(
            Assert.Single(definition.Indexes).Columns,
            column => Assert.Equal("storage_scope", column.ColumnLogicalName),
            column => Assert.Equal("tenantId", column.ColumnLogicalName));
    }

    [Fact]
    public void ScopePolicyParticipatesInResolvedDefinitionAndFingerprint()
    {
        var template = SampleManifests.MetadataManifest();
        var global = WithPhysicalStorage(template with
        {
            StorageUnits = [template.StorageUnits.Single() with { Tenancy = TenancyPolicy.Global }]
        }, new StorageUnitPhysicalStorage(StorageUnitProvisioningMode.Declared, PhysicalStoragePolicy.Default()));
        var scoped = global with
        {
            StorageUnits =
            [
                global.StorageUnits.Single() with { Tenancy = TenancyPolicy.Scoped }
            ]
        };

        var globalDefinition = Assert.Single(PhysicalStorageResolver.Resolve(
            global, PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity).Definitions);
        var scopedDefinition = Assert.Single(PhysicalStorageResolver.Resolve(
            scoped, PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity).Definitions);

        Assert.Equal(StorageScopePolicy.Global, globalDefinition.Resolved.ScopePolicy);
        Assert.Equal(StorageScopePolicy.Scoped, scopedDefinition.Resolved.ScopePolicy);
        Assert.NotEqual(globalDefinition.Fingerprint, scopedDefinition.Fingerprint);
    }

    [Fact]
    public void DeclaredDefaultWithoutScaleBearingDemandResolvesToDedicatedDocumentTable()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        var definition = Assert.Single(result.Definitions);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal(PhysicalStorageForm.DedicatedDocumentTable, definition.Definition.Form);
        Assert.Equal("configurationDocument", definition.PrimaryName.LogicalName);
        Assert.Equal("configurationDocument", definition.PrimaryName.Identifier);
    }

    [Fact]
    public void DeclaredDefaultEnvelopeOnlyDemandSynthesizesDedicatedPhysicalIndex()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-document-kind",
                    [new IndexField("documentKind")],
                    IndexValueKind.Keyword,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "list-by-document-kind",
                    "by-document-kind",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var definition = Assert.Single(result.Definitions).Definition;
        Assert.Equal(PhysicalStorageForm.DedicatedDocumentTable, definition.Form);
        Assert.Empty(definition.ProjectedColumns);
        var index = Assert.Single(definition.Indexes);
        Assert.Equal("by-document-kind", index.LogicalName);
        Assert.Collection(
            index.Columns,
            column => Assert.Equal("storage_scope", column.ColumnLogicalName),
            column => Assert.Equal("document_kind", column.ColumnLogicalName));
    }

    [Fact]
    public void WorkloadDescriptorDoesNotAffectDefaultResolutionOrFingerprint()
    {
        var original = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));
        var changedDescriptor = original with
        {
            StorageUnits =
            [
                original.StorageUnits.Single() with
                {
                    Intent = StorageIntent.Operational(
                        "Descriptor is diagnostic metadata only.",
                        WorkloadIntent.OperationalStream)
                }
            ]
        };

        var originalResult = PhysicalStorageResolver.Resolve(
            original,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        var changedResult = PhysicalStorageResolver.Resolve(
            changedDescriptor,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Equal(
            Assert.Single(originalResult.Definitions).Fingerprint,
            Assert.Single(changedResult.Definitions).Fingerprint);
    }

    [Fact]
    public void DeclaredDefaultWithScaleBearingNonEnvelopeDemandResolvesToEntityTable()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-category",
                    [new IndexField("category")],
                    IndexValueKind.Keyword,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "list-by-category",
                    "by-category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Ascending,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        var definition = Assert.Single(result.Definitions);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal(PhysicalStorageForm.PhysicalEntityTable, definition.Definition.Form);
        var projected = Assert.Single(definition.Definition.ProjectedColumns);
        Assert.Equal("category", projected.Path);
        Assert.Equal(PortablePhysicalType.String, projected.Type);
    }

    [Fact]
    public void DeclaredDefaultCannotInventDecimalPrecisionAndScaleForNumericDemand()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-value",
                    [new IndexField("value")],
                    IndexValueKind.Number,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "list-by-value",
                    "by-value",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-018");
    }

    [Fact]
    public void ScaleBearingCompoundDemandSynthesizesOrderedPhysicalIndex()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-customer-created",
                    [new IndexField("customerId"), new IndexField("createdAt")],
                    IndexValueKind.Keyword,
                    true,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "latest-by-customer",
                    "by-customer-created",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Descending,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        var definition = Assert.Single(result.Definitions).Definition;
        var index = Assert.Single(definition.Indexes);
        Assert.Equal("by-customer-created", index.LogicalName);
        Assert.True(index.IsUnique);
        Assert.Collection(
            index.Columns,
            column =>
            {
                Assert.Equal("storage_scope", column.ColumnLogicalName);
                Assert.Equal(0, column.Order);
                Assert.Equal(PhysicalSortDirection.Ascending, column.Direction);
            },
            column =>
            {
                Assert.Equal("customerId", column.ColumnLogicalName);
                Assert.Equal(1, column.Order);
                Assert.Equal(PhysicalSortDirection.Descending, column.Direction);
            },
            column =>
            {
                Assert.Equal("createdAt", column.ColumnLogicalName);
                Assert.Equal(2, column.Order);
                Assert.Equal(PhysicalSortDirection.Descending, column.Direction);
            });
    }

    [Fact]
    public void ScaleBearingCompoundDemandSynthesizesMixedSortDirections()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-customer-created",
                    [new IndexField("customerId"), new IndexField("createdAt")],
                    IndexValueKind.Keyword,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "latest-by-customer",
                    "by-customer-created",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Both,
                    QueryPagingSupport.Offset,
                    BoundedQueryExecutionClass.ScaleBearing,
                    sortFields:
                    [
                        new BoundedQuerySortField("customerId", PhysicalSortDirection.Ascending),
                        new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
                    ])
            ]);
        var manifest = WithPhysicalStorage(SampleManifests.MetadataManifest(), physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var columns = Assert.Single(Assert.Single(result.Definitions).Definition.Indexes).Columns;
        Assert.Collection(
            columns,
            column => Assert.Equal(PhysicalSortDirection.Ascending, column.Direction),
            column => Assert.Equal(PhysicalSortDirection.Ascending, column.Direction),
            column => Assert.Equal(PhysicalSortDirection.Descending, column.Direction));
    }

    [Fact]
    public void CompatibleReverseSortDemandsResolveIndependentlyOfDeclarationOrder()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-customer-created",
            [new IndexField("customerId"), new IndexField("createdAt")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var forward = Query(
            "forward",
            PhysicalSortDirection.Ascending,
            PhysicalSortDirection.Descending);
        var reverse = Query(
            "reverse",
            PhysicalSortDirection.Descending,
            PhysicalSortDirection.Ascending);

        var first = Resolve([forward, reverse]);
        var second = Resolve([reverse, forward]);

        Assert.True(first.IsValid, string.Join("; ", first.Diagnostics.Select(x => x.Message)));
        Assert.True(second.IsValid, string.Join("; ", second.Diagnostics.Select(x => x.Message)));
        Assert.Equal(Assert.Single(first.Definitions), Assert.Single(second.Definitions));
        Assert.Equal(
            Assert.Single(first.Definitions).Fingerprint,
            Assert.Single(second.Definitions).Fingerprint);

        BoundedQueryDeclaration Query(
            string identity,
            PhysicalSortDirection firstDirection,
            PhysicalSortDirection secondDirection) =>
            new(
                identity,
                logicalIndex.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.Both,
                QueryPagingSupport.Offset,
                BoundedQueryExecutionClass.ScaleBearing,
                sortFields:
                [
                    new BoundedQuerySortField("customerId", firstDirection),
                    new BoundedQuerySortField("createdAt", secondDirection)
                ]);

        PhysicalStorageResolutionResult Resolve(IReadOnlyList<BoundedQueryDeclaration> queries)
        {
            var manifest = WithPhysicalStorage(
                SampleManifests.MetadataManifest(),
                new StorageUnitPhysicalStorage(
                    StorageUnitProvisioningMode.Declared,
                    PhysicalStoragePolicy.Default(),
                    [logicalIndex],
                    queries));
            return PhysicalStorageResolver.Resolve(
                manifest,
                PhysicalNamePolicy.Identity,
                ProviderPhysicalNameNormalizer.Identity);
        }
    }

    [Fact]
    public void ScopedUniqueDemandIncludesScopeEnvelopeColumnInPhysicalIndex()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            [
                new LogicalIndexDeclaration(
                    "by-customer",
                    [new IndexField("customerId")],
                    IndexValueKind.Keyword,
                    true,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "find-by-customer",
                    "by-customer",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.None,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var template = SampleManifests.MetadataManifest();
        var tenantManifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
        var manifest = WithPhysicalStorage(tenantManifest, physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var index = Assert.Single(Assert.Single(result.Definitions).Definition.Indexes);
        Assert.True(index.IsUnique);
        Assert.Collection(
            index.Columns,
            column =>
            {
                Assert.Equal("storage_scope", column.ColumnLogicalName);
                Assert.Equal(0, column.Order);
            },
            column =>
            {
                Assert.Equal("customerId", column.ColumnLogicalName);
                Assert.Equal(1, column.Order);
            });
    }

    [Fact]
    public void DynamicDefaultUsesManifestOwnedSharedNameAndEnvelope()
    {
        var binding = new SharedStorageBinding("application-documents");
        var envelope = new DocumentEnvelopeDefinition(CanonicalJsonColumn: "payload");
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "groundwork_documents", envelope)]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default(binding)));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        var definition = Assert.Single(result.Definitions);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal(PhysicalStorageForm.SharedDocuments, definition.Definition.Form);
        Assert.Equal("groundwork_documents", definition.PrimaryName.Identifier);
        Assert.Null(definition.Definition.Envelope);
        Assert.Equal("payload", manifest.SharedDocumentStorages.Single().Envelope.CanonicalJsonColumn);
    }

    [Fact]
    public void DynamicDefaultCursorDemandSynthesizesLinkedProjectionAndIdentityTieBreakIndex()
    {
        var binding = new SharedStorageBinding("application-documents");
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Dynamic,
            PhysicalStoragePolicy.Default(binding),
            [
                new LogicalIndexDeclaration(
                    "by-category",
                    [new IndexField("category")],
                    IndexValueKind.Keyword,
                    false,
                    MissingValueBehavior.Excluded)
            ],
            [
                new BoundedQueryDeclaration(
                    "list-by-category",
                    "by-category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.Ascending,
                    QueryPagingSupport.Cursor,
                    BoundedQueryExecutionClass.ScaleBearing)
            ]);
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "groundwork_documents", new DocumentEnvelopeDefinition())]
            },
            physicalStorage);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var definition = Assert.Single(result.Definitions).Definition;
        Assert.Equal(PhysicalStorageForm.SharedDocuments, definition.Form);
        Assert.Equal("configurationDocument_projection", definition.LinkedProjectionLogicalName);
        Assert.Equal("category", Assert.Single(definition.ProjectedColumns).Path);
        var physicalIndex = Assert.Single(definition.Indexes);
        Assert.Equal("by-category", physicalIndex.LogicalName);
        Assert.Collection(
            physicalIndex.Columns,
            column => Assert.Equal("storage_scope", column.ColumnLogicalName),
            column => Assert.Equal("category", column.ColumnLogicalName),
            column => Assert.Equal("id_lookup_key", column.ColumnLogicalName));
    }

    [Fact]
    public void SharedPrimaryNameResolvesOnceFromBindingOwnership()
    {
        var binding = new SharedStorageBinding("documents");
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Dynamic,
            PhysicalStoragePolicy.Default(binding));
        var manifest = SampleManifests.MetadataManifest() with
        {
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())],
            StorageUnits =
            [
                template with
                {
                    Identity = new StorageUnitIdentity("firstDocument"),
                    PhysicalStorage = storage
                },
                template with
                {
                    Identity = new StorageUnitIdentity("secondDocument"),
                    PhysicalStorage = storage
                }
            ]
        };
        var hostInvocations = 0;
        var providerInvocations = 0;
        var hostPolicy = new DelegatePhysicalNamePolicy(context =>
        {
            if (context.ObjectKind == PhysicalObjectKind.PrimaryStorage)
                hostInvocations++;
            return $"{context.StorageUnit.Value}_{context.FeatureDefaultLogicalName}";
        });
        var providerPolicy = new DelegateProviderPhysicalNameNormalizer(context =>
        {
            if (context.ObjectKind == PhysicalObjectKind.PrimaryStorage)
                providerInvocations++;
            return context.LogicalName;
        });

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            hostPolicy,
            providerPolicy);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal(2, result.Definitions.Count);
        Assert.All(result.Definitions, definition =>
            Assert.Equal("shared:documents_documents", definition.PrimaryName.LogicalName));
        Assert.Equal(1, hostInvocations);
        Assert.Equal(1, providerInvocations);
    }

    [Fact]
    public void UnitOverrideCannotRenameManifestOwnedSharedPrimary()
    {
        var binding = new SharedStorageBinding("documents");
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default(binding),
                nameOverrides:
                [
                    new PhysicalObjectNameOverride(
                        PhysicalObjectKind.PrimaryStorage,
                        "documents",
                        "unit_documents")
                ]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-022");
        Assert.Equal("documents", Assert.Single(result.Definitions).PrimaryName.LogicalName);
    }

    [Fact]
    public void NamingPipelineAppliesHostThenUnitOverrideThenProviderNormalization()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default(),
                nameOverrides:
                [
                    new PhysicalObjectNameOverride(
                        PhysicalObjectKind.PrimaryStorage,
                        "configurationDocument",
                        "unit_documents")
                ]));
        var hostPolicy = new DelegatePhysicalNamePolicy(
            context => $"host_{context.FeatureDefaultLogicalName}");
        var providerPolicy = new DelegateProviderPhysicalNameNormalizer(
            context => context.LogicalName.ToUpperInvariant());

        var result = PhysicalStorageResolver.Resolve(manifest, hostPolicy, providerPolicy);

        var name = Assert.Single(result.Definitions).PrimaryName;
        Assert.Equal("configurationDocument", name.FeatureDefaultLogicalName);
        Assert.Equal("unit_documents", name.LogicalName);
        Assert.Equal("UNIT_DOCUMENTS", name.Identifier);
    }

    [Fact]
    public void ProviderNormalizationRejectsCollidingLogicalNames()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default());
        var manifest = SampleManifests.MetadataManifest() with
        {
            StorageUnits =
            [
                template with
                {
                    Identity = new StorageUnitIdentity("firstDocument"),
                    PhysicalStorage = storage
                },
                template with
                {
                    Identity = new StorageUnitIdentity("secondDocument"),
                    PhysicalStorage = storage
                }
            ]
        };
        var normalizer = new DelegateProviderPhysicalNameNormalizer(_ => "same_identifier");

        var result = PhysicalStorageResolver.Resolve(manifest, PhysicalNamePolicy.Identity, normalizer);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-011");
    }

    [Fact]
    public void HostPolicyCannotCollapseDistinctPhysicalObjectsToOneLogicalName()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default());
        var manifest = SampleManifests.MetadataManifest() with
        {
            StorageUnits =
            [
                template with
                {
                    Identity = new StorageUnitIdentity("firstDocument"),
                    PhysicalStorage = storage
                },
                template with
                {
                    Identity = new StorageUnitIdentity("secondDocument"),
                    PhysicalStorage = storage
                }
            ]
        };
        var hostPolicy = new DelegatePhysicalNamePolicy(_ => "same_name");

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            hostPolicy,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-011");
    }

    [Fact]
    public void FailedHostPrimaryNameDoesNotPromoteDedicatedLinkedStorageToPrimary()
    {
        var manifest = DedicatedWithLinkedStorage();
        var hostPolicy = new DelegatePhysicalNamePolicy(context =>
            context.ObjectKind == PhysicalObjectKind.PrimaryStorage
                ? " "
                : context.FeatureDefaultLogicalName);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            hostPolicy,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-009");
    }

    [Fact]
    public void FailedProviderPrimaryNameDoesNotPromoteDedicatedLinkedStorageToPrimary()
    {
        var manifest = DedicatedWithLinkedStorage();
        var providerNormalizer = new DelegateProviderPhysicalNameNormalizer(context =>
            context.ObjectKind == PhysicalObjectKind.PrimaryStorage
                ? " "
                : context.LogicalName);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            providerNormalizer);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-010");
    }

    [Fact]
    public void ProviderNormalizationScopesColumnCollisionsToTheirOwningTable()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "ignored",
            [new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)]);
        var manifest = SampleManifests.MetadataManifest() with
        {
            StorageUnits =
            [
                template with
                {
                    Identity = new StorageUnitIdentity("firstDocument"),
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                            "first_documents",
                            definition.ProjectedColumns)))
                },
                template with
                {
                    Identity = new StorageUnitIdentity("secondDocument"),
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                            "second_documents",
                            definition.ProjectedColumns)))
                }
            ]
        };
        var hostPolicy = new DelegatePhysicalNamePolicy(context =>
            context.ObjectKind == PhysicalObjectKind.ProjectedField
                ? $"{context.StorageUnit.Value}_{context.FeatureDefaultLogicalName}"
                : context.FeatureDefaultLogicalName);
        var normalizer = new DelegateProviderPhysicalNameNormalizer(context =>
            context.ObjectKind == PhysicalObjectKind.ProjectedField
                ? "status"
                : context.LogicalName);

        var result = PhysicalStorageResolver.Resolve(manifest, hostPolicy, normalizer);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
    }

    [Fact]
    public void DynamicDefaultWithoutSharedBindingFailsValidation()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default()));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-002");
    }

    [Fact]
    public void OrdinaryBoundedQueryMustReferenceExactlyOneLogicalIndex()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default(),
                logicalIndexes: [],
                boundedQueries:
                [
                    new BoundedQueryDeclaration(
                        "find-missing",
                        "missing-index",
                        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                        QuerySortSupport.None,
                        QueryPagingSupport.None)
                ]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-020");
    }

    [Fact]
    public void DeclaredDefaultWithSharedBindingFailsValidation()
    {
        var binding = new SharedStorageBinding("documents");
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default(binding)));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-003");
    }

    [Fact]
    public void ConflictingSharedDefinitionsForOneBindingFailValidation()
    {
        var binding = new SharedStorageBinding("documents");
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [
                    new SharedDocumentStorageDefinition(binding, "documents_a", new DocumentEnvelopeDefinition()),
                    new SharedDocumentStorageDefinition(binding, "documents_b", new DocumentEnvelopeDefinition())
                ]
            },
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Dynamic,
                PhysicalStoragePolicy.Default(binding)));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-012");
    }

    [Fact]
    public void SharedPrimaryEnvelopeParticipatesInFingerprint()
    {
        var binding = new SharedStorageBinding("documents");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Dynamic,
            PhysicalStoragePolicy.Default(binding));
        var first = WithPhysicalStorage(
            SampleManifests.MetadataManifest() with
            {
                SharedDocumentStorages =
                [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
            },
            storage);
        var second = first with
        {
            SharedDocumentStorages =
            [
                new SharedDocumentStorageDefinition(
                    binding,
                    "documents",
                    new DocumentEnvelopeDefinition(CanonicalJsonColumn: "payload"))
            ]
        };

        var firstDefinition = Assert.Single(PhysicalStorageResolver.Resolve(
            first,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions);
        var secondDefinition = Assert.Single(PhysicalStorageResolver.Resolve(
            second,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions);

        Assert.NotEqual(firstDefinition.Fingerprint, secondDefinition.Fingerprint);
    }

    [Fact]
    public void ProviderDefinitionHasDeterministicCanonicalSerialization()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));
        var definition = Assert.Single(PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions);

        var snapshot = PhysicalStorageDefinitionSerializer.Serialize(definition);

        Assert.Equal(
            "{\"storageUnit\":\"configurationDocument\",\"provisioningMode\":\"Declared\",\"scopePolicy\":\"Scoped\",\"identityPolicy\":{\"kind\":\"String\",\"fieldName\":\"id\",\"stringCasePolicy\":\"Ordinal\",\"comparisonAlgorithm\":\"groundwork-utf16-hex-v1\",\"lookupAlgorithm\":\"groundwork-sha256-utf8-lowerhex-v1\"},\"definition\":{\"form\":\"DedicatedDocumentTable\",\"featureDefaultLogicalName\":\"configurationDocument\",\"sharedStorage\":null,\"schemaVersion\":1,\"envelope\":{\"id\":\"id\",\"idComparisonKey\":\"id_comparison_key\",\"idLookupKey\":\"id_lookup_key\",\"documentKind\":\"document_kind\",\"storageScope\":\"storage_scope\",\"version\":\"version\",\"schemaVersion\":\"schema_version\",\"canonicalJson\":\"document\"},\"projectedColumns\":[],\"indexes\":[]},\"scaleBearingDemand\":[],\"names\":[{\"kind\":\"PrimaryStorage\",\"featureDefault\":\"configurationDocument\",\"logical\":\"configurationDocument\",\"identifier\":\"configurationDocument\",\"collisionScope\":\"primary-storage\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"document\",\"logical\":\"document\",\"identifier\":\"document\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"document_kind\",\"logical\":\"document_kind\",\"identifier\":\"document_kind\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"id\",\"logical\":\"id\",\"identifier\":\"id\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"id_comparison_key\",\"logical\":\"id_comparison_key\",\"identifier\":\"id_comparison_key\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"id_lookup_key\",\"logical\":\"id_lookup_key\",\"identifier\":\"id_lookup_key\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"schema_version\",\"logical\":\"schema_version\",\"identifier\":\"schema_version\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"storage_scope\",\"logical\":\"storage_scope\",\"identifier\":\"storage_scope\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"},{\"kind\":\"EnvelopeField\",\"featureDefault\":\"version\",\"logical\":\"version\",\"identifier\":\"version\",\"collisionScope\":\"configurationDocument:columns\",\"namingOwner\":\"configurationDocument\"}]}",
            snapshot);
    }

    [Fact]
    public void EvolutionMetadataParticipatesInFingerprint()
    {
        var baselineDefinition = PhysicalTableDefinition.DedicatedDocumentTable("configurationDocument");
        var changedDefinition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            evolution: new PhysicalEvolutionMetadata(
                RequiresBackfill: true,
                SemanticMigrationIdentity: "configuration-v2"));
        var baseline = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(baselineDefinition)));
        var changed = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(changedDefinition)));

        var baselineFingerprint = Assert.Single(PhysicalStorageResolver.Resolve(
            baseline,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions).Fingerprint;
        var changedFingerprint = Assert.Single(PhysicalStorageResolver.Resolve(
            changed,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions).Fingerprint;

        Assert.NotEqual(baselineFingerprint, changedFingerprint);
    }

    [Fact]
    public void ScaleBearingDemandParticipatesInFingerprintEvenWhenDefinitionIsExplicit()
    {
        var index = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-category",
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("category", 1)
                    ])
            ]);
        var ordinary = new BoundedQueryDeclaration(
            "list-by-category",
            "by-category",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.Ordinary);
        var scaleBearing = new BoundedQueryDeclaration(
            ordinary.Identity,
            ordinary.IndexIdentity,
            ordinary.Operations,
            ordinary.SortSupport,
            ordinary.PagingSupport,
            BoundedQueryExecutionClass.ScaleBearing,
            ordinary.SupportsDisjunction,
            ordinary.SupportsTotalCount);
        var baseline = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [ordinary]));
        var changed = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [scaleBearing]));

        var baselineFingerprint = Assert.Single(PhysicalStorageResolver.Resolve(
            baseline,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions).Fingerprint;
        var changedFingerprint = Assert.Single(PhysicalStorageResolver.Resolve(
            changed,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions).Fingerprint;

        Assert.NotEqual(baselineFingerprint, changedFingerprint);
    }

    [Fact]
    public void ExplicitScaleBearingDemandRequiresMatchingOrderedPhysicalIndex()
    {
        var index = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category"), new IndexField("createdAt")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-category",
            "by-category",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [
                new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String),
                new ProjectedColumnDefinition("createdAt", "createdAt", PortablePhysicalType.DateTime)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-category",
                    [
                        new PhysicalIndexColumnDefinition("createdAt", 0),
                        new PhysicalIndexColumnDefinition("category", 1)
                    ])
            ]);
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [query]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ExplicitScaleBearingExactIdentityDemandAcceptsLookupLeadingComparisonEvidence()
    {
        var result = ResolveExplicitIdentityIndex(
            [PortableQueryOperation.Equal],
            ["id_lookup_key", "id_comparison_key"]);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Single(result.Definitions);
    }

    [Fact]
    public void ExplicitScaleBearingOrderedIdentityDemandAcceptsComparisonEvidenceOnly()
    {
        var result = ResolveExplicitIdentityIndex(
            [PortableQueryOperation.GreaterThan, PortableQueryOperation.StartsWith],
            ["id_comparison_key"]);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Single(result.Definitions);
    }

    [Fact]
    public void ExplicitScaleBearingIdentityDemandRejectsRawOriginalIdentityEvidence()
    {
        var result = ResolveExplicitIdentityIndex(
            [PortableQueryOperation.Equal],
            ["id"]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ExplicitScaleBearingMixedIdentityDemandReportsUnsupportedEvidenceShape()
    {
        var result = ResolveExplicitIdentityIndex(
            [PortableQueryOperation.Equal, PortableQueryOperation.GreaterThan],
            ["id_lookup_key", "id_comparison_key"]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "GW-PHYSICAL-035");
        Assert.Contains("mixed exact and ordered document-identity demand", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ExplicitScaleBearingDemandRejectsMismatchedPhysicalIndexDirections()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-customer-created",
            [new IndexField("customerId"), new IndexField("createdAt")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-customer",
            "by-customer-created",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("customerId", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ]);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [
                new ProjectedColumnDefinition("customerId", "customerId", PortablePhysicalType.String),
                new ProjectedColumnDefinition("createdAt", "createdAt", PortablePhysicalType.DateTime)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-customer-created",
                    [
                        new PhysicalIndexColumnDefinition("customerId", 0, PhysicalSortDirection.Ascending),
                        new PhysicalIndexColumnDefinition("createdAt", 1, PhysicalSortDirection.Ascending)
                    ])
            ]);
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ExplicitCursorIndexMustIncludeTheIdentityLookupTieBreak()
    {
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
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("category", 1)
                    ])
            ]);
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ExplicitScopedUniqueIndexRequiresScopeEnvelopeColumn()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-customer",
            [new IndexField("customerId")],
            IndexValueKind.Keyword,
            true,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-customer",
            "by-customer",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("customerId", "customerId", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-customer",
                    [new PhysicalIndexColumnDefinition("customerId", 0)],
                    isUnique: true)
            ]);
        var template = SampleManifests.MetadataManifest();
        var tenantManifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
        var manifest = WithPhysicalStorage(
            tenantManifest,
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-025");
    }

    [Fact]
    public void ScopedExplicitUniqueIndexCannotOmitScopeEnvelopeColumn()
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("customerId", "customerId", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "unique-customer",
                    [new PhysicalIndexColumnDefinition("customerId", 0)],
                    isUnique: true)
            ]);
        var template = SampleManifests.MetadataManifest();
        var tenantManifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
        var manifest = WithPhysicalStorage(
            tenantManifest,
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-026");
    }

    [Fact]
    public void ExplicitScopedUniqueIndexUsesConfiguredScopeEnvelopeColumn()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-customer",
            [new IndexField("customerId")],
            IndexValueKind.Keyword,
            true,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-customer",
            "by-customer",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("customerId", "customerId", PortablePhysicalType.String)],
            envelope: new DocumentEnvelopeDefinition(StorageScopeColumn: "tenant_scope"),
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-customer",
                    [
                        new PhysicalIndexColumnDefinition("tenant_scope", 0),
                        new PhysicalIndexColumnDefinition("customerId", 1)
                    ],
                    isUnique: true)
            ]);
        var template = SampleManifests.MetadataManifest();
        var tenantManifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped
                }
            ]
        };
        var manifest = WithPhysicalStorage(
            tenantManifest,
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query]));

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Single(result.Definitions);
    }

    [Fact]
    public void HostAndProviderNamesParticipateInFingerprint()
    {
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Default()));
        var baseline = Assert.Single(PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity).Definitions);
        var renamed = Assert.Single(PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"host_{context.FeatureDefaultLogicalName}"),
            new DelegateProviderPhysicalNameNormalizer(context => $"provider_{context.LogicalName}")).Definitions);

        Assert.NotEqual(baseline.Fingerprint, renamed.Fingerprint);
    }

    [Fact]
    public void ExplicitDefinitionRejectsIndexesThatReferenceUnknownColumns()
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [
                new ProjectedColumnDefinition(
                    "category",
                    "category",
                    PortablePhysicalType.String)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-missing",
                    [new PhysicalIndexColumnDefinition("missing", 0)])
            ]);
        var manifest = WithPhysicalStorage(
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
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-014");
    }

    private static StorageManifest WithPhysicalStorage(
        StorageManifest manifest,
        StorageUnitPhysicalStorage physicalStorage) =>
        manifest with
        {
            StorageUnits = [manifest.StorageUnits.Single() with { PhysicalStorage = physicalStorage }]
        };

    private static PhysicalStorageResolutionResult ResolveExplicitIdentityIndex(
        IReadOnlyList<PortableQueryOperation> operations,
        IReadOnlyList<string> physicalColumns)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-id",
            logicalIndex.Identity,
            operations.ToHashSet(),
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "configurationDocument",
            [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    new[] { "storage_scope" }
                        .Concat(physicalColumns)
                        .Select((column, order) => new PhysicalIndexColumnDefinition(column, order))
                        .ToArray())
            ]);
        var manifest = WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query]));

        return PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
    }

    private static StorageManifest DedicatedWithLinkedStorage()
    {
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-category",
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("category", 1)
                    ])
            ],
            linkedProjectedColumns:
            [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
            linkedProjectionLogicalName: "configurationDocument_lookup");
        return WithPhysicalStorage(
            SampleManifests.MetadataManifest(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition)));
    }
}
