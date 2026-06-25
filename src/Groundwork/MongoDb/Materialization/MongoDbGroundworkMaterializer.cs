using Groundwork.Core.Indexing;
using Groundwork.Core.Physicalization;
using Groundwork.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Materialization;

public sealed class MongoDbGroundworkMaterializer(IMongoDatabase database, Action<string>? recordAdvisory = null)
{
    private const int IndexOptionsConflictErrorCode = 85;
    private const int IndexKeySpecsConflictErrorCode = 86;
    private const int NamespaceExistsErrorCode = 48;

    public async Task MaterializeAsync(MaterializationPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.IsPlannable)
            throw new InvalidOperationException("Cannot execute an unplannable materialization plan.");

        var collections = await GetCollectionNamesAsync(cancellationToken);
        var physicalizedFields = plan.Operations
            .OfType<CreateOptimizedProjectionOperation>()
            .SelectMany(operation => operation.Projection.Fields.Select(field => new
            {
                operation.Projection.UnitIdentity,
                Field = field
            }))
            .ToDictionary(
                item => (item.UnitIdentity, item.Field.Name),
                item => item.Field);

        foreach (var operation in plan.Operations)
        {
            switch (operation)
            {
                case CreateStorageUnitOperation storageUnit:
                    await EnsureCollectionAsync(collections, MongoDbGroundworkNames.CollectionName(storageUnit.StorageUnit.Identity), cancellationToken);
                    break;
                case CreateIndexOperation index:
                    await EnsureIndexAsync(index.Index, physicalizedFields, cancellationToken);
                    break;
                case CreateOptimizedProjectionOperation:
                    break;
                case RecordSchemaHistoryOperation history:
                    await EnsureCollectionAsync(collections, MongoDbGroundworkNames.SchemaHistoryCollection, cancellationToken);
                    await EnsureSchemaHistoryIndexAsync(cancellationToken);
                    await RecordSchemaHistoryAsync(history.Entry, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported materialization operation '{operation.Kind}'.");
            }
        }
    }

    private async Task<HashSet<string>> GetCollectionNamesAsync(CancellationToken cancellationToken)
    {
        var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var names = await cursor.ToListAsync(cancellationToken);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    private async Task EnsureCollectionAsync(HashSet<string> collections, string collectionName, CancellationToken cancellationToken)
    {
        if (collections.Contains(collectionName))
            return;

        try
        {
            await database.CreateCollectionAsync(collectionName, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceExistsErrorCode)
        {
            // NamespaceExists means another materializer created the collection after our initial snapshot.
        }

        collections.Add(collectionName);
    }

    private async Task EnsureSchemaHistoryIndexAsync(CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.SchemaHistoryCollection);
        var keys = Builders<BsonDocument>.IndexKeys
            .Ascending("manifest_id")
            .Ascending("manifest_version")
            .Ascending("provider_name")
            .Ascending("provider_version");
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "ux_groundwork_schema_history_identity",
            Unique = true
        });
        await CreateIndexWithAdvisoryAsync(collection, model, model.Options.Name, cancellationToken);
    }

    private async Task EnsureIndexAsync(
        MaterializedIndex index,
        IReadOnlyDictionary<(string UnitIdentity, string FieldName), PhysicalizedFieldPlan> physicalizedFields,
        CancellationToken cancellationToken)
    {
        if (index.FieldPaths.Count != 1)
            return;

        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(index.UnitIdentity));
        var path = !physicalizedFields.TryGetValue((index.UnitIdentity, index.Identity), out var physicalizedField)
            ? $"content.{index.FieldPaths[0]}"
            : $"physicalized.{MongoDbGroundworkNames.PhysicalizedFieldName(physicalizedField)}";
        var keys = Builders<BsonDocument>.IndexKeys.Ascending(path);
        var options = new CreateIndexOptions
        {
            Name = index.Identity,
            Unique = index.IsUnique,
            Sparse = index.MissingValueBehavior == MissingValueBehavior.Excluded
        };
        await CreateIndexWithAdvisoryAsync(collection, new CreateIndexModel<BsonDocument>(keys, options), index.Identity, cancellationToken);
    }

    private async Task CreateIndexWithAdvisoryAsync(
        IMongoCollection<BsonDocument> collection,
        CreateIndexModel<BsonDocument> model,
        string indexName,
        CancellationToken cancellationToken)
    {
        try
        {
            await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException ex) when (IsIndexConflictException(ex))
        {
            recordAdvisory?.Invoke(
                $"MongoDB index '{indexName}' on collection '{collection.CollectionNamespace.CollectionName}' conflicts with the declared Groundwork index. Drop or rebuild the existing index to apply changed index keys or options.");
        }
    }

    private static bool IsIndexConflictException(MongoCommandException exception) =>
        exception.Code is IndexOptionsConflictErrorCode or IndexKeySpecsConflictErrorCode;

    private async Task RecordSchemaHistoryAsync(SchemaHistoryEntry history, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.SchemaHistoryCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("manifest_id", history.ManifestIdentity.Value) &
                     Builders<BsonDocument>.Filter.Eq("manifest_version", history.ManifestVersion.Value) &
                     Builders<BsonDocument>.Filter.Eq("provider_name", history.Provider.Name) &
                     Builders<BsonDocument>.Filter.Eq("provider_version", history.Provider.Version);
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("manifest_id", history.ManifestIdentity.Value)
            .SetOnInsert("manifest_version", history.ManifestVersion.Value)
            .SetOnInsert("provider_name", history.Provider.Name)
            .SetOnInsert("provider_version", history.Provider.Version)
            .SetOnInsert("applied_utc", DateTimeOffset.UtcNow.ToString("O"));

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
