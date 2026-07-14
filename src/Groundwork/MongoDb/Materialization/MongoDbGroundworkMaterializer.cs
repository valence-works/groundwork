using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Text;
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

        var storageOperations = plan.Operations.OfType<CreateStorageUnitOperation>().ToArray();
        foreach (var operation in storageOperations)
            await EnsureCollectionAsync(collections, MongoDbGroundworkNames.CollectionName(operation.StorageUnit.Identity), cancellationToken);
        if (storageOperations.Length > 0)
        {
            await EnsureCollectionAsync(collections, MongoDbGroundworkNames.IdentitySchemaCollection, cancellationToken);
            await EnsureCollectionAsync(collections, MongoDbGroundworkNames.IdentitySchemaLockCollection, cancellationToken);
            var admissions = storageOperations
                .Select(operation => new DocumentStoreIdentitySchemaAdmission(
                    new Groundwork.Core.Manifests.StorageUnitIdentity(operation.StorageUnit.Identity),
                    operation.StorageUnit.IdentitySchemaState))
                .ToArray();
            await new IdentitySchemaAdmission(this).AdmitAsync(admissions, cancellationToken);
        }

        foreach (var operation in plan.Operations)
        {
            switch (operation)
            {
                case CreateStorageUnitOperation:
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
        string storageUnit,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(storageUnit));
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
                $"MongoDB identity lookup index on collection '{collection.CollectionNamespace.CollectionName}' conflicts with the required unique Document Store identity schema.",
                exception);
        }

        var indexes = await (await collection.Indexes.ListAsync(cancellationToken)).ToListAsync(cancellationToken);
        var retained = indexes.SingleOrDefault(index => index["name"] == options.Name);
        var retainedKeys = retained?.GetValue("key", new BsonDocument()).AsBsonDocument;
        if (retained is null ||
            !retained.GetValue("unique", false).ToBoolean() ||
            retainedKeys is null ||
            retainedKeys.ElementCount != 2 ||
            retainedKeys.GetElement(0).Name != "storage_scope" || retainedKeys.GetElement(0).Value != 1 ||
            retainedKeys.GetElement(1).Name != "id_lookup_key" || retainedKeys.GetElement(1).Value != 1)
        {
            throw new InvalidOperationException(
                $"MongoDB identity lookup index on collection '{collection.CollectionNamespace.CollectionName}' does not have the required unique Document Store key shape.");
        }
    }

    private async Task AdmitIdentitySchemasAsync(
        IReadOnlyList<DocumentStoreIdentitySchemaAdmission> admissions,
        CancellationToken cancellationToken)
    {
        var lockOwner = await AcquireIdentitySchemaLockAsync(cancellationToken);
        try
        {
            var stateCollection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.IdentitySchemaCollection);
            var plans = new List<MongoIdentityEvolutionPlan>();
            foreach (var admission in admissions)
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", admission.StorageUnit.Value);
                var retainedStateDocument = await stateCollection.Find(filter).SingleOrDefaultAsync(cancellationToken);
                var retainedState = retainedStateDocument is null
                    ? null
                    : DocumentStoreIdentitySchemaState.FromCanonicalJson(retainedStateDocument["state_json"].AsString);
                if (retainedState is not null && retainedState != admission.RequiredState)
                {
                    throw new InvalidOperationException(
                        $"Document Store Storage Unit '{admission.StorageUnit.Value}' identity schema state does not match the materialization target. Forward re-keying requires an explicit schema evolution.");
                }

                plans.Add(await PlanIdentityEvolutionAsync(admission, retainedState is not null, cancellationToken));
            }

            foreach (var plan in plans)
            {
                var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(plan.Admission.StorageUnit.Value));
                foreach (var row in plan.Rows)
                {
                    var result = await collection.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", row.Id),
                        Builders<BsonDocument>.Update
                            .Set("id_original", row.OriginalId)
                            .Set("id_comparison_key", row.Projection.ComparisonKey)
                            .Set("id_lookup_key", row.Projection.LookupKey),
                        cancellationToken: cancellationToken);
                    if (result.MatchedCount != 1)
                        throw new InvalidOperationException("Document Store identity schema evolution lost its authoritative MongoDB document.");
                }
            }

            foreach (var plan in plans)
                await PlanIdentityEvolutionAsync(plan.Admission, hasRecordedState: true, cancellationToken);

            foreach (var plan in plans)
                await EnsureIdentityLookupIndexAsync(plan.Admission.StorageUnit.Value, cancellationToken);

            foreach (var plan in plans.Where(plan => !plan.HasRecordedState))
            {
                await stateCollection.InsertOneAsync(
                    CreateIdentitySchemaDocument(plan.Admission),
                    cancellationToken: cancellationToken);
            }
        }
        finally
        {
            await ReleaseIdentitySchemaLockAsync(lockOwner, CancellationToken.None);
        }
    }

    private async Task<MongoIdentityEvolutionPlan> PlanIdentityEvolutionAsync(
        DocumentStoreIdentitySchemaAdmission admission,
        bool hasRecordedState,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(admission.StorageUnit.Value));
        var documents = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
        var rows = new List<MongoIdentityEvolutionRow>(documents.Count);
        foreach (var document in documents)
        {
            if (!document.TryGetValue("_id", out var idValue) || !idValue.IsBsonDocument ||
                !idValue.AsBsonDocument.TryGetValue("id", out var primaryId) || !primaryId.IsString)
            {
                throw new InvalidOperationException(
                    $"Document Store Storage Unit '{admission.StorageUnit.Value}' has no recoverable original identity in '_id.id'.");
            }

            var id = idValue.AsBsonDocument;
            var originalId = document.TryGetValue("id_original", out var retainedOriginal) && retainedOriginal.IsString
                ? retainedOriginal.AsString
                : primaryId.AsString;
            if (originalId != primaryId.AsString)
            {
                throw new InvalidOperationException(
                    $"Document Store Storage Unit '{admission.StorageUnit.Value}' contains conflicting original identity values.");
            }

            if (!document.TryGetValue("storage_scope", out var scope) || !scope.IsString ||
                !id.TryGetValue("scope", out var primaryScope) || !primaryScope.IsString ||
                scope.AsString != primaryScope.AsString)
            {
                throw new InvalidOperationException(
                    $"Document Store Storage Unit '{admission.StorageUnit.Value}' contains an invalid storage scope identity.");
            }

            var projection = PortableStringComparison.ProjectIdentity(
                originalId,
                PortableStringComparison.ForIdentityPolicy(admission.RequiredState.StringCasePolicy));
            if (hasRecordedState &&
                (!document.TryGetValue("id_comparison_key", out var comparison) || !comparison.IsString || comparison.AsString != projection.ComparisonKey ||
                 !document.TryGetValue("id_lookup_key", out var lookup) || !lookup.IsString || lookup.AsString != projection.LookupKey))
            {
                throw new InvalidOperationException(
                    $"Document Store Storage Unit '{admission.StorageUnit.Value}' contains an identity projection that does not match its recorded original identity.");
            }

            rows.Add(new(id, originalId, scope.AsString, projection));
        }

        var duplicate = rows
            .GroupBy(row => (row.StorageScope, row.Projection.LookupKey))
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Document Store Storage Unit '{admission.StorageUnit.Value}' contains identities that collide under the required identity schema; no schema state was recorded.");
        }

        return new(admission, hasRecordedState, rows);
    }

    private async Task<string> AcquireIdentitySchemaLockAsync(CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.IdentitySchemaLockCollection);
        var owner = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var now = DateTime.UtcNow;
            var filter = Builders<BsonDocument>.Filter.Eq("_id", "document-store-identity-schema") &
                         (Builders<BsonDocument>.Filter.Lt("expires_utc", now) |
                          Builders<BsonDocument>.Filter.Eq("owner", owner));
            var update = Builders<BsonDocument>.Update
                .Set("owner", owner)
                .Set("expires_utc", now.AddMinutes(2));
            try
            {
                var retained = await collection.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument>
                    {
                        IsUpsert = true,
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken);
                if (retained?["owner"].AsString == owner)
                    return owner;
            }
            catch (MongoCommandException exception) when (exception.Code == 11000)
            {
                // Another materializer owns the lease created between our filter and upsert.
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Timed out acquiring the Document Store identity schema evolution lock.");
    }

    private Task ReleaseIdentitySchemaLockAsync(string owner, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.IdentitySchemaLockCollection);
        return collection.DeleteOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", "document-store-identity-schema") &
            Builders<BsonDocument>.Filter.Eq("owner", owner),
            cancellationToken);
    }

    private static BsonDocument CreateIdentitySchemaDocument(DocumentStoreIdentitySchemaAdmission admission) =>
        new()
        {
            ["_id"] = admission.StorageUnit.Value,
            ["state_json"] = admission.RequiredState.ToCanonicalJson()
        };

    private sealed record MongoIdentityEvolutionPlan(
        DocumentStoreIdentitySchemaAdmission Admission,
        bool HasRecordedState,
        IReadOnlyList<MongoIdentityEvolutionRow> Rows);

    private sealed record MongoIdentityEvolutionRow(
        BsonDocument Id,
        string OriginalId,
        string StorageScope,
        Groundwork.Core.Text.PortableStringIdentityProjection Projection);

    private sealed class IdentitySchemaAdmission(MongoDbGroundworkMaterializer materializer)
        : IDocumentStoreIdentitySchemaAdmission
    {
        public Task AdmitAsync(
            IReadOnlyList<DocumentStoreIdentitySchemaAdmission> admissions,
            CancellationToken cancellationToken = default) =>
            materializer.AdmitIdentitySchemasAsync(admissions, cancellationToken);
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
