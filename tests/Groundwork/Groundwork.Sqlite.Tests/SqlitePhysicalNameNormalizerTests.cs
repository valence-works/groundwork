using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqlitePhysicalNameNormalizerTests
{
    [Fact]
    public void PhysicalIndexesUseStorageUnitQualifiedNamesInTheSchemaGlobalNamespace()
    {
        var normalizer = SqliteGroundworkCapabilities.PhysicalNames;

        var first = new ProviderPhysicalNameContext(
            new StorageUnitIdentity("firstUnit"),
            PhysicalObjectKind.PhysicalIndex,
            "by-category");
        var second = first with { StorageUnit = new StorageUnitIdentity("FirstUnit") };

        var firstName = normalizer.Normalize(first);
        var secondName = normalizer.Normalize(second);
        Assert.Equal(PhysicalizationNameEncoder.Encode("firstUnit\u001fby-category"), firstName);
        Assert.Equal(PhysicalizationNameEncoder.Encode("FirstUnit\u001fby-category"), secondName);
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals(firstName, secondName));
        Assert.Equal("schema-objects", normalizer.GetCollisionScope(first));
        Assert.Equal("schema-objects", normalizer.GetCollisionScope(second));
    }

    [Fact]
    public async Task AppliesRepeatedLogicalIndexNamesToDifferentPhysicalTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var manifest = CreateManifest();
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            SqliteGroundworkCapabilities.PhysicalNames);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        var target = new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            SqliteGroundworkCapabilities.Provider,
            compilation.Routes);

        var result = await PhysicalSchemaApplication.ApplyAsync(
            target,
            new SqlitePhysicalSchemaExecutor(connection));
        var restart = await PhysicalSchemaApplication.ApplyAsync(
            target,
            new SqlitePhysicalSchemaExecutor(connection));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        var indexes = target.Routes
            .SelectMany(route => route.Indexes.Select(index => (Route: route, Index: index)))
            .ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Equal(2, indexes.Select(item => item.Index.Name.Identifier).Distinct(StringComparer.Ordinal).Count());
        foreach (var (route, index) in indexes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type = 'index' AND name = $name;";
            command.Parameters.AddWithValue("$name", index.Name.Identifier);
            Assert.Equal(route.PrimaryStorage.Name.Identifier, await command.ExecuteScalarAsync());
        }
    }

    private static StorageManifest CreateManifest()
    {
        var template = SqliteTestManifests.MetadataManifest();
        return template with
        {
            StorageUnits =
            [
                CreateUnit(template.StorageUnits.Single(), "firstUnit", "first_entities"),
                CreateUnit(template.StorageUnits.Single(), "secondUnit", "second_entities")
            ]
        };
    }

    private static StorageUnit CreateUnit(StorageUnit template, string identity, string table)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var projectedColumn = new ProjectedColumnDefinition(
            "category",
            "category",
            PortablePhysicalType.String,
            IsNullable: false);
        var physicalIndex = new PhysicalIndexDefinition(
            "by-category",
            [new PhysicalIndexColumnDefinition("category", 0)]);

        return template with
        {
            Identity = new StorageUnitIdentity(identity),
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(
                    PhysicalTableDefinition.PhysicalEntityTable(
                        table,
                        [projectedColumn],
                        indexes: [physicalIndex])),
                [logicalIndex])
        };
    }
}
