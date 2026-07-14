using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.SqlServer.Documents;

/// <summary>Builds the certified SQL Server bounded-mutation runtime for one compiled route.</summary>
public static class SqlServerPhysicalMutationRuntime
{
    public static IBoundedDocumentMutationStore Create(
        SqlServerPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        RelationalPhysicalMutationRuntime.Create(new RelationalPhysicalMutationRuntimeContext(
            store,
            manifest,
            route,
            provider,
            SqlServerGroundworkCapabilities.Provider.Name,
            "sqlserver"));
}
