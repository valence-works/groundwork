using System.Collections.Frozen;
using System.Data.Common;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;

namespace Groundwork.Sqlite.Documents;

/// <summary>Builds the certified SQLite bounded-query runtime for one compiled storage route.</summary>
public static class SqlitePhysicalQueryRuntime
{
    public static IBoundedDocumentStore Create(
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        var unit = manifest.StorageUnits.Single(candidate => candidate.Identity == route.StorageUnit);
        var storage = unit.PhysicalStorage ?? throw new InvalidOperationException(
            $"Storage unit '{route.StorageUnit.Value}' has no physical query declarations.");
        var capabilities = Capabilities(provider);
        var compilation = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics.Select(x => $"{x.Code}: {x.Message}")));

        var handlers = capabilities.HandlerIdentities.Select(registration =>
        {
            var certifications = compilation.Plans
                .Where(plan => plan.HandlerIdentity == registration.Value)
                .Select(RelationalPhysicalDocumentQueryHandler.Certify)
                .ToArray();
            return (IPhysicalDocumentQueryHandler)new RelationalPhysicalDocumentQueryHandler(
                registration.Value,
                registration.Key,
                store,
                certifications,
                ExplainAsync);
        }).ToArray();
        return new PhysicalQueryDocumentStore(route, storage, capabilities, handlers);
    }

    internal static async Task<RelationalPhysicalNativeQueryPlan> ExplainAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        command.CommandText = $"EXPLAIN QUERY PLAN {command.CommandText}";
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            details.Add(reader.GetString(3));
        return new RelationalPhysicalNativeQueryPlan("sqlite-query-plan", string.Join(Environment.NewLine, details));
    }

    public static PhysicalQueryPlannerCapabilities Capabilities(ProviderIdentity provider)
    {
        var sources = new[]
        {
            PhysicalQuerySourceKind.LinkedIndex,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            PhysicalQuerySourceKind.PrimaryEnvelope,
            PhysicalQuerySourceKind.PrimaryCanonicalJson
        };
        return new PhysicalQueryPlannerCapabilities(
            provider,
            sources,
            Enum.GetValues<PortableQueryOperation>().ToFrozenSet(),
            sources.ToFrozenDictionary(source => source, source => $"sqlite:{source}"),
            nativeFieldIdentifiers: null,
            supportsCompoundPredicates: true,
            supportsDisjunction: true,
            supportsOffsetPaging: true,
            supportsKeysetPaging: true,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey: true,
            sourceValueKinds: new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>
            {
                [PhysicalQuerySourceKind.PrimaryCanonicalJson] = new HashSet<IndexValueKind>
                {
                    IndexValueKind.String,
                    IndexValueKind.Keyword,
                    IndexValueKind.Boolean
                }
            });
    }
}
