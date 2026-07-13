using Groundwork.Core.Indexing;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalPhysicalStorageTestModels
{
    public static (StorageManifest Manifest, PhysicalSchemaTarget Target) Create(
        PhysicalStorageForm form,
        ProviderIdentity provider,
        bool includePriority,
        bool scoped = false,
        bool dedicatedWithoutLinked = false,
        PortablePhysicalType priorityType = PortablePhysicalType.Int32,
        int? priorityPrecision = null,
        int? priorityScale = null,
        string? instance = null,
        IProviderPhysicalNameNormalizer? normalizer = null,
        bool categoryUnique = false,
        bool categoryNullable = false)
    {
        var template = RelationalTestManifests.MetadataManifest();
        instance ??= Guid.NewGuid().ToString("N")[..8];
        var columns = new List<ProjectedColumnDefinition>
        {
            new("category", "category", PortablePhysicalType.String, Length: 200, IsNullable: categoryNullable)
        };
        var categoryIndexColumns = new List<PhysicalIndexColumnDefinition>();
        if (scoped)
            categoryIndexColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
        categoryIndexColumns.Add(new PhysicalIndexColumnDefinition("category", categoryIndexColumns.Count));
        var indexes = new List<PhysicalIndexDefinition>
        {
            new("by-category", categoryIndexColumns, categoryUnique)
        };
        if (includePriority)
        {
            columns.Add(new ProjectedColumnDefinition(
                "priority",
                "priority",
                priorityType,
                Precision: priorityPrecision,
                Scale: priorityScale));
            var priorityIndexColumns = new List<PhysicalIndexColumnDefinition>();
            if (scoped)
                priorityIndexColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
            priorityIndexColumns.Add(new PhysicalIndexColumnDefinition("priority", priorityIndexColumns.Count));
            indexes.Add(new PhysicalIndexDefinition("by-priority", priorityIndexColumns));
            var compoundColumns = new List<PhysicalIndexColumnDefinition>();
            if (scoped)
                compoundColumns.Add(new PhysicalIndexColumnDefinition("storage_scope", 0));
            compoundColumns.Add(new PhysicalIndexColumnDefinition("category", compoundColumns.Count));
            compoundColumns.Add(new PhysicalIndexColumnDefinition("priority", compoundColumns.Count));
            indexes.Add(new PhysicalIndexDefinition("by-category-priority", compoundColumns));
        }

        var binding = new SharedStorageBinding("runtime-documents");
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.String,
            categoryUnique,
            MissingValueBehavior.Excluded);
        var boundedQuery = new BoundedQueryDeclaration(
            "list-by-category",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count,
                BoundedQueryResultOperation.Any,
                BoundedQueryResultOperation.First
            });
        var logicalIndexes = new List<LogicalIndexDeclaration> { logicalIndex };
        var boundedQueries = new List<BoundedQueryDeclaration> { boundedQuery };
        if (includePriority)
        {
            var compound = new LogicalIndexDeclaration(
                "by-category-priority",
                [new IndexField("category"), new IndexField("priority", ToIndexValueKind(priorityType))],
                IndexValueKind.String,
                false,
                MissingValueBehavior.Excluded);
            logicalIndexes.Add(compound);
            boundedQueries.Add(new BoundedQueryDeclaration(
                "find-by-category-priority",
                compound.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true,
                predicateFields:
                [
                    new BoundedQueryPredicateField("category", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                    new BoundedQueryPredicateField("priority", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                ],
                resultOperations: new HashSet<BoundedQueryResultOperation>
                {
                    BoundedQueryResultOperation.Documents,
                    BoundedQueryResultOperation.Count
                }));
        }

        var definition = dedicatedWithoutLinked
            ? PhysicalTableDefinition.DedicatedDocumentTable("configuration_documents")
            : form switch
            {
                PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                    binding, columns, indexes, linkedProjectionLogicalName: "configuration_projection"),
                PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                    "configuration_documents", indexes: indexes, linkedProjectedColumns: columns,
                    linkedProjectionLogicalName: "configuration_projection"),
                PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                    "configuration_entities", columns, indexes: indexes),
                _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
            };
        var manifest = template with
        {
            Identity = new StorageManifestIdentity($"{template.Identity.Value}-{instance}"),
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Tenancy = scoped ? TenancyPolicy.Scoped : TenancyPolicy.Global,
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition),
                        dedicatedWithoutLinked ? [] : logicalIndexes,
                        dedicatedWithoutLinked ? [] : boundedQueries)
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer ?? ProviderPhysicalNameNormalizer.Identity);
        if (!resolution.IsValid)
            throw new InvalidOperationException(string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!compilation.IsValid)
            throw new InvalidOperationException(string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return (manifest, new PhysicalSchemaTarget(manifest.Identity, manifest.Version, provider, compilation.Routes));
    }

    private static IndexValueKind ToIndexValueKind(PortablePhysicalType type) => type switch
    {
        PortablePhysicalType.Boolean => IndexValueKind.Boolean,
        PortablePhysicalType.Int32 or PortablePhysicalType.Int64 or PortablePhysicalType.Decimal => IndexValueKind.Number,
        PortablePhysicalType.DateTime => IndexValueKind.DateTime,
        PortablePhysicalType.Guid or PortablePhysicalType.Binary or PortablePhysicalType.Json =>
            throw new InvalidOperationException($"Physical type '{type}' has no portable logical index value kind."),
        _ => IndexValueKind.String
    };
}
