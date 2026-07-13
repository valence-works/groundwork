using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.PostgreSql.Documents;

public static class PostgreSqlPhysicalQueryRuntime
{
    public static IBoundedDocumentStore Create(
        PostgreSqlPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        RelationalPhysicalQueryRuntime.Create(store, manifest, route, provider, "postgresql");
}
