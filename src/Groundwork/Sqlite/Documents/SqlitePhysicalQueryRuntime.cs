using System.Collections.Frozen;
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
                certifications);
        }).ToArray();
        return new PhysicalQueryDocumentStore(route, storage, capabilities, handlers);
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
            supportsKeysetPaging: false,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey: false,
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
