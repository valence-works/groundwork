using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.PostgreSql.Documents;

/// <summary>Builds the certified PostgreSQL bounded-mutation runtime for one compiled route.</summary>
public static class PostgreSqlPhysicalMutationRuntime
{
    public static IBoundedDocumentMutationStore Create(
        PostgreSqlPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        RelationalPhysicalMutationRuntime.Create(new RelationalPhysicalMutationRuntimeContext(
            store,
            manifest,
            route,
            provider,
            PostgreSqlGroundworkCapabilities.Provider.Name,
            "postgresql"));
}
