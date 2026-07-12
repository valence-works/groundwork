using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Intents;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalTestManifests
{
    public static StorageManifest MetadataManifest() =>
        new(
            new StorageManifestIdentity("configuration.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("configurationDocument"),
                    "Configuration document",
                    StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Global,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [
                        new IndexDeclaration(
                            "by-key",
                            [new IndexField("key")],
                            IndexValueKind.Keyword,
                            true,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.Contains }),
                        new IndexDeclaration(
                            "by-category",
                            [new IndexField("category")],
                            IndexValueKind.String,
                            false,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In }),
                        new IndexDeclaration(
                            "by-sort",
                            [new IndexField("sort")],
                            IndexValueKind.String,
                            false,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                    ],
                    [
                        new PortableQueryDeclaration(
                            "find-by-key",
                            "by-key",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            QuerySortSupport.None,
                            QueryPagingSupport.None),
                        new PortableQueryDeclaration(
                            "list-by-category",
                            "by-category",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            QuerySortSupport.Both,
                            QueryPagingSupport.Offset)
                    ],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    public static ProviderIdentity SqlServerProvider { get; } = new("groundwork-sqlserver", "1.0.0");
    public static ProviderIdentity PostgreSqlProvider { get; } = new("groundwork-postgresql", "1.0.0");

    public static StorageManifest ScopedManifest()
    {
        var manifest = MetadataManifest();
        return manifest with
        {
            Identity = new StorageManifestIdentity("scoped.configuration.documents"),
            StorageUnits =
            [
                manifest.StorageUnits.Single() with
                {
                    Tenancy = TenancyPolicy.Scoped,
                    Physicalization = PhysicalizationPolicy.Optimized
                }
            ]
        };
    }

    public static StorageManifest WithoutIndex(string indexIdentity)
    {
        var unit = MetadataManifest().StorageUnits.Single();
        return MetadataManifest() with
        {
            StorageUnits =
            [
                unit with
                {
                    Indexes = unit.Indexes.Where(index => index.Identity != indexIdentity).ToList(),
                    Queries = unit.Queries.Where(query => query.IndexIdentity != indexIdentity).ToList()
                }
            ]
        };
    }
}
