using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.PostgreSql;
using Groundwork.PostgreSql.PhysicalStorage;
using Npgsql;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed partial class PostgreSqlRelationalPhysicalStorageConformanceTests
{
    [Fact]
    public void PhysicalIndexesUseStorageUnitQualifiedNamesInTheSchemaGlobalRelationNamespace()
    {
        var normalizer = PostgreSqlGroundworkCapabilities.PhysicalNames;
        var first = new ProviderPhysicalNameContext(
            new StorageUnitIdentity("firstUnit"),
            PhysicalObjectKind.PhysicalIndex,
            "by-category");
        var second = first with { StorageUnit = new StorageUnitIdentity("secondUnit") };
        var longUnicode = first with
        {
            StorageUnit = new StorageUnitIdentity(string.Concat(Enumerable.Repeat("📌長い名前", 20)))
        };

        var firstName = normalizer.Normalize(first);
        var secondName = normalizer.Normalize(second);
        var longUnicodeName = normalizer.Normalize(longUnicode);

        Assert.Equal(PhysicalizationNameEncoder.Encode("firstUnit\\u001fby-category"), firstName);
        Assert.Equal(PhysicalizationNameEncoder.Encode("secondUnit\\u001fby-category"), secondName);
        Assert.NotEqual(firstName, secondName);
        Assert.InRange(Encoding.UTF8.GetByteCount(longUnicodeName), 1, 63);
        Assert.Equal("schema-relations", normalizer.GetCollisionScope(first));
        Assert.Equal("schema-relations", normalizer.GetCollisionScope(second));
    }

    [Fact]
    public async Task AppliesRepeatedLogicalIndexNamesToDifferentPhysicalTables()
    {
        var manifest = CreateRepeatedIndexNameManifest();
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            PostgreSqlGroundworkCapabilities.PhysicalNames);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        var target = new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            PostgreSqlGroundworkCapabilities.Provider,
            compilation.Routes);

        var executor = new PostgreSqlPhysicalSchemaExecutor(container.GetConnectionString());
        var result = await PhysicalSchemaApplication.ApplyAsync(target, executor);
        var restart = await PhysicalSchemaApplication.ApplyAsync(target, executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, restart.Outcome);
        var indexes = target.Routes
            .SelectMany(route => route.Indexes.Select(index => (Route: route, Index: index)))
            .ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Equal(2, indexes.Select(item => item.Index.Name.Identifier).Distinct(StringComparer.Ordinal).Count());

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        foreach (var (route, index) in indexes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT tablename FROM pg_indexes WHERE schemaname = current_schema() AND indexname = @name;";
            command.Parameters.AddWithValue("name", index.Name.Identifier);
            Assert.Equal(route.PrimaryStorage.Name.Identifier, await command.ExecuteScalarAsync());
        }
    }

    private static StorageManifest CreateRepeatedIndexNameManifest()
    {
        var template = RelationalTestManifests.MetadataManifest();
        return template with
        {
            Identity = new StorageManifestIdentity("postgresql.index-namespace"),
            StorageUnits =
            [
                CreateRepeatedIndexNameUnit(template.StorageUnits.Single(), "firstUnit", "namespace_first_entities"),
                CreateRepeatedIndexNameUnit(template.StorageUnits.Single(), "secondUnit", "namespace_second_entities")
            ]
        };
    }

    private static StorageUnit CreateRepeatedIndexNameUnit(StorageUnit template, string identity, string table)
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
