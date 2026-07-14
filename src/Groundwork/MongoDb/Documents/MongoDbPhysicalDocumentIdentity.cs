using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Text;
using Groundwork.Documents.Store;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// Owns the MongoDB representation of a compiled physical document identity. Runtime callers
/// provide original IDs; this module projects and binds the persisted lookup and comparison
/// evidence without reinterpreting the provider-neutral policy.
/// </summary>
internal static class MongoDbPhysicalDocumentIdentity
{
    public static PortableStringIdentityProjection WritePrimary(
        BsonDocument document,
        ExecutableStorageRoute route,
        string originalId)
    {
        var projection = route.Envelope.Identity.Project(originalId);
        Write(document, route.Envelope.Identity, projection);
        return projection;
    }

    public static PortableStringIdentityProjection WriteLinked(
        BsonDocument document,
        ExecutableStorageRoute route,
        string originalId)
    {
        var identity = route.LinkedRelationship?.Identity ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked identity.");
        var projection = identity.Project(originalId);
        Write(document, identity, projection);
        return projection;
    }

    public static FilterDefinition<BsonDocument> PrimaryExactFilter(
        ExecutableStorageRoute route,
        string originalId,
        string scope) =>
        PrimaryExactFilter(route, route.Envelope.Identity.Project(originalId), scope);

    public static FilterDefinition<BsonDocument> PrimaryExactFilter(
        ExecutableStorageRoute route,
        PortableStringIdentityProjection projection,
        string scope) =>
        PrimaryExactFilter(route, scope, projection.LookupKey, projection.ComparisonKey);

    public static FilterDefinition<BsonDocument> PrimaryExactFilter(
        ExecutableStorageRoute route,
        BsonDocument linked)
    {
        var relationship = route.LinkedRelationship ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked identity.");
        return PrimaryExactFilter(
            route,
            linked[relationship.StorageScope.Identifier].AsString,
            linked[relationship.Identity.LookupKey.Identifier].AsString,
            linked[relationship.Identity.ComparisonKey.Identifier].AsString);
    }

    public static FilterDefinition<BsonDocument> PrimaryLookupFilter(
        ExecutableStorageRoute route,
        string scope,
        string lookupKey)
    {
        var values = new BsonDocument
        {
            [route.Discriminator.Column.Identifier] = route.Discriminator.Value,
            [route.ScopeKey.Column.Identifier] = scope,
            [route.Envelope.Identity.LookupKey.Identifier] = lookupKey
        };
        return Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Id,
            MongoDbPhysicalSchemaExecutor.KeyDocument(route.PrimaryKey, values));
    }

    public static void ThrowIfCollision(
        ExecutableStorageRoute route,
        PortableStringIdentityProjection requested,
        BsonDocument retained) =>
        ThrowIfCollision(
            route,
            requested.OriginalValue,
            requested.ComparisonKey,
            requested.LookupKey,
            retained);

    public static void ThrowIfCollision(
        ExecutableStorageRoute route,
        string requestedOriginal,
        string requestedComparison,
        string requestedLookup,
        BsonDocument retained)
    {
        var identity = route.Envelope.Identity;
        var retainedComparison = retained[identity.ComparisonKey.Identifier].AsString;
        if (string.Equals(retainedComparison, requestedComparison, StringComparison.Ordinal))
            return;

        throw new DocumentIdentityLookupCollisionException(
            route.StorageUnit.Value,
            requestedOriginal,
            retained[identity.OriginalId.Identifier].AsString,
            requestedLookup);
    }

    public static FilterDefinition<BsonDocument> LinkedExactFilter(
        ExecutableStorageRoute route,
        string originalId,
        string scope)
    {
        var identity = route.LinkedRelationship?.Identity ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked identity.");
        var projection = identity.Project(originalId);
        return LinkedLookupFilter(route, scope, projection.LookupKey) &
               Builders<BsonDocument>.Filter.Eq(
                   identity.ComparisonKey.Identifier,
                   projection.ComparisonKey);
    }

    public static BsonDocument LinkedKeyDocument(
        ExecutableStorageRoute route,
        BsonDocument linked)
    {
        var relationship = route.LinkedRelationship ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked identity.");
        return LinkedKeyDocument(
            route,
            linked[relationship.StorageScope.Identifier].AsString,
            linked[relationship.Identity.LookupKey.Identifier].AsString);
    }

    private static FilterDefinition<BsonDocument> LinkedLookupFilter(
        ExecutableStorageRoute route,
        string scope,
        string lookupKey) =>
        Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Id,
            LinkedKeyDocument(route, scope, lookupKey));

    private static FilterDefinition<BsonDocument> PrimaryExactFilter(
        ExecutableStorageRoute route,
        string scope,
        string lookupKey,
        string comparisonKey) =>
        PrimaryLookupFilter(route, scope, lookupKey) &
        Builders<BsonDocument>.Filter.Eq(
            route.Envelope.Identity.ComparisonKey.Identifier,
            comparisonKey);

    private static BsonDocument LinkedKeyDocument(
        ExecutableStorageRoute route,
        string scope,
        string lookupKey)
    {
        var relationship = route.LinkedRelationship ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked identity.");
        var values = new BsonDocument
        {
            [relationship.DocumentKind.Identifier] = route.Discriminator.Value,
            [relationship.StorageScope.Identifier] = scope,
            [relationship.Identity.LookupKey.Identifier] = lookupKey
        };
        return MongoDbPhysicalSchemaExecutor.KeyDocument(route.AuxiliaryKey!, values);
    }

    private static void Write(
        BsonDocument document,
        ExecutableDocumentIdentityRoute route,
        PortableStringIdentityProjection projection)
    {
        document[route.OriginalId.Identifier] = projection.OriginalValue;
        document[route.ComparisonKey.Identifier] = projection.ComparisonKey;
        document[route.LookupKey.Identifier] = projection.LookupKey;
    }
}
