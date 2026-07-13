using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;

namespace Groundwork.SchemaTool;

internal sealed record SchemaToolTargetCompilation(
    PhysicalSchemaTarget? Target,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsValid => Target is not null && Diagnostics.All(item => !item.IsError);
}

internal static class SchemaToolTargetCompiler
{
    public static SchemaToolTargetCompilation Compile(
        StorageManifest manifest,
        IPhysicalNamePolicy namePolicy,
        string provider)
    {
        var descriptor = ProviderDescriptor.Find(provider)
            ?? throw new SchemaToolConfigurationException(
                "GW-CLI-002",
                "Unknown provider. Supported providers: mongodb, postgresql, sqlite, sqlserver.");

        var diagnostics = new List<GroundworkDiagnostic>();
        diagnostics.AddRange(new StorageManifestValidator().Validate(manifest).Diagnostics);
        if (diagnostics.Any(item => item.IsError))
            return new SchemaToolTargetCompilation(null, Order(diagnostics));

        if (descriptor.Alias == "mongodb")
        {
            try
            {
                return new SchemaToolTargetCompilation(
                    MongoDbPhysicalStorageModel.Compile(manifest, descriptor.Identity, namePolicy).Target,
                    Order(diagnostics));
            }
            catch (Exception)
            {
                diagnostics.Add(GroundworkDiagnostic.Error(
                    "GW-CLI-008",
                    "MongoDB physical target compilation failed.",
                    "physicalRoutes"));
                return new SchemaToolTargetCompilation(null, Order(diagnostics));
            }
        }

        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            namePolicy,
            descriptor.PhysicalNames);
        diagnostics.AddRange(resolution.Diagnostics);
        if (diagnostics.Any(item => item.IsError))
            return new SchemaToolTargetCompilation(null, Order(diagnostics));

        var routes = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        diagnostics.AddRange(routes.Diagnostics);
        return routes.IsValid
            ? new SchemaToolTargetCompilation(
                new PhysicalSchemaTarget(
                    manifest.Identity,
                    manifest.Version,
                    descriptor.Identity,
                    routes.Routes),
                Order(diagnostics))
            : new SchemaToolTargetCompilation(null, Order(diagnostics));
    }

    private static IReadOnlyList<GroundworkDiagnostic> Order(IEnumerable<GroundworkDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Target, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToArray();
}

internal sealed record ProviderDescriptor(
    string Alias,
    ProviderIdentity Identity,
    IProviderPhysicalNameNormalizer PhysicalNames)
{
    private static readonly IReadOnlyList<ProviderDescriptor> All =
    [
        new("mongodb", MongoDbGroundworkCapabilities.Provider, MongoDbPhysicalNameNormalizer.Instance),
        new("postgresql", PostgreSqlGroundworkCapabilities.Provider, PostgreSqlGroundworkCapabilities.PhysicalNames),
        new("sqlite", SqliteGroundworkCapabilities.Provider, ProviderPhysicalNameNormalizer.Identity),
        new("sqlserver", SqlServerGroundworkCapabilities.Provider, SqlServerGroundworkCapabilities.PhysicalNames)
    ];

    public static ProviderDescriptor? Find(string alias) => All.SingleOrDefault(
        item => string.Equals(item.Alias, alias, StringComparison.OrdinalIgnoreCase));
}
