using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Xunit;

namespace Groundwork.Tests;

public sealed class StorageManifestCompositionTests
{
    private static StorageManifest RuntimeManifest() =>
        SampleManifests.MetadataManifest() with
        {
            Identity = new StorageManifestIdentity("runtime.documents"),
        };

    private static StorageManifest DesignManifest()
    {
        var baseManifest = SampleManifests.MetadataManifest();
        var designUnits = baseManifest.StorageUnits
            .Select(unit => unit with { Identity = new StorageUnitIdentity($"design.{unit.Identity.Value}") })
            .ToList();

        return baseManifest with
        {
            Identity = new StorageManifestIdentity("design.documents"),
            StorageUnits = designUnits,
        };
    }

    [Fact]
    public void UnionMergesDisjointStorageUnits()
    {
        var runtime = RuntimeManifest();
        var design = DesignManifest();

        var union = StorageManifestComposition.Union(
            new StorageManifestIdentity("composite.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            runtime,
            design);

        var expected = runtime.StorageUnits
            .Concat(design.StorageUnits)
            .Select(unit => unit.Identity.Value)
            .ToHashSet();

        Assert.Equal(expected, union.StorageUnits.Select(unit => unit.Identity.Value).ToHashSet());
        Assert.Equal("composite.documents", union.Identity.Value);
    }

    [Fact]
    public void UnionRemainsValid()
    {
        var union = StorageManifestComposition.Union(
            new StorageManifestIdentity("composite.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            RuntimeManifest(),
            DesignManifest());

        var result = new StorageManifestValidator().Validate(union);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    [Fact]
    public void UnionUnionsRequiredCapabilities()
    {
        var runtime = RuntimeManifest() with { RequiredCapabilities = new HashSet<string> { "cap.a" } };
        var design = DesignManifest() with { RequiredCapabilities = new HashSet<string> { "cap.a", "cap.b" } };

        var union = StorageManifestComposition.Union(
            new StorageManifestIdentity("composite.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            runtime,
            design);

        Assert.Equal(new HashSet<string> { "cap.a", "cap.b" }, union.RequiredCapabilities.ToHashSet());
    }

    [Fact]
    public void UnionThrowsOnOverlappingStorageUnitIdentity()
    {
        var exception = Assert.Throws<StorageManifestCompositionException>(() =>
            StorageManifestComposition.Union(
                new StorageManifestIdentity("composite.documents"),
                new StorageManifestOwner("sample.application"),
                new StorageManifestVersion("1.0.0"),
                RuntimeManifest(),
                RuntimeManifest()));

        Assert.Contains("configurationDocument", exception.UnitIdentity);
    }

    [Fact]
    public void UnionRequiresAtLeastOneManifest()
    {
        Assert.Throws<ArgumentException>(() =>
            StorageManifestComposition.Union(
                new StorageManifestIdentity("composite.documents"),
                new StorageManifestOwner("sample.application"),
                new StorageManifestVersion("1.0.0")));
    }
}
