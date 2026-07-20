using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

/// <summary>Builds the certified SQLite bounded-mutation runtime for one compiled route.</summary>
public static class SqlitePhysicalMutationRuntime
{
    public static IPhysicalDocumentMutationExplainer Create(
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        (IPhysicalDocumentMutationExplainer)Create(store, manifest, route, provider, null);

    internal static IBoundedDocumentMutationStore Create(
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept) =>
        (IPhysicalDocumentMutationExplainer)RelationalPhysicalMutationRuntime.Create(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                manifest,
                route,
                provider,
                SqliteGroundworkCapabilities.Provider.Name,
                "sqlite",
                CanonicalJsonValueKinds(provider)),
            intercept,
            ExplainNativeAsync);

    private static async Task<RelationalPhysicalNativeMutationPlan> ExplainNativeAsync(
        DbCommand command,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route,
        CancellationToken cancellationToken)
    {
        var renderedCommand = command.CommandText;
        var native = await SqlitePhysicalQueryRuntime.ExplainAsync(command, cancellationToken);
        return SqliteNativeMutationPlanInspector.Inspect(renderedCommand, native, plan, route);
    }

    internal static async Task<string> ExplainAsync(
        SqliteConnection connection,
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        DocumentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var selection = RelationalPhysicalMutationRuntime.BuildSelectionCommand(
            new RelationalPhysicalMutationRuntimeContext(
                store,
                manifest,
                route,
                provider,
                SqliteGroundworkCapabilities.Provider.Name,
                "sqlite",
                CanonicalJsonValueKinds(provider)),
            mutation);
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {selection.CommandText};";
        foreach (var (name, value) in selection.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private static IReadOnlySet<IndexValueKind> CanonicalJsonValueKinds(ProviderIdentity provider) =>
        SqlitePhysicalQueryRuntime.Capabilities(provider)
            .SourceValueKinds[PhysicalQuerySourceKind.PrimaryCanonicalJson];
}

internal static partial class SqliteNativeMutationPlanInspector
{
    [GeneratedRegex(
        @"^(?:SEARCH|SCAN)\s+(?<target>\S+)(?:\s+USING\s+(?:COVERING\s+)?INDEX\s+(?<index>\S+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex AccessPattern();

    [GeneratedRegex(
        @"\b(?:FROM|JOIN)\s+""(?<storage>(?:[^""]|"""")*)""(?:\s+AS)?\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AliasBindingPattern();

    internal static RelationalPhysicalNativeMutationPlan Inspect(
        string renderedCommand,
        RelationalPhysicalNativeQueryPlan native,
        PhysicalMutationPlan plan,
        ExecutableStorageRoute route)
    {
        var accesses = native.Content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => AccessPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => (
                Target: match.Groups["target"].Value,
                Index: match.Groups["index"].Success ? match.Groups["index"].Value : null))
            .ToArray();
        var aliasBindings = AliasBindingPattern().Matches(renderedCommand)
            .Select(match => (
                Alias: match.Groups["alias"].Value,
                StorageObject: match.Groups["storage"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)))
            .GroupBy(binding => binding.Alias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(binding => binding.StorageObject).ToArray(), StringComparer.Ordinal);
        var selectors = ExpectedTargets(plan, route)
            .Select(expected =>
            {
                var alias = expected.Target == ExecutableStorageObjectRole.PrimaryStorage ? "p" : "l";
                var access = accesses.SingleOrDefault(candidate => candidate.Target == alias);
                if (access == default ||
                    string.IsNullOrWhiteSpace(access.Index) ||
                    (expected.Index is not null &&
                     !string.Equals(access.Index, expected.Index.Identifier, StringComparison.Ordinal)) ||
                    !aliasBindings.TryGetValue(alias, out var bindings) ||
                    bindings.Length != 1 ||
                    !string.Equals(bindings[0], expected.StorageObject.Identifier, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"SQLite native mutation plan for '{plan.MutationIdentity}' did not prove one indexed " +
                        $"'{expected.Target}' selector.");
                }
                return new RelationalPhysicalNativeMutationSelector(
                    expected.Target,
                    bindings[0],
                    access.Index);
            })
            .ToArray();
        return new RelationalPhysicalNativeMutationPlan(native.Format, native.Content, selectors);
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

}
