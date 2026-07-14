using Groundwork.Core.Indexing;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.RelationalProviders.Tests;

internal sealed record RelationalTypedTransitionTestOptions(
    string PrioritySource = "1",
    string PriorityTarget = "2",
    IReadOnlyDictionary<string, (string Source, string Target)>? FieldValues = null);

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
        bool categoryNullable = false,
        bool includeCategoryTransition = false,
        bool includeRangeDelete = false,
        string documentKind = "configurationDocument",
        Func<PhysicalNameContext, string>? namePolicy = null,
        bool includeTypedTransitions = false,
        RelationalTypedTransitionTestOptions? typedTransitions = null)
    {
        typedTransitions ??= new RelationalTypedTransitionTestOptions();
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
        if (includeTypedTransitions)
        {
            foreach (var field in TypedTransitionFields())
            {
                columns.Add(new ProjectedColumnDefinition(field.Name, field.Name, field.PhysicalType, Length: field.Length));
                indexes.Add(new PhysicalIndexDefinition(
                    $"by-{field.Name}",
                    [new PhysicalIndexColumnDefinition(field.Name, 0)]));
            }
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
            var priorityOperations = includeRangeDelete
                ? new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.Equal,
                    PortableQueryOperation.LessThan
                }
                : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
            boundedQueries.Add(new BoundedQueryDeclaration(
                "find-by-category-priority",
                compound.Identity,
                priorityOperations,
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true,
                predicateFields:
                [
                    new BoundedQueryPredicateField("category", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                    new BoundedQueryPredicateField(
                        "priority",
                        includeRangeDelete
                            ? new HashSet<PortableQueryOperation> { PortableQueryOperation.LessThan }
                            : new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                ],
                resultOperations: new HashSet<BoundedQueryResultOperation>
                {
                    BoundedQueryResultOperation.Documents,
                    BoundedQueryResultOperation.Count
                }));
        }
        if (includeTypedTransitions)
        {
            var priorityIndex = new LogicalIndexDeclaration(
                "by-priority",
                [new IndexField("priority")],
                IndexValueKind.Number,
                false,
                MissingValueBehavior.Excluded);
            logicalIndexes.Add(priorityIndex);
            boundedQueries.Add(new BoundedQueryDeclaration(
                "list-by-priority",
                priorityIndex.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                supportsTotalCount: true,
                resultOperations: new HashSet<BoundedQueryResultOperation>
                {
                    BoundedQueryResultOperation.Documents,
                    BoundedQueryResultOperation.Count
                }));
            foreach (var field in TypedTransitionFields())
            {
                var index = new LogicalIndexDeclaration(
                    $"by-{field.Name}",
                    [new IndexField(field.Name)],
                    field.ValueKind,
                    false,
                    MissingValueBehavior.Excluded);
                logicalIndexes.Add(index);
                boundedQueries.Add(new BoundedQueryDeclaration(
                    $"list-by-{field.Name}",
                    index.Identity,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    QuerySortSupport.None,
                    QueryPagingSupport.None,
                    BoundedQueryExecutionClass.ScaleBearing,
                    supportsTotalCount: true,
                    resultOperations: new HashSet<BoundedQueryResultOperation>
                    {
                        BoundedQueryResultOperation.Documents,
                        BoundedQueryResultOperation.Count
                    }));
            }
        }
        var boundedMutations = new List<BoundedMutationDeclaration>();
        if (includeCategoryTransition)
        {
            boundedMutations.Add(new BoundedMutationDeclaration(
                "revoke-pending",
                "list-by-category",
                BoundedMutationAction.Transition("category", ["pending"], "revoked")));
        }
        if (includeRangeDelete)
        {
            boundedMutations.Add(new BoundedMutationDeclaration(
                "prune-by-category-cutoff",
                "find-by-category-priority",
                BoundedMutationAction.Delete()));
        }
        if (includeTypedTransitions)
        {
            boundedMutations.Add(new BoundedMutationDeclaration(
                "raise-priority",
                "list-by-priority",
                BoundedMutationAction.Transition(
                    "priority",
                    [typedTransitions.PrioritySource],
                    typedTransitions.PriorityTarget)));
            foreach (var field in TypedTransitionFields())
            {
                var values = typedTransitions.FieldValues is not null &&
                             typedTransitions.FieldValues.TryGetValue(field.Name, out var configured)
                    ? configured
                    : (field.Source, field.Target);
                boundedMutations.Add(new BoundedMutationDeclaration(
                    $"transition-{field.Name}",
                    $"list-by-{field.Name}",
                    BoundedMutationAction.Transition(field.Name, [values.Source], values.Target)));
            }
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
                    Identity = new StorageUnitIdentity(documentKind),
                    Tenancy = scoped ? TenancyPolicy.Scoped : TenancyPolicy.Global,
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(definition),
                        dedicatedWithoutLinked ? [] : logicalIndexes,
                        dedicatedWithoutLinked ? [] : boundedQueries,
                        boundedMutations: boundedMutations)
                }
            ],
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
                : []
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(namePolicy ?? (context => $"gw_{instance}_{context.FeatureDefaultLogicalName}")),
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

    private static IReadOnlyList<TypedTransitionField> TypedTransitionFields() =>
    [
        new("enabled", PortablePhysicalType.Boolean, IndexValueKind.Boolean, "true", "false"),
        new("title", PortablePhysicalType.String, IndexValueKind.String, "alpha", "bravo", 200),
        new("token", PortablePhysicalType.String, IndexValueKind.Keyword, "TOKEN_A", "TOKEN_B", 200),
        new("dueAt", PortablePhysicalType.DateTime, IndexValueKind.DateTime, "2026-01-01T00:00:00Z", "2026-02-02T00:00:00Z"),
        new("externalId", PortablePhysicalType.Guid, IndexValueKind.Keyword,
            "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")
    ];

    private sealed record TypedTransitionField(
        string Name,
        PortablePhysicalType PhysicalType,
        IndexValueKind ValueKind,
        string Source,
        string Target,
        int? Length = null);
}
