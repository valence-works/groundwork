using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Provider-neutral deployment entry point discovered by the Groundwork schema tool. Application
/// assemblies expose their manifest and optional host naming policy through this contract; provider
/// selection, provider SDKs, connections, and schema execution remain outside Core.
/// </summary>
public interface IPhysicalSchemaManifestSource
{
    StorageManifest CreateManifest();

    IPhysicalNamePolicy CreateNamePolicy() => PhysicalNamePolicy.Identity;
}
