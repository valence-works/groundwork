using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.TestInfrastructure;

public static class SortOnlyResidualPredicateConformance
{
    public static StorageManifest CreateManifest(string instance)
    {
        var index = new LogicalIndexDeclaration(
            "by-last-modified-definition",
            [
                new IndexField("lastModifiedAt", IndexValueKind.DateTime),
                new IndexField("definitionId")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "browse-definitions",
            index.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.Contains
            },
            QuerySortSupport.Both,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true,
            sortFields:
            [
                new BoundedQuerySortField("lastModifiedAt", PhysicalSortDirection.Descending),
                new BoundedQuerySortField("definitionId", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "lastModifiedAt",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.Count
            },
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "definitionId",
                    IndexValueKind.String,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains },
                    isRequired: true)
            ]);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_definitions",
            [
                new ProjectedColumnDefinition(
                    "last_modified_at",
                    "lastModifiedAt",
                    PortablePhysicalType.DateTime),
                new ProjectedColumnDefinition(
                    "definition_id",
                    "definitionId",
                    PortablePhysicalType.String,
                    Length: 200)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    [
                        new PhysicalIndexColumnDefinition(
                            "last_modified_at",
                            0,
                            PhysicalSortDirection.Descending),
                        new PhysicalIndexColumnDefinition(
                            "definition_id",
                            1,
                            PhysicalSortDirection.Ascending),
                        new PhysicalIndexColumnDefinition(
                            new DocumentEnvelopeDefinition().IdLookupKeyColumn,
                            2,
                            PhysicalSortDirection.Ascending)
                    ])
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity("workflowDefinition"),
            "Workflow definition",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Global,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [index],
                [query])
        };
        return new StorageManifest(
            new StorageManifestIdentity($"sort-only-residual.{instance}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
    }

    public static PhysicalSchemaTarget CreateTarget(
        StorageManifest manifest,
        ProviderIdentity provider,
        IProviderPhysicalNameNormalizer normalizer,
        string instance)
    {
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context =>
                $"gw_{instance}_{context.FeatureDefaultLogicalName}"),
            normalizer);
        Assert.True(
            resolution.IsValid,
            string.Join("; ", resolution.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(
            compilation.IsValid,
            string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            provider,
            compilation.Routes);
    }

    public static async Task VerifyAsync(
        IDocumentStore writer,
        IBoundedDocumentStore queries)
    {
        await SaveAsync("noise-new", "noise-new", "2026-07-19T13:00:00Z");
        await SaveAsync("document-b", "target-b", "2026-07-19T12:00:00Z");
        await SaveAsync("document-a", "target-a", "2026-07-19T12:00:00Z");
        await SaveAsync("noise-middle", "noise-middle", "2026-07-19T11:00:00Z");
        await SaveAsync("document-c", "target-c", "2026-07-19T10:00:00Z");
        var targetClause = DocumentQueryClause.Of(
            DocumentQueryComparison.Contains("definitionId", "target"));
        var query = new DocumentQuery(
            "workflowDefinition",
            "browse-definitions",
            [targetClause],
            [
                new DocumentQueryOrder("lastModifiedAt", PhysicalSortDirection.Descending),
                new DocumentQueryOrder("definitionId", PhysicalSortDirection.Ascending)
            ],
            take: 1);

        var first = await queries.QueryAsync(query);
        var second = await queries.QueryAsync(query.ContinueAfter(first.NextContinuation!));
        var final = await queries.QueryAsync(
            query.Page(skip: null, take: 2).ContinueAfter(second.NextContinuation!));
        var count = await queries.CountAsync(query.Select(BoundedQueryResultOperation.Count));
        var mismatched = new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            [
                DocumentQueryClause.Of(
                    DocumentQueryComparison.Contains("definitionId", "noise"))
            ],
            query.Order,
            take: query.Take,
            continuation: first.NextContinuation);

        Assert.Equal("document-a", Assert.Single(first.Documents).Id);
        Assert.NotNull(first.NextContinuation);
        Assert.Equal("document-b", Assert.Single(second.Documents).Id);
        Assert.NotNull(second.NextContinuation);
        Assert.Equal("document-c", Assert.Single(final.Documents).Id);
        Assert.Null(final.NextContinuation);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(3, final.TotalCount);
        Assert.Equal(3, count);
        await Assert.ThrowsAsync<InvalidDocumentQueryContinuationException>(() =>
            queries.QueryAsync(mismatched));

        async Task SaveAsync(string id, string definitionId, string lastModifiedAt)
        {
            var content =
                $$"""{"definitionId":"{{definitionId}}","lastModifiedAt":"{{lastModifiedAt}}"}""";
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await writer.SaveAsync(new SaveDocumentRequest(
                    "workflowDefinition",
                    id,
                    "1",
                    content))).Status);
        }
    }
}
