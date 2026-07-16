using System.Text.Json;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class ManifestSourceLoaderTests
{
    [Fact]
    public async Task Explicit_source_selection_does_not_load_unrelated_types()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var assembly = Path.Combine(
            AppContext.BaseDirectory,
            "ExplicitSourceFixture",
            "Groundwork.SchemaTool.ExplicitSourceFixture.dll");

        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            candidate => candidate.GetName().Name == "Microsoft.Extensions.Identity.Stores");

        var exitCode = await GroundworkSchemaCli.RunAsync(
        [
            "validate",
            "--manifest-assembly", assembly,
            "--manifest-type", "Groundwork.SchemaTool.ExplicitSourceFixture.ExplicitManifestSource",
            "--provider", "sqlite",
            "--output", "json",
            "--offline"
        ], output, error);

        Assert.Equal(SchemaToolExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "explicit-source-fixture",
            report.RootElement.GetProperty("target").GetProperty("manifestIdentity").GetString());
    }
}
