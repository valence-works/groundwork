using System.Collections.Frozen;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

/// <summary>
/// Builds a bounded-query runtime from executable relational handlers. Provider adapters supply
/// only their identity and canonical-JSON value kinds; route certification stays shared.
/// </summary>
public static class RelationalPhysicalQueryRuntime
{
    public static IBoundedDocumentStore Create(
        RelationalPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        string handlerPrefix,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerPrefix);
        var unit = manifest.StorageUnits.Single(candidate => candidate.Identity == route.StorageUnit);
        var storage = unit.PhysicalStorage ?? throw new InvalidOperationException(
            $"Storage unit '{route.StorageUnit.Value}' has no physical query declarations.");
        var capabilities = Capabilities(provider, handlerPrefix, canonicalJsonValueKinds);
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

    public static PhysicalQueryPlannerCapabilities Capabilities(
        ProviderIdentity provider,
        string handlerPrefix,
        IReadOnlySet<IndexValueKind>? canonicalJsonValueKinds = null)
    {
        var sources = new[]
        {
            PhysicalQuerySourceKind.LinkedIndex,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            PhysicalQuerySourceKind.PrimaryEnvelope,
            PhysicalQuerySourceKind.PrimaryCanonicalJson
        };
        var valueKinds = canonicalJsonValueKinds ?? new HashSet<IndexValueKind>
        {
            IndexValueKind.String,
            IndexValueKind.Keyword
        };
        return new PhysicalQueryPlannerCapabilities(
            provider,
            sources,
            Enum.GetValues<PortableQueryOperation>().ToFrozenSet(),
            sources.ToFrozenDictionary(source => source, source => $"{handlerPrefix}:{source}"),
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
                [PhysicalQuerySourceKind.PrimaryCanonicalJson] = valueKinds
            });
    }
}
