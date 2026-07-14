using System.Diagnostics;
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
    private const int DuplicateKeyErrorCode = 11000;
    private const string IdentitySchemaLockId = "document-store-identity-schema";
    private static readonly TimeSpan IdentitySchemaLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly MongoDbIdentitySchemaAdmissionHooks identitySchemaHooks =
        MongoDbIdentitySchemaAdmissionHooks.None;

    internal MongoDbGroundworkMaterializer(
        IMongoDatabase database,
        MongoDbIdentitySchemaAdmissionHooks identitySchemaHooks)
        : this(database)
    {
        this.identitySchemaHooks = identitySchemaHooks;
    }

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
                .Select(operation => new IdentitySchemaAdmission(
                    new Groundwork.Core.Manifests.StorageUnitIdentity(operation.StorageUnit.Identity),
                    operation.StorageUnit.IdentitySchemaState))
                .ToArray();
            await AdmitIdentitySchemasAsync(admissions, cancellationToken);
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
        IReadOnlyList<IdentitySchemaAdmission> admissions,
        CancellationToken cancellationToken)
    {
        await MongoDbTransactionCapability.ForDatabase(database).EnsureSupportedAsync(
            admissions.Select(admission => admission.StorageUnit.Value).ToArray(),
            "Document Store identity-schema admission",
            cancellationToken);
        var lease = await AcquireIdentitySchemaLeaseAsync(cancellationToken);
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

            var committedRows = 0;
            foreach (var plan in plans)
            {
                var collection = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.CollectionName(plan.Admission.StorageUnit.Value));
                foreach (var row in plan.Rows)
                {
                    await ExecuteWithIdentitySchemaLeaseAsync(
                        lease,
                        async session =>
                        {
                            var result = await collection.UpdateOneAsync(
                                session,
                                Builders<BsonDocument>.Filter.Eq("_id", row.Id),
                                Builders<BsonDocument>.Update
                                    .Set("id_original", row.OriginalId)
                                    .Set("id_comparison_key", row.Projection.ComparisonKey)
                                    .Set("id_lookup_key", row.Projection.LookupKey),
                                cancellationToken: cancellationToken);
                            if (result.MatchedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    "Document Store identity schema evolution lost its authoritative MongoDB document.");
                            }
                        },
                        cancellationToken);
                    await identitySchemaHooks.BackfillRowCommitted(++committedRows, cancellationToken);
                }
            }

            foreach (var plan in plans)
            {
                await AssertAndRenewIdentitySchemaLeaseAsync(lease, session: null, cancellationToken);
                await PlanIdentityEvolutionAsync(plan.Admission, hasRecordedState: true, cancellationToken);
            }

            foreach (var plan in plans)
            {
                await AssertAndRenewIdentitySchemaLeaseAsync(lease, session: null, cancellationToken);
                await EnsureIdentityLookupIndexAsync(plan.Admission.StorageUnit.Value, cancellationToken);
                await AssertAndRenewIdentitySchemaLeaseAsync(lease, session: null, cancellationToken);
            }

            var missingStates = plans.Where(plan => !plan.HasRecordedState).ToArray();
            if (missingStates.Length > 0)
            {
                await ExecuteWithIdentitySchemaLeaseAsync(
                    lease,
                    async session =>
                    {
                        foreach (var plan in missingStates)
                        {
                            await stateCollection.InsertOneAsync(
                                session,
                                CreateIdentitySchemaDocument(plan.Admission),
                                cancellationToken: cancellationToken);
                        }
                    },
                    cancellationToken);
            }
        }
        finally
        {
            await ReleaseIdentitySchemaLeaseAsync(lease, CancellationToken.None);
        }
    }

    private async Task<MongoIdentityEvolutionPlan> PlanIdentityEvolutionAsync(
        IdentitySchemaAdmission admission,
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

    private async Task<IdentitySchemaLease> AcquireIdentitySchemaLeaseAsync(CancellationToken cancellationToken)
    {
        var collection = IdentitySchemaLeaseCollection();
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            await collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", IdentitySchemaLockId),
                Builders<BsonDocument>.Update
                    .SetOnInsert("expires_utc", DateTime.UnixEpoch)
                    .SetOnInsert("fence", 0L),
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == DuplicateKeyErrorCode)
        {
            // Another contender initialized the singleton lease document.
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == DuplicateKeyErrorCode)
        {
            // Another contender initialized the singleton lease document.
        }

        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = new BsonDocument
            {
                ["_id"] = IdentitySchemaLockId,
                ["$expr"] = new BsonDocument("$lte", new BsonArray
                {
                    new BsonDocument("$ifNull", new BsonArray
                    {
                        "$expires_utc",
                        new BsonDateTime(DateTime.UnixEpoch)
                    }),
                    "$$NOW"
                })
            };
            var update = ServerTimedLeaseUpdate(owner, incrementFence: true);
            var retained = await collection.FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            if (retained?["owner"].AsString == owner)
            {
                return new IdentitySchemaLease(owner, retained["fence"].ToInt64());
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Timed out acquiring the Document Store identity schema evolution lock.");
    }

    private async Task AssertAndRenewIdentitySchemaLeaseAsync(
        IdentitySchemaLease lease,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var collection = IdentitySchemaLeaseCollection();
        var filter = Builders<BsonDocument>.Filter.Eq("_id", IdentitySchemaLockId) &
                     Builders<BsonDocument>.Filter.Eq("owner", lease.Owner) &
                     Builders<BsonDocument>.Filter.Eq("fence", lease.Fence) &
                     new BsonDocumentFilterDefinition<BsonDocument>(new BsonDocument(
                         "$expr",
                         new BsonDocument("$gt", new BsonArray { "$expires_utc", "$$NOW" })));
        var update = ServerTimedLeaseUpdate(lease.Owner, incrementFence: false);
        var result = session is null
            ? await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)
            : await collection.UpdateOneAsync(session, filter, update, cancellationToken: cancellationToken);
        if (result.MatchedCount != 1)
        {
            throw new InvalidOperationException(
                $"MongoDB Document Store identity-schema lease is no longer owned by fence {lease.Fence}.");
        }
    }

    private async Task ExecuteWithIdentitySchemaLeaseAsync(
        IdentitySchemaLease lease,
        Func<IClientSessionHandle, Task> body,
        CancellationToken cancellationToken)
    {
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try
        {
            await AssertAndRenewIdentitySchemaLeaseAsync(lease, session, cancellationToken);
            await body(session);
            await session.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task ReleaseIdentitySchemaLeaseAsync(
        IdentitySchemaLease lease,
        CancellationToken cancellationToken)
    {
        var collection = IdentitySchemaLeaseCollection();
        var filter = Builders<BsonDocument>.Filter.Eq("_id", IdentitySchemaLockId) &
                     Builders<BsonDocument>.Filter.Eq("owner", lease.Owner) &
                     Builders<BsonDocument>.Filter.Eq("fence", lease.Fence);
        var update = new PipelineUpdateDefinition<BsonDocument>(new BsonDocument[]
        {
            new("$set", new BsonDocument("expires_utc", "$$NOW")),
            new("$unset", "owner")
        });
        await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
    }

    private IMongoCollection<BsonDocument> IdentitySchemaLeaseCollection() =>
        database
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority)
            .GetCollection<BsonDocument>(MongoDbGroundworkNames.IdentitySchemaLockCollection);

    private static PipelineUpdateDefinition<BsonDocument> ServerTimedLeaseUpdate(
        string owner,
        bool incrementFence)
    {
        BsonValue fence = incrementFence
            ? new BsonDocument("$add", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$fence", 0L }),
                1L
            })
            : new BsonString("$fence");
        return new PipelineUpdateDefinition<BsonDocument>(new BsonDocument[]
        {
            new("$set", new BsonDocument
            {
                ["owner"] = owner,
                ["fence"] = fence,
                ["expires_utc"] = new BsonDocument("$dateAdd", new BsonDocument
                {
                    ["startDate"] = "$$NOW",
                    ["unit"] = "millisecond",
                    ["amount"] = (long)IdentitySchemaLeaseDuration.TotalMilliseconds
                })
            })
        });
    }

    private static BsonDocument CreateIdentitySchemaDocument(IdentitySchemaAdmission admission) =>
        new()
        {
            ["_id"] = admission.StorageUnit.Value,
            ["state_json"] = admission.RequiredState.ToCanonicalJson()
        };

    private sealed record MongoIdentityEvolutionPlan(
        IdentitySchemaAdmission Admission,
        bool HasRecordedState,
        IReadOnlyList<MongoIdentityEvolutionRow> Rows);

    private sealed record MongoIdentityEvolutionRow(
        BsonDocument Id,
        string OriginalId,
        string StorageScope,
        Groundwork.Core.Text.PortableStringIdentityProjection Projection);

    private sealed record IdentitySchemaLease(string Owner, long Fence);

    private sealed record IdentitySchemaAdmission(
        Groundwork.Core.Manifests.StorageUnitIdentity StorageUnit,
        DocumentStoreIdentitySchemaState RequiredState);

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

internal sealed record MongoDbIdentitySchemaAdmissionHooks(
    Func<int, CancellationToken, ValueTask> BackfillRowCommitted)
{
    public static MongoDbIdentitySchemaAdmissionHooks None { get; } = new(
        static (_, _) => ValueTask.CompletedTask);
}
