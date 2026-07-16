using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Validation;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>Compiles a provider-neutral physical-schema target from a storage manifest.</summary>
public static class PhysicalSchemaTargetCompiler
{
    public static PhysicalSchemaTarget Compile(
        StorageManifest manifest,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer providerNames,
        IPhysicalNamePolicy? namePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerNames);
        namePolicy ??= PhysicalNamePolicy.Identity;
        new StorageManifestValidator().Validate(manifest).RequireValid();

        var resolution = PhysicalStorageResolver.Resolve(manifest, namePolicy, providerNames);
        if (!resolution.IsValid)
            throw Invalid("physical storage", resolution.Diagnostics);

        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!compilation.IsValid)
            throw Invalid("executable routes", compilation.Diagnostics);

        return new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            provider,
            compilation.Routes);
    }

    private static InvalidOperationException Invalid(
        string stage,
        IReadOnlyList<GroundworkDiagnostic> diagnostics) =>
        new($"Physical schema target {stage} compilation failed: " +
            string.Join("; ", diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
}
