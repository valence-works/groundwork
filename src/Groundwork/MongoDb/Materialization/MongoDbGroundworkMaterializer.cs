using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Materialization;

public sealed class MongoDbGroundworkMaterializer(IMongoDatabase database, Action<string>? recordAdvisory = null)
{
    private const int IndexOptionsConflictErrorCode = 85;
    private const int IndexKeySpecsConflictErrorCode = 86;
    private const int NamespaceExistsErrorCode = 48;

    public Task<PhysicalSchemaApplicationResult> MaterializeAsync(
        MongoDbPhysicalStorageModel model,
        CancellationToken cancellationToken = default) =>
        MaterializeAsync(model, MongoDbTransactionCapability.ForDatabase(database), cancellationToken);

    internal async Task<PhysicalSchemaApplicationResult> MaterializeAsync(
        MongoDbPhysicalStorageModel model,
        MongoDbTransactionCapability transactionCapability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transactionCapability);
        await transactionCapability.EnsureSupportedAsync(
            model.Routes.Select(route => route.StorageUnit.Value).ToArray(),
            "physical schema application",
            cancellationToken);
        var result = await PhysicalSchemaApplication.ApplyAsync(
            model.Target,
            new MongoDbPhysicalSchemaExecutor(database),
            cancellationToken: cancellationToken);
        return result;
    }

    public async Task MaterializeAsync(MaterializationPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.IsPlannable)
            throw new InvalidOperationException("Cannot execute an unplannable materialization plan.");

        var collections = await GetCollectionsAsync(cancellationToken);
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
                    await EnsureIdentityLookupIndexAsync(storageUnit.StorageUnit, cancellationToken);
                    await EnsureCollectionAsync(collections, MongoDbGroundworkNames.IdentitySchemaCollection, cancellationToken);
                    await AdmitIdentityPolicyAsync(storageUnit.StorageUnit, cancellationToken);
                    break;
                case CreateIndexOperation index:
                    await EnsureIndexAsync(index.Index, physicalizedFields, cancellationToken);
                    break;
                case BackfillCanonicalJsonOperation:
                    // A native MongoDB index covers documents that predate index creation.
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

    private async Task<Dictionary<string, BsonDocument>> GetCollectionsAsync(CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        var collections = await cursor.ToListAsync(cancellationToken);
        return collections.ToDictionary(
            collection => collection["name"].AsString,
            collection => collection,
            StringComparer.Ordinal);
    }

    private async Task EnsureCollectionAsync(
        Dictionary<string, BsonDocument> collections,
        string collectionName,
        CancellationToken cancellationToken)
    {
        if (collections.TryGetValue(collectionName, out var existing))
        {
            ValidateSimpleBinaryCollation(collectionName, existing);
            return;
        }

        try
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions { Collation = new Collation("simple") },
                cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceExistsErrorCode)
        {
            // NamespaceExists means another materializer created the collection after our initial snapshot.
        }

        var created = await GetCollectionAsync(collectionName, cancellationToken);
        ValidateSimpleBinaryCollation(collectionName, created);
        collections.Add(collectionName, created);
    }

    private async Task<BsonDocument> GetCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions
            {
                Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName)
            },
            cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).Single();
    }

    private static void ValidateSimpleBinaryCollation(string collectionName, BsonDocument collection)
    {
        var options = collection.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (!options.TryGetValue("collation", out var collation) ||
            collation.AsBsonDocument.GetValue("locale", "simple").AsString == "simple")
        {
            return;
        }

        throw new InvalidOperationException(
            $"MongoDB collection '{collectionName}' must use the simple binary collation for exact Groundwork key and storage-scope semantics.");
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

    private async Task EnsureIdentityLookupIndexAsync(
        MaterializedStorageUnit storageUnit,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(storageUnit.Identity));
        var keys = Builders<BsonDocument>.IndexKeys
            .Ascending("storage_scope")
            .Ascending("id_lookup_key");
        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = "ux_groundwork_document_identity_lookup",
            Unique = true
        };
        try
        {
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(keys, options),
                cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (IsIndexConflictException(exception))
        {
            throw new InvalidOperationException(
                $"MongoDB identity lookup index on collection '{collection.CollectionNamespace.CollectionName}' conflicts with the required unique conventional-store identity schema.",
                exception);
        }
    }

    private async Task AdmitIdentityPolicyAsync(
        MaterializedStorageUnit storageUnit,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.IdentitySchemaCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", storageUnit.Identity);
        var retained = await collection.Find(filter).SingleOrDefaultAsync(cancellationToken);
        if (retained is null)
        {
            try
            {
                await collection.InsertOneAsync(
                    CreateIdentitySchemaDocument(storageUnit),
                    cancellationToken: cancellationToken);
                return;
            }
            catch (MongoWriteException exception) when (IsDuplicateKey(exception))
            {
                retained = await collection.Find(filter).SingleAsync(cancellationToken);
            }
        }

        if (retained["string_case_policy"].AsString == storageUnit.StringIdentityCasePolicy.ToString() &&
            retained["comparison_algorithm"].AsString == storageUnit.ComparisonAlgorithmId &&
            retained["lookup_algorithm"].AsString == storageUnit.LookupAlgorithmId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Conventional document kind '{storageUnit.Identity}' identity policy or algorithm state does not match the materialization target. Drop and recreate the schema; automatic re-keying is not supported.");
    }

    private static BsonDocument CreateIdentitySchemaDocument(MaterializedStorageUnit storageUnit) =>
        new()
        {
            ["_id"] = storageUnit.Identity,
            ["string_case_policy"] = storageUnit.StringIdentityCasePolicy.ToString(),
            ["comparison_algorithm"] = storageUnit.ComparisonAlgorithmId,
            ["lookup_algorithm"] = storageUnit.LookupAlgorithmId
        };

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
        var keys = Builders<BsonDocument>.IndexKeys.Ascending("storage_scope").Ascending(path);
        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = index.Identity,
            Unique = index.IsUnique,
            PartialFilterExpression = index.MissingValueBehavior == MissingValueBehavior.Excluded
                ? new BsonDocument(path, new BsonDocument("$exists", true))
                : null
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

    private static bool IsDuplicateKey(MongoWriteException exception) =>
        exception.WriteError?.Code == 11000;

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
