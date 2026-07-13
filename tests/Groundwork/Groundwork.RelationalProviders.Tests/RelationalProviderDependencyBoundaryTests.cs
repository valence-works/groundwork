using System.Xml.Linq;
using Groundwork.PostgreSql.Documents;
using Groundwork.Relational.Documents;
using Groundwork.SqlServer.Documents;
using Groundwork.TestInfrastructure;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalProviderDependencyBoundaryTests
{
    private static readonly string RepositoryRoot = RepositoryRootLocator.FindRepositoryRoot();

    [Fact]
    public void Only_provider_bound_mutation_runtimes_are_public_capability_surfaces()
    {
        Assert.False(typeof(RelationalPhysicalMutationRuntime).IsPublic);
        Assert.True(typeof(SqlServerPhysicalMutationRuntime).IsPublic);
        Assert.True(typeof(PostgreSqlPhysicalMutationRuntime).IsPublic);
    }

    [Theory]
    [InlineData("src/Groundwork/SqlServer/Groundwork.SqlServer.csproj")]
    [InlineData("src/Groundwork/PostgreSql/Groundwork.PostgreSql.csproj")]
    public void ProviderProjectsDoNotReferenceHostSpecificProjects(string projectPath)
    {
        var project = Path.Combine(RepositoryRoot, projectPath);
        var references = XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .ToList();

        Assert.All(references, reference => Assert.DoesNotContain("HostApplication", reference, StringComparison.OrdinalIgnoreCase));
    }

}
