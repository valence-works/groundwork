using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;

namespace Groundwork.Sqlite.Tests;

internal static class ClosedQueryManifests
{
    public static ProviderIdentity Provider { get; } = new("groundwork-sqlite", "1.0.0");

    public static StorageManifest WidgetManifest() =>
        new(
            new StorageManifestIdentity("closed.query.widgets"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("widget"),
                    "Widget",
                    StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Global,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [
                        Index("by-name", "name", sortable: true, PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.Contains, PortableQueryOperation.NotContains),
                        Index("by-category", "category", sortable: false, PortableQueryOperation.Equal, PortableQueryOperation.In),
                        Index("by-color", "color", sortable: false, PortableQueryOperation.Equal),
                        Index("by-sort-key", "sortKey", sortable: true, PortableQueryOperation.Equal)
                    ],
                    [],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    public static StorageManifest TenantManifest() =>
        new(
            new StorageManifestIdentity("closed.query.scoped"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("scopedDocument"),
                    "Scoped document",
                    StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Scoped,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [
                        Index("by-name", "name", sortable: true, PortableQueryOperation.Equal, PortableQueryOperation.In, PortableQueryOperation.Contains, PortableQueryOperation.NotContains)
                    ],
                    [],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    private static IndexDeclaration Index(string identity, string field, bool sortable, params PortableQueryOperation[] operations) =>
        new(
            identity,
            [new IndexField(field)],
            IndexValueKind.String,
            false,
            sortable,
            MissingValueBehavior.Excluded,
            new HashSet<PortableQueryOperation>(operations));
}
