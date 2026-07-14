namespace Groundwork.Core.PhysicalStorage;

public static class PhysicalDocumentFieldPaths
{
    public const string Id = "id";
    public const string DocumentKind = "documentKind";
    public const string StorageScope = "storageScope";
    public const string Version = "version";
    public const string SchemaVersion = "schemaVersion";

    public static bool IsEnvelope(string path) => path is
        Id or DocumentKind or StorageScope or Version or SchemaVersion;

    public static bool IsMutableContent(PhysicalQueryField field) => field.Source switch
    {
        PhysicalQueryFieldSource.CanonicalJsonPath or PhysicalQueryFieldSource.ProjectedColumn => true,
        PhysicalQueryFieldSource.NativeDocumentField => !IsEnvelope(field.Path),
        _ => false
    };
}
