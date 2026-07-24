using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkPhysicalModel(
    StorageManifest Manifest,
    PhysicalSchemaTarget Target,
    ExecutableStorageRoute Route);

public static class BenchmarkModelFactory
{
    public const string DocumentKind = "benchmarkItem";
    public const string QueryIdentity = "list-by-status-rank";
    public const string CompoundIndexIdentity = "by-status-rank";

    public static StorageManifest CreateManifest(
        PhysicalStorageForm form,
        string instance,
        bool includeCategory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var binding = new SharedStorageBinding("benchmark-runtime");
        var columns = new List<ProjectedColumnDefinition>
        {
            new("status", "status", PortablePhysicalType.String, Length: 64, IsNullable: false),
            new("rank", "rank", PortablePhysicalType.Int32, IsNullable: false)
        };
        if (includeCategory)
            columns.Add(new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: 64));

        var index = new PhysicalIndexDefinition(
            CompoundIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("status", 1),
                new PhysicalIndexColumnDefinition("rank", 2, PhysicalSortDirection.Descending)
            ]);
        var table = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                columns,
                [index],
                linkedProjectionLogicalName: "benchmark_items_lookup"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "benchmark_items",
                indexes: [index],
                linkedProjectedColumns: columns,
                linkedProjectionLogicalName: "benchmark_items_lookup"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                "benchmark_items",
                columns,
                indexes: [index]),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var logical = new LogicalIndexDeclaration(
            CompoundIndexIdentity,
            [new IndexField("status", IndexValueKind.Keyword), new IndexField("rank", IndexValueKind.Number)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            QueryIdentity,
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields: [new BoundedQuerySortField("rank", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "status",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count,
                BoundedQueryResultOperation.Any,
                BoundedQueryResultOperation.First
            });
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Physical storage benchmark item",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(table),
                [logical],
                [query])
        };
        return new StorageManifest(
            new StorageManifestIdentity($"benchmark.{instance}"),
            new StorageManifestOwner("groundwork-benchmark-harness"),
            new StorageManifestVersion(includeCategory ? "2" : "1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "benchmark_documents", new DocumentEnvelopeDefinition())]
                : []
        };
    }

    public static BenchmarkPhysicalModel CompileRelational(
        PhysicalStorageForm form,
        string instance,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        bool includeCategory = true)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(normalizer);
        var manifest = CreateManifest(form, instance, includeCategory);
        var target = PhysicalSchemaTargetCompiler.Compile(
            manifest,
            provider,
            normalizer,
            NamePolicy(instance));
        var route = target.Routes.Single();
        return new BenchmarkPhysicalModel(
            manifest,
            target,
            route);
    }

    public static IPhysicalNamePolicy NamePolicy(string instance) =>
        new DelegatePhysicalNamePolicy(context => $"gw_bench_{Sanitize(instance)}_{context.FeatureDefaultLogicalName}");

    private static string Sanitize(string value) =>
        new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').ToArray());
}
