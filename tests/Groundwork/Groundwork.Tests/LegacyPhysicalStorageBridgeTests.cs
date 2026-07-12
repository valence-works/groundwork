using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Xunit;

namespace Groundwork.Tests;

public sealed class LegacyPhysicalStorageBridgeTests
{
    [Fact]
    public void OptimizedMapsToSharedDocumentsWithLinkedProjectionNeverEntityStorage()
    {
        var unit = SampleManifests.MetadataManifest().StorageUnits.Single() with
        {
            Physicalization = PhysicalizationPolicy.Optimized
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var policy = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(result.PhysicalStorage!.Policy);
        Assert.Equal(PhysicalStorageForm.SharedDocuments, policy.Definition.Form);
        Assert.Equal("configurationDocument_projection", policy.Definition.LinkedProjectionLogicalName);
        Assert.NotEmpty(policy.Definition.ProjectedColumns);
    }

    [Fact]
    public void SpecializedRequiresAnExplicitAdapter()
    {
        var unit = SampleManifests.MetadataManifest().StorageUnits.Single() with
        {
            Physicalization = PhysicalizationPolicy.Specialized
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.False(result.IsValid);
        Assert.Null(result.PhysicalStorage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-LEGACY-001");
    }

    [Fact]
    public void ExtraLegacyIndexOperationsDoNotCreateImplicitQueryCapabilities()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var query = template.Queries.Single(x => x.Identity == "find-by-key");
        var unit = template with
        {
            Queries =
            [
                query with
                {
                    Operations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }
                }
            ]
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.True(result.IsValid);
        Assert.Equal(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            result.PhysicalStorage!.BoundedQueries.Single().Operations);
    }

    [Fact]
    public void PortableMapsToSharedDocumentsWithoutLinkedProjection()
    {
        var unit = SampleManifests.MetadataManifest().StorageUnits.Single() with
        {
            Physicalization = PhysicalizationPolicy.Portable
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        var policy = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(result.PhysicalStorage!.Policy);
        Assert.Equal(PhysicalStorageForm.SharedDocuments, policy.Definition.Form);
        Assert.Null(policy.Definition.LinkedProjectionLogicalName);
        Assert.Empty(policy.Definition.ProjectedColumns);
    }

    [Fact]
    public void PortableUnitPreservesPerIndexOptimizedLinkedProjection()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var unit = template with
        {
            Physicalization = PhysicalizationPolicy.Portable,
            Indexes = template.Indexes
                .Select(index => index.Identity == "by-key"
                    ? index with { Physicalization = IndexPhysicalizationPolicy.Optimized }
                    : index)
                .ToArray()
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var policy = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(result.PhysicalStorage!.Policy);
        var projected = Assert.Single(policy.Definition.ProjectedColumns);
        Assert.Equal("by-key", projected.LogicalName);
        Assert.Equal("key", projected.Path);
        Assert.Equal("configurationDocument_projection", policy.Definition.LinkedProjectionLogicalName);
        var physicalIndex = Assert.Single(policy.Definition.Indexes);
        Assert.True(physicalIndex.IsUnique);
        Assert.Collection(
            physicalIndex.Columns,
            column => Assert.Equal("storage_scope", column.ColumnLogicalName),
            column => Assert.Equal("by-key", column.ColumnLogicalName));
    }

    [Fact]
    public void QueryOperationOutsideLegacyIndexSupportFailsConversion()
    {
        var template = SampleManifests.MetadataManifest().StorageUnits.Single();
        var query = template.Queries.Single(x => x.Identity == "find-by-key");
        var unit = template with
        {
            Queries =
            [
                query with
                {
                    Operations = new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal,
                        PortableQueryOperation.Contains
                    }
                }
            ]
        };

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-LEGACY-003");
    }

    [Fact]
    public void OrderingComesFromLegacyQueryDeclaration()
    {
        var unit = SampleManifests.MetadataManifest().StorageUnits.Single();

        var result = LegacyPhysicalStorageBridge.Convert(
            unit,
            new SharedStorageBinding("legacy-documents"));

        Assert.Equal(
            QuerySortSupport.Both,
            result.PhysicalStorage!.BoundedQueries.Single(x => x.Identity == "list-by-category").SortSupport);
    }
}
