using Groundwork.Core.Manifests;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Relational.Physicalization;
using System.Text.Json;
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
    public void Collection_element_projection_preserves_each_canonical_element_and_its_ordinal()
    {
        var projection = new ProjectedColumnDefinition(
            "redirect_uri",
            "redirectUris",
            PortablePhysicalType.String,
            Cardinality: ProjectionCardinality.CollectionElements,
            MaxCollectionElements: 4);

        var elements = CanonicalCollectionElementProjection.Read(
            "{\"redirectUris\":[\"https://one.example/callback\",\"https://two.example/callback\"]}",
            projection);

        Assert.Collection(
            elements,
            first =>
            {
                Assert.Equal(0, first.Ordinal);
                Assert.Equal(JsonValueKind.String, first.Value.ValueKind);
                Assert.Equal("https://one.example/callback", first.Value.GetString());
            },
            second =>
            {
                Assert.Equal(1, second.Ordinal);
                Assert.Equal(JsonValueKind.String, second.Value.ValueKind);
                Assert.Equal("https://two.example/callback", second.Value.GetString());
            });
    }

    [Fact]
    public void Collection_element_projection_fails_closed_for_non_array_and_nested_elements()
    {
        var projection = new ProjectedColumnDefinition(
            "scope",
            "scopes",
            PortablePhysicalType.String,
            Cardinality: ProjectionCardinality.CollectionElements,
            MaxCollectionElements: 2);

        Assert.Throws<InvalidDataException>(() => CanonicalCollectionElementProjection.Read(
            "{\"scopes\":\"read\"}", projection));
        Assert.Throws<InvalidDataException>(() => CanonicalCollectionElementProjection.Read(
            "{\"scopes\":[{\"name\":\"read\"}]}", projection));
        Assert.Throws<InvalidDataException>(() => CanonicalCollectionElementProjection.Read(
            "{\"scopes\":[\"read\",\"write\",\"admin\"]}", projection));
    }

    [Fact]
    public void Collection_cardinality_changes_the_physical_definition_fingerprint()
    {
        var scalar = PhysicalTableDefinition.PhysicalEntityTable(
            "applications",
            [new ProjectedColumnDefinition("redirect_uri", "redirectUris", PortablePhysicalType.String)]);
        var elements = PhysicalTableDefinition.PhysicalEntityTable(
            "applications",
            [new ProjectedColumnDefinition(
                "redirect_uri",
                "redirectUris",
                PortablePhysicalType.String,
                Cardinality: ProjectionCardinality.CollectionElements,
                MaxCollectionElements: 8)]);

        var scalarResult = PhysicalStorageResolver.Resolve(
            WithDefinition(scalar), PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);
        var elementsResult = PhysicalStorageResolver.Resolve(
            WithDefinition(elements), PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);

        Assert.True(scalarResult.IsValid);
        Assert.True(elementsResult.IsValid);
        Assert.NotEqual(
            Assert.Single(scalarResult.Definitions).Fingerprint,
            Assert.Single(elementsResult.Definitions).Fingerprint);

        var routes = ExecutableStorageRouteCompiler.Compile(elementsResult.Definitions);
        var route = Assert.Single(routes.Routes);
        var restored = ExecutableStorageRouteSerializer.Deserialize(
            ExecutableStorageRouteSerializer.Serialize(route));

        Assert.Equal(ProjectionCardinality.CollectionElements, Assert.Single(restored.ProjectedColumns).Definition.Cardinality);
        Assert.Equal(8, Assert.Single(restored.ProjectedColumns).Definition.MaxCollectionElements);
        var elementStorage = Assert.Single(restored.CollectionElementStorages);
        Assert.Equal(ExecutableStorageObjectRole.CollectionElementStorage, elementStorage.Storage.Role);
        Assert.Equal("redirect_uri__elements", elementStorage.Storage.Name.Identifier);
        Assert.Same(Assert.Single(restored.ProjectedColumns), elementStorage.Projection);
        Assert.Throws<NotSupportedException>(() =>
            RelationalPhysicalProjectionValues.Read(
                "{\"redirectUris\":[\"https://one.example/callback\"]}",
                restored.ProjectedColumns));

        var values = RelationalPhysicalProjectionValues.ReadCollection(
            "{\"redirectUris\":[\"https://one.example/callback\",\"https://two.example/callback\",\"https://one.example/callback\"]}",
            Assert.Single(restored.ProjectedColumns));

        Assert.Collection(
            values,
            first =>
            {
                Assert.Equal(0, first.Ordinal);
                Assert.Equal("https://one.example/callback", first.Value);
            },
            second =>
            {
                Assert.Equal(1, second.Ordinal);
                Assert.Equal("https://two.example/callback", second.Value);
            },
            third =>
            {
                Assert.Equal(2, third.Ordinal);
                Assert.Equal("https://one.example/callback", third.Value);
            });
    }

    [Theory]
    [InlineData("redirectUris..callback", PortablePhysicalType.String, null, null)]
    [InlineData("redirectUris", PortablePhysicalType.Json, null, null)]
    [InlineData("redirectUris", PortablePhysicalType.Int32, 10, null)]
    [InlineData("redirectUris", PortablePhysicalType.Boolean, null, "ordinal")]
    public void Collection_element_projection_rejects_non_portable_path_type_length_and_collation(
        string path,
        PortablePhysicalType type,
        int? length,
        string? collation)
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "applications",
            [new ProjectedColumnDefinition(
                "redirect_uri",
                path,
                type,
                Length: length,
                Collation: collation,
                Cardinality: ProjectionCardinality.CollectionElements,
                MaxCollectionElements: 8)]);

        var result = PhysicalStorageResolver.Resolve(
            WithDefinition(definition), PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-018");
    }

    [Theory]
    [InlineData("{\"redirectUris\":[null]}")]
    [InlineData("{\"redirectUris\":[42]}")]
    [InlineData("{\"redirectUris\":[\"one\",\"two\",\"three\",\"four\",\"five\",\"six\",\"seven\",\"eight\",\"nine\"]}")]
    public void Relational_collection_materialization_rejects_null_wrong_type_and_bound_overflow(string canonicalJson)
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "applications",
            [new ProjectedColumnDefinition(
                "redirect_uri",
                "redirectUris",
                PortablePhysicalType.String,
                Cardinality: ProjectionCardinality.CollectionElements,
                MaxCollectionElements: 8)]);
        var route = Assert.Single(ExecutableStorageRouteCompiler.Compile(
            PhysicalStorageResolver.Resolve(
                WithDefinition(definition), PhysicalNamePolicy.Identity, ProviderPhysicalNameNormalizer.Identity).Definitions).Routes);

        Assert.Throws<InvalidDataException>(() => RelationalPhysicalProjectionValues.ReadCollection(
            canonicalJson,
            Assert.Single(route.ProjectedColumns)));
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
