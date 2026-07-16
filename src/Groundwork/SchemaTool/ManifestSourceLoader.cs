using System.Reflection;
using System.Runtime.Loader;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.SchemaTool;

internal static class ManifestSourceLoader
{
    public static IPhysicalSchemaManifestSource Load(string assemblyPath, string? typeName)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new SchemaToolConfigurationException("GW-CLI-003", "Manifest assembly was not found.");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => SameLocation(candidate, path))
            ?? Load(path);
        Type sourceType;
        if (typeName is not null)
        {
            sourceType = assembly.GetType(typeName, throwOnError: false, ignoreCase: false)
                ?? throw new SchemaToolConfigurationException("GW-CLI-004", "Manifest source type was not found.");
            if (sourceType.IsAbstract ||
                sourceType.IsInterface ||
                !typeof(IPhysicalSchemaManifestSource).IsAssignableFrom(sourceType))
                throw new SchemaToolConfigurationException("GW-CLI-004", "Manifest source type does not implement IPhysicalSchemaManifestSource.");
        }
        else
        {
            var candidates = assembly.GetTypes()
                .Where(type => !type.IsAbstract &&
                               !type.IsInterface &&
                               typeof(IPhysicalSchemaManifestSource).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            sourceType = candidates.Length == 1
                ? candidates[0]
                : throw new SchemaToolConfigurationException(
                    "GW-CLI-004",
                    candidates.Length == 0
                        ? "Manifest assembly contains no IPhysicalSchemaManifestSource implementation."
                        : "Manifest assembly contains multiple sources; specify '--manifest-type'.");
        }

        try
        {
            return (IPhysicalSchemaManifestSource)(Activator.CreateInstance(sourceType)
                ?? throw new InvalidOperationException("The manifest source constructor returned null."));
        }
        catch (Exception exception) when (exception is not SchemaToolConfigurationException)
        {
            throw new SchemaToolConfigurationException(
                "GW-CLI-004",
                "Manifest source must have an accessible parameterless constructor.",
                exception);
        }
    }

    private static Assembly Load(string path) => new ManifestAssemblyLoadContext(path).LoadFromAssemblyPath(path);

    private static bool SameLocation(Assembly assembly, string path)
    {
        try
        {
            return !string.IsNullOrEmpty(assembly.Location) &&
                   string.Equals(Path.GetFullPath(assembly.Location), path, StringComparison.Ordinal);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private sealed class ManifestAssemblyLoadContext(string assemblyPath) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
            if (shared is not null)
                return shared;

            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

internal sealed class SchemaToolConfigurationException : Exception
{
    public SchemaToolConfigurationException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
