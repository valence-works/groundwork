using Groundwork.Core.Manifests;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalTableDefinitionTests
{
    [Fact]
    public void Declared_string_length_has_a_provider_neutral_structured_failure()
    {
        var definition = new ProjectedColumnDefinition("label", "label", PortablePhysicalType.String, Length: 128);

        PhysicalProjectionValueValidation.ValidateStringLength(new string('a', 128), definition);
        var exception = Assert.Throws<PhysicalProjectionValueValidationException>(() =>
            PhysicalProjectionValueValidation.ValidateStringLength(new string('a', 129), definition));

        Assert.Equal("GW-PHYSICAL-037", exception.Diagnostic.Code);
        Assert.Equal("projectedColumns.label", exception.Diagnostic.Target);
    }

    [Fact]
    public void Declared_string_length_counts_utf16_code_units()
    {
        var definition = new ProjectedColumnDefinition("label", "label", PortablePhysicalType.String, Length: 128);

        PhysicalProjectionValueValidation.ValidateStringLength(string.Concat(Enumerable.Repeat("😀", 64)), definition);
        Assert.Throws<PhysicalProjectionValueValidationException>(() =>
            PhysicalProjectionValueValidation.ValidateStringLength(string.Concat(Enumerable.Repeat("😀", 65)), definition));
    }

    [Fact]
    public void PhysicalStorageFormContainsExactlyTheThreeRatifiedForms()
    {
        Assert.Equal(
            [
                PhysicalStorageForm.SharedDocuments,
                PhysicalStorageForm.DedicatedDocumentTable,
                PhysicalStorageForm.PhysicalEntityTable
            ],
            Enum.GetValues<PhysicalStorageForm>());
    }

    [Fact]
    public void SeparatelyConstructedDefinitionsHaveStructuralEquality()
    {
        var first = EntityDefinition();
        var second = EntityDefinition();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void PhysicalStorageDeclarationsUseOrderIndependentStructuralEquality()
    {
        var first = PhysicalStorageDeclaration(reverse: false);
        var second = PhysicalStorageDeclaration(reverse: true);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void IndependentResolutionResultsHaveStructuralEquality()
    {
        var declaration = PhysicalStorageDeclaration(reverse: false);
        var query = declaration.BoundedQueries.Single(x => x.Identity == "list-by-category");
        var scaleBearingDeclaration = new StorageUnitPhysicalStorage(
            declaration.ProvisioningMode,
            declaration.Policy,
            declaration.LogicalIndexes,
            [
                new BoundedQueryDeclaration(
                    query.Identity,
                    query.IndexIdentity,
                    query.Operations,
                    query.SortSupport,
                    query.PagingSupport,
                    BoundedQueryExecutionClass.ScaleBearing,
                    query.SupportsDisjunction,
                    query.SupportsTotalCount,
                    [new BoundedQuerySortField("category", PhysicalSortDirection.Descending)])
            ]);
        var manifest = SampleManifests.MetadataManifest();
        manifest = manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    PhysicalStorage = scaleBearingDeclaration
                }
            ]
        };

        var first = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        var second = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        var firstDefinition = Assert.Single(first.Definitions);
        var secondDefinition = Assert.Single(second.Definitions);
        Assert.Equal(firstDefinition.Resolved, secondDefinition.Resolved);
        Assert.Equal(
            Assert.Single(firstDefinition.Resolved.ScaleBearingDemand),
            Assert.Single(secondDefinition.Resolved.ScaleBearingDemand));
        Assert.Equal(firstDefinition, secondDefinition);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void CompoundIndexOrderMustBeUniqueAndContiguous()
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "orders",
            [
                new ProjectedColumnDefinition("customer", "customer.id", PortablePhysicalType.String),
                new ProjectedColumnDefinition("created", "createdAt", PortablePhysicalType.DateTime)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-customer-created",
                    [
                        new PhysicalIndexColumnDefinition("customer", 0),
                        new PhysicalIndexColumnDefinition("created", 0, PhysicalSortDirection.Descending)
                    ])
            ]);
        var manifest = WithDefinition(definition);

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-015");
    }

    [Fact]
    public void DedicatedDocumentTableCanOwnNamedLinkedProjectedIndexStorage()
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
        var equivalent = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            indexes: definition.Indexes,
            linkedProjectedColumns: definition.ProjectedColumns,
            linkedProjectionLogicalName: "configurationDocument_lookup");
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
        var manifest = WithDefinition(definition) with
        {
            StorageUnits =
            [
                WithDefinition(definition).StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition),
                        [logicalIndex],
                        [query])
                }
            ]
        };

        var result = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        var renamedDefinition = PhysicalTableDefinition.DedicatedDocumentTable(
            "configurationDocument",
            indexes: definition.Indexes,
            linkedProjectedColumns: definition.ProjectedColumns,
            linkedProjectionLogicalName: "configurationDocument_lookup_v2");
        var renamedManifest = manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(renamedDefinition),
                        [logicalIndex],
                        [query])
                }
            ]
        };
        var renamedResult = PhysicalStorageResolver.Resolve(
            renamedManifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Equal(definition, equivalent);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var resolved = Assert.Single(result.Definitions);
        Assert.Equal(PhysicalStorageForm.DedicatedDocumentTable, resolved.Definition.Form);
        Assert.Equal(
            "configurationDocument_lookup",
            resolved.Names.Single(x => x.ObjectKind == PhysicalObjectKind.LinkedIndexStorage).LogicalName);
        Assert.Contains("linkedProjectionLogicalName", PhysicalStorageDefinitionSerializer.Serialize(resolved));
        Assert.NotEqual(resolved.Fingerprint, Assert.Single(renamedResult.Definitions).Fingerprint);
    }

    private static PhysicalTableDefinition EntityDefinition() =>
        PhysicalTableDefinition.PhysicalEntityTable(
            "orders",
            [
                new ProjectedColumnDefinition(
                    "customer",
                    "customer.id",
                    PortablePhysicalType.String,
                    Length: 64,
                    IsNullable: false)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-customer",
                    [new PhysicalIndexColumnDefinition("customer", 0)],
                    isUnique: false)
            ],
            schemaVersion: 2,
            evolution: new PhysicalEvolutionMetadata(RequiresBackfill: true));

    private static StorageUnitPhysicalStorage PhysicalStorageDeclaration(bool reverse)
    {
        LogicalIndexDeclaration[] indexes =
        [
            new LogicalIndexDeclaration(
                "by-category",
                [new IndexField("category")],
                IndexValueKind.Keyword,
                false,
                MissingValueBehavior.Excluded),
            new LogicalIndexDeclaration(
                "by-key",
                [new IndexField("key")],
                IndexValueKind.Keyword,
                true,
                MissingValueBehavior.Excluded)
        ];
        BoundedQueryDeclaration[] queries =
        [
            new BoundedQueryDeclaration(
                "find-by-key",
                "by-key",
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None),
            new BoundedQueryDeclaration(
                "list-by-category",
                "by-category",
                new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.StartsWith,
                    PortableQueryOperation.Equal
                },
                QuerySortSupport.Both,
                QueryPagingSupport.Offset)
        ];

        return new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default(),
            reverse ? indexes.Reverse().ToArray() : indexes,
            reverse ? queries.Reverse().ToArray() : queries);
    }

    private static StorageManifest WithDefinition(PhysicalTableDefinition definition)
    {
        var manifest = SampleManifests.MetadataManifest();
        return manifest with
        {
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition))
                }
            ]
        };
    }
}
