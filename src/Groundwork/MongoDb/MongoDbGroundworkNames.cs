using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;

namespace Groundwork.MongoDb;

public static class MongoDbGroundworkNames
{
    public const int MaxEncodedIdentityLength = 120;
    public const int MaxPhysicalizedFieldNameLength = 120;
    public const string SchemaHistoryCollection = "groundwork_schema_history";
    public const string IdentitySchemaCollection = "groundwork_document_identity_schema";
    public const string IdentitySchemaLockCollection = "groundwork_document_identity_schema_locks";

    public static string CollectionName(StorageUnit unit) => CollectionName(unit.Identity.Value);

    public static string CollectionName(string unitIdentity) => $"groundwork_{PhysicalizationNameEncoder.Encode(unitIdentity, MaxEncodedIdentityLength)}";

    public static string PhysicalizedFieldName(PhysicalizedFieldPlan field) => PhysicalizationNameEncoder.Encode(field.Name, MaxPhysicalizedFieldNameLength);
}
