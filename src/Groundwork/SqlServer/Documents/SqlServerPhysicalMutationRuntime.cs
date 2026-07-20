using System.Data.Common;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.SqlServer.Documents;

/// <summary>Builds the certified SQL Server bounded-mutation runtime for one compiled route.</summary>
public static class SqlServerPhysicalMutationRuntime
{
    public static IPhysicalDocumentMutationExplainer Create(
        SqlServerPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        (IPhysicalDocumentMutationExplainer)RelationalPhysicalMutationRuntime.Create(new RelationalPhysicalMutationRuntimeContext(
            store,
            manifest,
            route,
            provider,
            SqlServerGroundworkCapabilities.Provider.Name,
            "sqlserver"),
            explain: ExplainNativeAsync);

    private static async Task<RelationalPhysicalNativeMutationPlan> ExplainNativeAsync(
        DbCommand command,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var native = await SqlServerPhysicalQueryRuntime.ExplainAsync(command, cancellationToken);
        return SqlServerNativeMutationPlanInspector.Inspect(native, plan, route);
    }
}

internal static partial class SqlServerNativeMutationPlanInspector
{
    [GeneratedRegex(@"\[(?<part>(?:[^\]]|\]\])*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierParts();

    internal static RelationalPhysicalNativeMutationPlan Inspect(
        RelationalPhysicalNativeQueryPlan native,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route)
    {
        var document = XDocument.Parse(native.Content);
        var accesses = document.Descendants()
            .Where(element => element.Name.LocalName == "RelOp" &&
                              element.Attribute("PhysicalOp")?.Value is "Index Seek" or "Clustered Index Seek")
            .SelectMany(element => element.Descendants().Where(candidate => candidate.Name.LocalName == "Object"))
            .Select(element => (
                StorageObject: Normalize(element.Attribute("Table")?.Value),
                Index: Normalize(element.Attribute("Index")?.Value)))
            .Where(access => access.StorageObject is not null && access.Index is not null)
            .Select(access => (access.StorageObject!, access.Index!))
            .ToArray();
        var selectors = ExpectedTargets(plan, route)
            .Select(expected =>
            {
                var matches = accesses.Where(access =>
                    string.Equals(access.Item1, expected.StorageObject.Identifier, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 ||
                    (expected.Index is not null &&
                     !string.Equals(matches[0].Item2, expected.Index.Identifier, StringComparison.Ordinal)))
                    throw Missing(plan, expected.Target);
                return new RelationalPhysicalNativeMutationSelector(
                    expected.Target,
                    matches[0].Item1,
                    matches[0].Item2);
            })
            .ToArray();
        return new RelationalPhysicalNativeMutationPlan(native.Format, native.Content, selectors);
    }

    private static string? Normalize(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;
        var parts = IdentifierParts().Matches(identifier);
        if (parts.Count != 0)
            return parts[^1].Groups["part"].Value.Replace("]]", "]", StringComparison.Ordinal);
        return identifier.Split('.').Last();
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
        new($"SQL Server native mutation plan for '{plan.MutationIdentity}' did not prove one indexed '{target}' selector.");
}
