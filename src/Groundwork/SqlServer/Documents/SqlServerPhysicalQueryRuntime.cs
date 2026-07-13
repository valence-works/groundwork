using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerPhysicalQueryRuntime
{
    public static IBoundedDocumentStore Create(
        SqlServerPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        RelationalPhysicalQueryRuntime.Create(store, manifest, route, provider, "sqlserver");
}
