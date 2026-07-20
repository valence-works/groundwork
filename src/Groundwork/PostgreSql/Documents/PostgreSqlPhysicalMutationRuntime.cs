using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.PostgreSql.Documents;

/// <summary>Builds the certified PostgreSQL bounded-mutation runtime for one compiled route.</summary>
public static class PostgreSqlPhysicalMutationRuntime
{
    public static IPhysicalDocumentMutationExplainer Create(
        PostgreSqlPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        (IPhysicalDocumentMutationExplainer)RelationalPhysicalMutationRuntime.Create(new RelationalPhysicalMutationRuntimeContext(
            store,
            manifest,
            route,
            provider,
            PostgreSqlGroundworkCapabilities.Provider.Name,
            "postgresql"),
            explain: ExplainNativeAsync);

    private static async Task<RelationalPhysicalNativeMutationPlan> ExplainNativeAsync(
        DbCommand command,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var native = await PostgreSqlPhysicalQueryRuntime.ExplainAsync(command, cancellationToken);
        return PostgreSqlNativeMutationPlanInspector.Inspect(native, plan, route);
    }
}

internal static class PostgreSqlNativeMutationPlanInspector
{
    internal static RelationalPhysicalNativeMutationPlan Inspect(
        RelationalPhysicalNativeQueryPlan native,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route)
    {
        using var document = JsonDocument.Parse(native.Content);
        var accesses = new List<(string StorageObject, string Index)>();
        Visit(document.RootElement[0].GetProperty("Plan"), null, accesses);
        var selectors = ExpectedTargets(plan, route)
            .Select(expected =>
            {
                var matches = accesses.Where(access =>
                    string.Equals(access.StorageObject, expected.StorageObject.Identifier, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 ||
                    (expected.Index is not null &&
                     !string.Equals(matches[0].Index, expected.Index.Identifier, StringComparison.Ordinal)))
                    throw Missing(plan, expected.Target);
                return new RelationalPhysicalNativeMutationSelector(
                    expected.Target,
                    matches[0].StorageObject,
                    matches[0].Index);
            })
            .ToArray();
        return new RelationalPhysicalNativeMutationPlan(native.Format, native.Content, selectors);
    }

    private static void Visit(
        JsonElement node,
        string? inheritedStorageObject,
        ICollection<(string StorageObject, string Index)> accesses)
    {
        var storageObject = node.TryGetProperty("Relation Name", out var relation)
            ? relation.GetString()
            : inheritedStorageObject;
        if (storageObject is not null &&
            node.TryGetProperty("Index Name", out var index) &&
            !string.IsNullOrWhiteSpace(index.GetString()))
        {
            accesses.Add((storageObject, index.GetString()!));
        }
        if (!node.TryGetProperty("Plans", out var children))
            return;
        foreach (var child in children.EnumerateArray())
            Visit(child, storageObject, accesses);
    }

    private static IReadOnlyList<(
        ExecutableStorageObjectRole Target,
        ProviderPhysicalObjectName StorageObject,
        ProviderPhysicalObjectName? Index)>
        ExpectedTargets(PhysicalMutationPlan plan, ExecutableStorageRoute route) =>
        plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ?
            [
                (ExecutableStorageObjectRole.PrimaryStorage, route.PrimaryStorage.Name, null),
                (ExecutableStorageObjectRole.LinkedIndexStorage, route.LinkedIndexStorage!.Name, plan.Predicate.IndexName)
            ]
            : [(ExecutableStorageObjectRole.PrimaryStorage, route.PrimaryStorage.Name, plan.Predicate.IndexName)];

    private static InvalidOperationException Missing(
        PhysicalMutationPlan plan,
        ExecutableStorageObjectRole target) =>
        new($"PostgreSQL native mutation plan for '{plan.MutationIdentity}' did not prove one indexed '{target}' selector.");
}
