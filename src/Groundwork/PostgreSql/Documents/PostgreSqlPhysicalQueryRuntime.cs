using System.Data.Common;
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
        ProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        return CompilePlanSet(manifest, route, provider).Bind(store);
    }

    /// <summary>
    /// Compiles a connection-independent plan set once so a session-per-operation consumer can bind it to
    /// a fresh store on each open without recompiling the admitted catalog.
    /// </summary>
    public static RelationalPhysicalQueryPlanSet CompilePlanSet(
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        RelationalPhysicalQueryRuntime.CompilePlanSet(manifest, route, provider, "postgresql", ExplainAsync);

    internal static async Task<RelationalPhysicalNativeQueryPlan> ExplainAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        command.CommandText = $"EXPLAIN (FORMAT JSON) {command.CommandText}";
        var content = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
        return new RelationalPhysicalNativeQueryPlan("postgresql-json", content);
    }
}
