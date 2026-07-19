using System.Xml.Linq;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolDependencyBoundaryTests
{
    private static readonly string RepositoryRoot = RepositoryRootLocator.FindRepositoryRoot();

    [Fact]
    public void Core_manifest_source_contract_has_no_provider_sdk_or_ef_dependency()
    {
        var project = Load("src/Groundwork/Core/Groundwork.Core.csproj");

        Assert.Empty(References(project, "ProjectReference"));
        Assert.Empty(References(project, "PackageReference"));
        Assert.DoesNotContain(
            "EntityFrameworkCore",
            File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "src/Groundwork/Core/SchemaEvolution/IPhysicalSchemaManifestSource.cs")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tool_is_the_only_composition_root_and_references_all_available_providers_and_deployment_contracts_without_ef()
    {
        var project = Load("src/Groundwork/SchemaTool/Groundwork.SchemaTool.csproj");
        var actual = References(project, "ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Replace('\\', '/')))
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Groundwork.Core",
                "Groundwork.DiagnosticRecords",
                "Groundwork.DiagnosticRecords.Relational",
                "Groundwork.MongoDb",
                "Groundwork.PostgreSql",
                "Groundwork.SqlServer",
                "Groundwork.Sqlite"
            },
            actual);
        Assert.DoesNotContain(
            project.Descendants().Select(element => element.Value),
            value => value.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument Load(string path) => XDocument.Load(Path.Combine(RepositoryRoot, path));

    private static IEnumerable<string> References(XDocument document, string elementName) => document
        .Descendants(elementName)
        .Select(element => element.Attribute("Include")?.Value)
        .OfType<string>();
}
