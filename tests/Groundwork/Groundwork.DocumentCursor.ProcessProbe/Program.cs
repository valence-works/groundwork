using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;

if (args is not [var operation, var databasePath, ..])
    throw new ArgumentException("Usage: <seed|resume> <database-path> [continuation]");

var (manifest, target) = CreateModel();
await using var connection = new SqliteConnection($"Data Source={databasePath}");
await connection.OpenAsync();
if (operation == "seed")
    await PhysicalSchemaApplication.ApplyAsync(target, new SqlitePhysicalSchemaExecutor(connection));
var documents = new SqlitePhysicalDocumentStore(
    connection,
    manifest,
    target.Routes,
    DocumentStoreAccess.Global);
var queryStore = SqlitePhysicalQueryRuntime.Create(
    documents,
    manifest,
    target.Routes.Single(),
    target.Provider);
var query = new DocumentQuery(
    "configurationDocument",
    "list-by-category",
    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("category", "tools"))],
    [new DocumentQueryOrder("category")],
    take: operation == "seed" ? 1 : 2,
    continuation: operation == "resume"
        ? args.ElementAtOrDefault(2) ?? throw new ArgumentException("Resume requires a continuation.")
        : null);

if (operation == "seed")
{
    foreach (var id in new[] { "c", "a", "b" })
    {
        await documents.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            id,
            "1",
            """{"category":"tools"}"""));
    }
}
else if (operation != "resume")
{
    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Expected seed or resume.");
}

var page = await queryStore.QueryAsync(query);
Console.WriteLine(JsonSerializer.Serialize(new ProbeResult(
    page.Documents.Select(document => document.Id).ToArray(),
    page.NextContinuation,
    page.TotalCount)));
return;

static (StorageManifest Manifest, PhysicalSchemaTarget Target) CreateModel()
{
    var envelope = new DocumentEnvelopeDefinition();
    var logical = new LogicalIndexDeclaration(
        "by-category",
        [new IndexField("category")],
        IndexValueKind.String,
        false,
        MissingValueBehavior.Excluded);
    var query = new BoundedQueryDeclaration(
        "list-by-category",
        logical.Identity,
        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
        QuerySortSupport.Ascending,
        QueryPagingSupport.Cursor,
        BoundedQueryExecutionClass.ScaleBearing,
        supportsTotalCount: true);
    var definition = PhysicalTableDefinition.PhysicalEntityTable(
        "configuration_entities",
        [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: 100)],
        envelope,
        [
            new PhysicalIndexDefinition(
                logical.Identity,
                [
                    new PhysicalIndexColumnDefinition("category", 0),
                    new PhysicalIndexColumnDefinition(envelope.IdLookupKeyColumn, 1)
                ])
        ]);
    var unit = new StorageUnit(
        new StorageUnitIdentity("configurationDocument"),
        "Configuration document",
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
            [logical],
            [query])
    };
    var manifest = new StorageManifest(
        new StorageManifestIdentity("document-cursor-process-probe"),
        new StorageManifestOwner("groundwork-tests"),
        new StorageManifestVersion("1"),
        [unit],
        new HashSet<string>(),
        []);
    var resolution = PhysicalStorageResolver.Resolve(
        manifest,
        PhysicalNamePolicy.Identity,
        SqliteGroundworkCapabilities.PhysicalNames);
    if (!resolution.IsValid)
        throw new InvalidOperationException(string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
    var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
    if (!compilation.IsValid)
        throw new InvalidOperationException(string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
    return (
        manifest,
        new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            SqliteGroundworkCapabilities.Provider,
            compilation.Routes));
}

internal sealed record ProbeResult(IReadOnlyList<string> Ids, string? Continuation, long TotalCount);
