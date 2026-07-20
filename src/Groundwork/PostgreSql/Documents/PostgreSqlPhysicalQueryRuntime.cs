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
        ProviderIdentity provider) =>
        RelationalPhysicalQueryRuntime.CreateWithExplainer(
            store,
            manifest,
            route,
            provider,
            "postgresql",
            ExplainAsync);

    internal static async Task<RelationalPhysicalNativeQueryPlan> ExplainAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        command.CommandText = $"EXPLAIN (FORMAT JSON) {command.CommandText}";
        var content = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
        return new RelationalPhysicalNativeQueryPlan("postgresql-json", content);
    }
}
