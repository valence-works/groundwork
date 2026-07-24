using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using System.Globalization;
using System.Text.Json;
using Groundwork.MongoDb.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Materialization;

/// <summary>Durable MongoDB implementation of the additive physical-schema execution boundary.</summary>
public sealed class MongoDbPhysicalSchemaExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
{
    private const string AppliedStateCollection = "groundwork_physical_schema_state";
    private const string OperationCollection = "groundwork_physical_schema_operations";
    private const string LockCollection = "groundwork_physical_schema_locks";
    private const int NamespaceExists = 48;

    /// <summary>
    /// Smallest supported application-lease duration. MongoDB persists lease deadlines at
    /// millisecond precision, and the renewal loop needs scheduling headroom before expiry.
    /// </summary>
    public static TimeSpan MinimumLeaseDuration { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Default application-lease duration used when the host does not specify one.</summary>
    public static TimeSpan DefaultLeaseDuration { get; } = TimeSpan.FromMinutes(5);
    private readonly IMongoDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan leaseDuration;
    private readonly Func<CancellationToken, ValueTask>? beforeBackfillWrite;
    private readonly Func<CancellationToken, ValueTask>? beforeOperationEvidenceWrite;
    private readonly Func<CancellationToken, ValueTask>? beforeAppliedStateWrite;
    private readonly Action<DateTimeOffset>? afterLeaseRenewal;
    private readonly MongoDbPhysicalMutationSchemaDefinitionHandler mutationDefinitions;

    public MongoDbPhysicalSchemaExecutor(
        IMongoDatabase database,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null)
        : this(database, timeProvider, leaseDuration, null, null, null, null)
    {
    }

    internal MongoDbPhysicalSchemaExecutor(
        IMongoDatabase database,
        TimeProvider? timeProvider,
        TimeSpan? leaseDuration,
        Func<CancellationToken, ValueTask>? beforeBackfillWrite,
        Func<CancellationToken, ValueTask>? beforeOperationEvidenceWrite = null,
        Func<CancellationToken, ValueTask>? beforeAppliedStateWrite = null,
        Action<DateTimeOffset>? afterLeaseRenewal = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database
            .WithReadConcern(ReadConcern.Majority)
            .WithWriteConcern(WriteConcern.WMajority);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (this.leaseDuration < MinimumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                $"MongoDB physical-schema lease duration must be at least {MinimumLeaseDuration}.");
        }
        this.beforeBackfillWrite = beforeBackfillWrite;
        this.beforeOperationEvidenceWrite = beforeOperationEvidenceWrite;
        this.beforeAppliedStateWrite = beforeAppliedStateWrite;
        this.afterLeaseRenewal = afterLeaseRenewal;
        mutationDefinitions = new MongoDbPhysicalMutationSchemaDefinitionHandler(
            this.database,
            beforeBackfillWrite);
    }

    public async ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        await EnsureInfrastructureAsync(cancellationToken);
        var owner = Guid.NewGuid().ToString("N");
        var locks = database.GetCollection<BsonDocument>(LockCollection);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            var filter = Builders<BsonDocument>.Filter.Eq("_id", TargetIdentityDocument(target)) &
                         Builders<BsonDocument>.Filter.Lte("expires_at", now.UtcDateTime);
            var update = Builders<BsonDocument>.Update
                .Set("owner", owner)
                .Set("expires_at", now.Add(leaseDuration).UtcDateTime)
                .Inc("fence", 1L);
            try
            {
                var document = await locks.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument>
                    {
                        IsUpsert = true,
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken);
                if (document.GetValue("owner").AsString == owner)
                {
                    return new ApplicationLock(
                        locks,
                        target,
                        owner,
                        document.GetValue("fence").ToInt64(),
                        leaseDuration,
                        timeProvider,
                        afterLeaseRenewal);
                }
            }
            catch (MongoCommandException exception) when (exception.Code == 11000)
            {
                // Another applicant owns the non-expired target lease.
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
            {
                // Another applicant owns the non-expired target lease.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider, cancellationToken);
        }
    }

    public async ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        var lease = RequireLease(applicationLock, target);
        await lease.AssertOwnedAsync(session: null, cancellationToken);
        return await ReadInspectedHistoryAsync(
            target,
            validateDurableEvidence: true,
            cancellationToken);
    }

    public ValueTask<PhysicalSchemaInspectionResult> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ValueTask<PhysicalSchemaInspectionResult>(InspectTargetAsync(target, cancellationToken));
    }

    private async Task<PhysicalSchemaInspectionResult> InspectTargetAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        var history = await ReadInspectedHistoryAsync(
            target.Identity,
            validateDurableEvidence: false,
            cancellationToken);
        var isAppliedSchemaValid = true;
        if (history.AppliedState is { } appliedState)
        {
            try
            {
                await ValidateDurableEvidenceAsync(appliedState, cancellationToken);
                await ValidateAsync(
                    ValidatePhysicalSchemaOperation.ForAppliedState(appliedState),
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                isAppliedSchemaValid = false;
            }
        }
        return new PhysicalSchemaInspectionResult(history, isAppliedSchemaValid);
    }

    private async Task<PhysicalSchemaHistoryState> ReadInspectedHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        bool validateDurableEvidence,
        CancellationToken cancellationToken)
    {
        var collectionNames = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        if (!collectionNames.Contains(AppliedStateCollection, StringComparer.Ordinal))
            return await ReadLegacyHistoryAsync(target, collectionNames, cancellationToken);

        var state = await database.GetCollection<BsonDocument>(AppliedStateCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", TargetIdentityDocument(target)))
            .SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
        {
            var applied = PhysicalSchemaAppliedStateSerializer.Deserialize(state.GetValue("state").AsString);
            if (validateDurableEvidence)
                await ValidateDurableEvidenceAsync(applied, cancellationToken);
            return PhysicalSchemaHistoryState.FromApplied(applied);
        }
        return await ReadLegacyHistoryAsync(target, collectionNames, cancellationToken);
    }

    private async Task<PhysicalSchemaHistoryState> ReadLegacyHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyCollection<string> collectionNames,
        CancellationToken cancellationToken)
    {
        if (!collectionNames.Contains(MongoDbGroundworkNames.SchemaHistoryCollection, StringComparer.Ordinal))
            return PhysicalSchemaHistoryState.Empty;
        var legacyHistory = database.GetCollection<BsonDocument>(MongoDbGroundworkNames.SchemaHistoryCollection);
        var legacyHistoryFilter = Builders<BsonDocument>.Filter.Eq("manifest_id", target.ManifestIdentity.Value) &
                                  Builders<BsonDocument>.Filter.Eq("provider_name", target.ProviderName);
        return await legacyHistory.Find(legacyHistoryFilter).Limit(1).AnyAsync(cancellationToken)
            ? PhysicalSchemaHistoryState.LegacyHistoryDetected
            : PhysicalSchemaHistoryState.Empty;
    }

    public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(operation);
        var lease = RequireLease(applicationLock, target);
        await lease.AssertOwnedAsync(session: null, cancellationToken);
        var ledger = database.GetCollection<BsonDocument>(OperationCollection);
        var evidenceIdentity = OperationEvidenceIdentity(target, operation.Identity);
        var existing = await ledger.Find(Builders<BsonDocument>.Filter.Eq("_id", evidenceIdentity))
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var acknowledgement = AcknowledgeExisting(operation, existing);
            if (operation is ValidatePhysicalSchemaOperation)
            {
                await ExecuteAsync(operation, cancellationToken);
                return acknowledgement;
            }
            if (await IsOperationPublishedAsync(operation, lease.Target, cancellationToken))
                return acknowledgement;
            // Execution evidence from an attempt that never published its target is not a skip
            // token. Re-run the additive/idempotent operation so backfills observe all writes
            // made while the previous attempt was incomplete.
        }

        await ExecuteAsync(operation, cancellationToken);
        if (beforeOperationEvidenceWrite is not null)
            await beforeOperationEvidenceWrite(cancellationToken);
        var appliedAt = timeProvider.GetUtcNow();
        var evidence = new BsonDocument
        {
            ["_id"] = evidenceIdentity,
            ["target_id"] = TargetIdentityDocument(target),
            ["operation_id"] = operation.Identity,
            ["fingerprint"] = operation.Fingerprint,
            ["kind"] = operation.Kind.ToString(),
            ["applied_at"] = appliedAt.UtcDateTime
        };
        if (operation is CreatePhysicalIndexOperation index)
        {
            var partialFilter = MongoDbPhysicalIndexSemantics.PartialFilter(index.Route, index.Index);
            evidence["collection"] = index.Storage.Name.Identifier;
            evidence["index_name"] = index.Index.Name.Identifier;
            evidence["index_keys"] = IndexKeys(index);
            evidence["unique"] = index.Index.IsUnique;
            evidence["missing_value_behavior"] = index.Index.MissingValueBehavior.ToString();
            evidence["collation"] = new BsonDocument("locale", "simple");
            evidence["sparse"] = false;
            evidence["hidden"] = false;
            if (partialFilter is not null)
                evidence["partial_filter_expression"] = partialFilter;
        }
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try
        {
            await lease.AssertOwnedAsync(session, cancellationToken);
            existing = await ledger.Find(session, Builders<BsonDocument>.Filter.Eq("_id", evidenceIdentity))
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                AcknowledgeExisting(operation, existing);
                await ledger.ReplaceOneAsync(
                    session,
                    Builders<BsonDocument>.Filter.Eq("_id", evidenceIdentity),
                    evidence,
                    cancellationToken: cancellationToken);
                await session.CommitTransactionAsync(cancellationToken);
                return new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    appliedAt);
            }
            await ledger.InsertOneAsync(session, evidence, cancellationToken: cancellationToken);
            await session.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }
        return new PhysicalSchemaOperationAcknowledgement(operation.Identity, operation.Fingerprint, appliedAt);
    }

    private async Task<bool> IsOperationPublishedAsync(
        PhysicalSchemaOperation operation,
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        var document = await database.GetCollection<BsonDocument>(AppliedStateCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", TargetIdentityDocument(target)))
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null)
            return false;

        var state = PhysicalSchemaAppliedStateSerializer.Deserialize(document.GetValue("state").AsString);
        return state.AppliedOperations.Any(applied =>
            applied.Identity == operation.Identity &&
            applied.Fingerprint == operation.Fingerprint);
    }

    public async ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var targetIdentity = new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name);
        var target = TargetIdentityDocument(targetIdentity);
        var lease = RequireLease(applicationLock, targetIdentity);
        if (beforeAppliedStateWrite is not null)
            await beforeAppliedStateWrite(cancellationToken);
        var collection = database.GetCollection<BsonDocument>(AppliedStateCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", target);
        filter &= expectedAppliedTargetFingerprint is null
            ? Builders<BsonDocument>.Filter.Exists("target_fingerprint", false)
            : Builders<BsonDocument>.Filter.Eq("target_fingerprint", expectedAppliedTargetFingerprint);
        var replacement = new BsonDocument
        {
            ["_id"] = target,
            ["target_fingerprint"] = state.TargetFingerprint,
            ["state"] = PhysicalSchemaAppliedStateSerializer.Serialize(state)
        };
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try
        {
            await lease.AssertOwnedAsync(session, cancellationToken);
            var result = await collection.ReplaceOneAsync(
                session,
                filter,
                replacement,
                new ReplaceOptions { IsUpsert = expectedAppliedTargetFingerprint is null },
                cancellationToken);
            if (result.MatchedCount == 1 || result.UpsertedId is not null)
            {
                await session.CommitTransactionAsync(cancellationToken);
                return;
            }

            var currentInTransaction = await collection.Find(
                    session,
                    Builders<BsonDocument>.Filter.Eq("_id", target))
                .SingleOrDefaultAsync(cancellationToken);
            if (currentInTransaction is not null &&
                currentInTransaction.GetValue("target_fingerprint").AsString == state.TargetFingerprint &&
                currentInTransaction.GetValue("state").AsString == PhysicalSchemaAppliedStateSerializer.Serialize(state))
            {
                await session.CommitTransactionAsync(cancellationToken);
                return;
            }
        }
        catch
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }

        await session.AbortTransactionAsync(CancellationToken.None);

        throw new InvalidOperationException(
            $"MongoDB applied physical-schema state for '{targetIdentity}' changed since fingerprint '{expectedAppliedTargetFingerprint ?? "<empty>"}'.");
    }

    private async Task ExecuteAsync(PhysicalSchemaOperation operation, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation primary:
                await EnsureCollectionAsync(primary.Storage.Name.Identifier, cancellationToken);
                break;
            case CreatePhysicalEntityStorageOperation entity:
                await EnsureCollectionAsync(entity.Storage.Name.Identifier, cancellationToken);
                break;
            case CreateLinkedStorageOperation linked:
                await EnsureCollectionAsync(linked.Storage.Name.Identifier, cancellationToken);
                break;
            case CreateCollectionElementStorageOperation collectionElement:
                await EnsureCollectionElementStorageAsync(collectionElement.Storage, cancellationToken);
                break;
            case AddProjectedColumnOperation:
                // MongoDB fields are materialized by the canonical-JSON backfill and every write.
                break;
            case FinalizeProjectedColumnOperation:
                // MongoDB has no collection-level column nullability to alter. The preceding
                // canonical backfill proves required/default semantics; durable evidence still
                // records this operation so publication and restart match the portable plan.
                break;
            case CreatePhysicalIndexOperation index:
                await EnsureIndexAsync(index, cancellationToken);
                break;
            case BackfillCanonicalJsonOperation backfill:
                await BackfillAsync(backfill, cancellationToken);
                break;
            case ApplyProviderPhysicalSchemaDefinitionOperation providerDefinition:
                await mutationDefinitions.ApplyAsync(providerDefinition.Definition, cancellationToken);
                break;
            case ValidatePhysicalSchemaOperation validation:
                await ValidateAsync(validation, cancellationToken);
                break;
            case RecordPhysicalSchemaAppliedStateOperation:
                throw new InvalidOperationException("Applied-state recording is owned by RecordAppliedStateAsync.");
            default:
                throw new InvalidOperationException($"Unsupported MongoDB physical schema operation '{operation.Kind}'.");
        }
    }

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(AppliedStateCollection, cancellationToken);
        await EnsureCollectionAsync(OperationCollection, cancellationToken);
        await EnsureCollectionAsync(LockCollection, cancellationToken);
        await EnsureCollectionAsync(MongoDbPhysicalStorageFields.BoundedMutationOperationsCollection, cancellationToken);
    }

    private async Task EnsureCollectionAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await database.CreateCollectionAsync(
                name,
                new CreateCollectionOptions { Collation = new Collation("simple") },
                cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == NamespaceExists)
        {
        }

        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", name) },
            cancellationToken);
        var existing = (await cursor.ToListAsync(cancellationToken)).Single();
        ValidateWritableCollection(name, existing, "resolved physical route");
    }

    /// <summary>
    /// Creates the provider-owned representation of one bounded canonical JSON collection. MongoDB
    /// has no native table or primary-key concept, so the strict collection validator and named
    /// unique compound index together are the physical contract. Collection and index creation are
    /// separate MongoDB operations; the physical-schema application lease and exact replay
    /// admission fence the sequence. An existing incompatible collection is never modified into
    /// compliance.
    /// </summary>
    private async Task EnsureCollectionElementStorageAsync(
        ExecutableCollectionElementStorageRoute storage,
        CancellationToken cancellationToken)
    {
        var validator = CollectionElementValidator(storage);
        try
        {
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    ["create"] = storage.Storage.Name.Identifier,
                    ["collation"] = new BsonDocument("locale", "simple"),
                    ["validator"] = validator,
                    ["validationLevel"] = "strict",
                    ["validationAction"] = "error"
                },
                cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == NamespaceExists)
        {
            // Replay or a concurrent installer created the namespace. Validate its exact shape
            // below rather than loosening its validator with collMod.
        }

        var metadata = await CollectionMetadataAsync(storage.Storage.Name.Identifier, cancellationToken);
        ValidateCollectionElementStorage(storage, metadata);
        await EnsureCollectionElementOwnerOrdinalIndexAsync(storage, cancellationToken);
    }

    private async Task EnsureCollectionElementOwnerOrdinalIndexAsync(
        ExecutableCollectionElementStorageRoute storage,
        CancellationToken cancellationToken)
    {
        var keys = CollectionElementOwnerOrdinalIndexKeys(storage);
        var model = new CreateIndexModel<BsonDocument>(
            keys,
            new CreateIndexOptions<BsonDocument>
            {
                Name = storage.OwnerOrdinalKey.Name.Identifier,
                Unique = true,
                Collation = Collation.Simple,
                Sparse = false,
                Hidden = false
            });
        try
        {
            await database.GetCollection<BsonDocument>(storage.Storage.Name.Identifier)
                .Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code is 85 or 86)
        {
            throw new InvalidOperationException(
                $"MongoDB collection-element owner key '{storage.OwnerOrdinalKey.Name.Identifier}' on collection " +
                $"'{storage.Storage.Name.Identifier}' conflicts with the resolved physical route.",
                exception);
        }
        await ValidateCollectionElementOwnerOrdinalIndexAsync(storage, cancellationToken);
    }

    private async Task EnsureIndexAsync(CreatePhysicalIndexOperation operation, CancellationToken cancellationToken)
    {
        var keys = IndexKeys(operation);
        var partialFilter = MongoDbPhysicalIndexSemantics.PartialFilter(operation.Route, operation.Index);
        var model = new CreateIndexModel<BsonDocument>(
            keys,
            new CreateIndexOptions<BsonDocument>
            {
                Name = operation.Index.Name.Identifier,
                Unique = operation.Index.IsUnique,
                Collation = Collation.Simple,
                Sparse = false,
                Hidden = false,
                PartialFilterExpression = partialFilter
            });
        try
        {
            await database.GetCollection<BsonDocument>(operation.Storage.Name.Identifier)
                .Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code is 85 or 86)
        {
            throw new InvalidOperationException(
                $"MongoDB index '{operation.Index.Name.Identifier}' on collection '{operation.Storage.Name.Identifier}' conflicts with the resolved physical route.",
                exception);
        }

        var actual = (await (await database.GetCollection<BsonDocument>(operation.Storage.Name.Identifier)
                .Indexes.ListAsync(cancellationToken))
            .ToListAsync(cancellationToken))
            .SingleOrDefault(index => index.GetValue("name", "").AsString == operation.Index.Name.Identifier);
        if (actual is null || !IndexMatches(actual, keys, operation.Index.IsUnique, partialFilter))
        {
            throw new InvalidOperationException(
                $"MongoDB index '{operation.Index.Name.Identifier}' on collection '{operation.Storage.Name.Identifier}' conflicts with the resolved physical route. Actual: {actual?.ToJson() ?? "<missing>"}");
        }
    }

    private async Task ValidateDurableEvidenceAsync(
        PhysicalSchemaAppliedState state,
        CancellationToken cancellationToken)
    {
        var ledger = database.GetCollection<BsonDocument>(OperationCollection);
        var target = new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name);
        var evidenceByIdentity = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        foreach (var operation in state.AppliedOperations.Where(operation =>
                     operation.Kind != PhysicalSchemaOperationKind.RecordAppliedState))
        {
            var evidence = await ledger.Find(Builders<BsonDocument>.Filter.Eq(
                    "_id",
                    OperationEvidenceIdentity(target, operation.Identity)))
                .SingleOrDefaultAsync(cancellationToken);
            if (evidence is null ||
                evidence.GetValue("fingerprint", "").AsString != operation.Fingerprint ||
                evidence.GetValue("kind", "").AsString != operation.Kind.ToString())
            {
                throw new InvalidOperationException(
                    $"MongoDB physical-schema operation '{operation.Identity}' has no matching durable applied route state evidence.");
            }
            evidenceByIdentity.Add(operation.Identity, evidence);
        }

        using var collectionCursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        var collections = (await collectionCursor.ToListAsync(cancellationToken))
            .ToDictionary(collection => collection.GetValue("name").AsString, StringComparer.Ordinal);
        var storageNames = state.Snapshot.Routes
            .SelectMany(route => route.ResolvedNames)
            .Where(name => name.Kind is nameof(PhysicalObjectKind.PrimaryStorage) or
                nameof(PhysicalObjectKind.LinkedIndexStorage) or
                nameof(PhysicalObjectKind.CollectionElementStorage))
            .Select(name => name.Identifier)
            .Distinct(StringComparer.Ordinal);
        foreach (var storageName in storageNames)
        {
            if (!collections.TryGetValue(storageName, out var collection))
            {
                throw new InvalidOperationException(
                    $"MongoDB collection '{storageName}' required by durable applied route state is missing.");
            }

            ValidateWritableCollection(storageName, collection, "durable applied route state");
        }

        foreach (var snapshot in state.Snapshot.Routes)
        {
            var route = ExecutableStorageRouteSerializer.Deserialize(snapshot.CanonicalRouteJson);
            foreach (var storage in route.CollectionElementStorages)
            {
                if (!collections.TryGetValue(storage.Storage.Name.Identifier, out var collection))
                {
                    throw new InvalidOperationException(
                        $"MongoDB collection-element storage '{storage.Storage.Name.Identifier}' required by durable applied route state is missing.");
                }
                ValidateCollectionElementStorage(storage, collection);
                await ValidateCollectionElementOwnerOrdinalIndexAsync(storage, cancellationToken);
            }
        }

        foreach (var operation in state.AppliedOperations.Where(operation =>
                     operation.Kind == PhysicalSchemaOperationKind.CreatePhysicalIndex))
        {
            var evidence = evidenceByIdentity[operation.Identity];
            var expected = ReadExpectedIndex(state, operation);
            if (evidence.GetValue("collection", "").AsString != expected.Collection ||
                evidence.GetValue("index_name", "").AsString != expected.Name ||
                !evidence.GetValue("index_keys", new BsonDocument()).AsBsonDocument.Equals(expected.Keys) ||
                evidence.GetValue("unique", false).ToBoolean() != expected.Unique ||
                evidence.GetValue("missing_value_behavior", "").AsString != expected.MissingValueBehavior.ToString() ||
                !IsSimpleCollation(evidence.GetValue("collation", new BsonDocument()).AsBsonDocument) ||
                evidence.GetValue("sparse", false).ToBoolean() ||
                evidence.GetValue("hidden", false).ToBoolean() ||
                !EvidencePartialFilterMatches(evidence, expected.PartialFilter))
            {
                throw new InvalidOperationException(
                    $"MongoDB index operation '{operation.Identity}' evidence conflicts with durable applied route state.");
            }

            var collection = database.GetCollection<BsonDocument>(expected.Collection);
            var indexes = await (await collection.Indexes.ListAsync(cancellationToken)).ToListAsync(cancellationToken);
            var actual = indexes.SingleOrDefault(index => index.GetValue("name", "").AsString == expected.Name);
            if (actual is null || !IndexMatches(actual, expected.Keys, expected.Unique, expected.PartialFilter))
            {
                throw new InvalidOperationException(
                    $"MongoDB index '{expected.Name}' on collection '{collection.CollectionNamespace.CollectionName}' conflicts with durable applied route state.");
            }
        }
    }

    private static ExpectedIndex ReadExpectedIndex(
        PhysicalSchemaAppliedState state,
        PhysicalSchemaAppliedOperation operation)
    {
        var route = state.Snapshot.Routes.Single(candidate => candidate.StorageUnit == operation.StorageUnit);
        using var document = JsonDocument.Parse(route.CanonicalRouteJson);
        var root = document.RootElement;
        var index = root.GetProperty("indexes").EnumerateArray()
            .Single(candidate => candidate.GetProperty("identity").GetString() == operation.SubjectIdentity);
        var target = index.GetProperty("target").GetString();
        var storage = target == ExecutableStorageObjectRole.LinkedIndexStorage.ToString()
            ? root.GetProperty("linkedIndexStorage")
            : root.GetProperty("primaryStorage");
        var keys = new BsonDocument();
        foreach (var column in index.GetProperty("columns").EnumerateArray().OrderBy(column => column.GetProperty("order").GetInt32()))
        {
            keys[column.GetProperty("column").GetProperty("identifier").GetString()!] =
                column.GetProperty("direction").GetString() == PhysicalSortDirection.Ascending.ToString() ? 1 : -1;
        }
        var missingValueBehavior = Enum.Parse<Groundwork.Core.Indexing.MissingValueBehavior>(
            index.GetProperty("missingValueBehavior").GetString()!);
        BsonDocument? partialFilter = null;
        if (missingValueBehavior == Groundwork.Core.Indexing.MissingValueBehavior.Excluded)
        {
            var targetName = index.GetProperty("target").GetString();
            var projectedIdentifiers = root.GetProperty("projectedColumns").EnumerateArray()
                .Where(projection => projection.GetProperty("target").GetString() == targetName)
                .Select(projection => projection.GetProperty("column").GetProperty("identifier").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var valueFields = index.GetProperty("columns").EnumerateArray()
                .OrderBy(column => column.GetProperty("order").GetInt32())
                .Select(column => column.GetProperty("column").GetProperty("identifier").GetString()!)
                .Where(projectedIdentifiers.Contains)
                .ToArray();
            if (valueFields.Length != 0)
            {
                partialFilter = new BsonDocument();
                foreach (var field in valueFields)
                    partialFilter[field] = new BsonDocument("$exists", true);
            }
        }
        return new ExpectedIndex(
            storage.GetProperty("name").GetProperty("identifier").GetString()!,
            index.GetProperty("name").GetProperty("identifier").GetString()!,
            keys,
            index.GetProperty("unique").GetBoolean(),
            missingValueBehavior,
            partialFilter);
    }

    private sealed record ExpectedIndex(
        string Collection,
        string Name,
        BsonDocument Keys,
        bool Unique,
        Groundwork.Core.Indexing.MissingValueBehavior MissingValueBehavior,
        BsonDocument? PartialFilter);

    internal static bool IndexMatches(
        BsonDocument actual,
        BsonDocument keys,
        bool unique,
        BsonDocument? partialFilter) =>
        actual.GetValue("key", new BsonDocument()).AsBsonDocument.Equals(keys) &&
        actual.GetValue("unique", false).ToBoolean() == unique &&
        (!actual.TryGetValue("collation", out var collation) || IsSimpleCollation(collation.AsBsonDocument)) &&
        !actual.GetValue("sparse", false).ToBoolean() &&
        !actual.GetValue("hidden", false).ToBoolean() &&
        MongoDbPhysicalIndexSemantics.PartialFilterMatches(actual, partialFilter) &&
        !actual.Contains("expireAfterSeconds") &&
        !actual.Contains("wildcardProjection");

    private static bool EvidencePartialFilterMatches(BsonDocument evidence, BsonDocument? expected)
    {
        if (expected is null)
            return !evidence.Contains("partial_filter_expression");
        return evidence.TryGetValue("partial_filter_expression", out var actual) &&
               actual.IsBsonDocument &&
               actual.AsBsonDocument.Equals(expected);
    }

    private static bool IsSimpleCollation(BsonDocument collation) =>
        collation.ElementCount == 1 &&
        collation.GetValue("locale", "").AsString == "simple";

    private static BsonDocument IndexKeys(CreatePhysicalIndexOperation operation) =>
        IndexKeys(operation.Index);

    private static BsonDocument IndexKeys(ExecutablePhysicalIndexRoute index)
    {
        var keys = new BsonDocument();
        foreach (var column in index.Columns.OrderBy(column => column.Order))
            keys[column.Column.Identifier] = column.Direction == PhysicalSortDirection.Ascending ? 1 : -1;
        return keys;
    }

    internal static BsonDocument CollectionElementOwnerOrdinalIndexKeys(
        ExecutableCollectionElementStorageRoute storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var keys = new BsonDocument();
        foreach (var field in storage.OwnerOrdinalKey.Columns)
            keys[field.Column.Identifier] = 1;
        return keys;
    }

    private async Task BackfillAsync(BackfillCanonicalJsonOperation operation, CancellationToken cancellationToken)
    {
        var route = operation.Route ?? throw new InvalidOperationException("MongoDB physical backfill requires an executable route.");
        var primary = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var discriminator = Builders<BsonDocument>.Filter.Eq(route.Discriminator.Column.Identifier, route.Discriminator.Value);
        using var cursor = await primary.Find(discriminator).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                var canonicalJson = MongoDbCanonicalJson.Serialize(
                    document.GetValue(route.Envelope.CanonicalJson.Identifier));
                if (beforeBackfillWrite is not null)
                    await beforeBackfillWrite(cancellationToken);
                if (operation.Target == ExecutableStorageObjectRole.PrimaryStorage)
                {
                    var updates = ProjectionUpdates(route, operation.SourcePaths, operation.Target, canonicalJson);
                    if (updates.Count != 0)
                    {
                        await primary.UpdateOneAsync(
                            Builders<BsonDocument>.Filter.Eq(
                                MongoDbPhysicalStorageFields.Id,
                                document[MongoDbPhysicalStorageFields.Id]) &
                            Builders<BsonDocument>.Filter.Eq(
                                MongoDbPhysicalStorageFields.Incarnation,
                                document[MongoDbPhysicalStorageFields.Incarnation]) &
                            Builders<BsonDocument>.Filter.Eq(
                                route.Envelope.Version.Identifier,
                                document[route.Envelope.Version.Identifier]),
                            Builders<BsonDocument>.Update.Combine(updates),
                            cancellationToken: cancellationToken);
                    }
                    continue;
                }

                await UpsertLinkedProjectionAsync(route, document, canonicalJson, cancellationToken);
            }
        }
    }

    private async Task UpsertLinkedProjectionAsync(
        ExecutableStorageRoute route,
        BsonDocument primary,
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        var projections = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
            .ToArray();
        var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(canonicalJson, projections);
        var linked = MongoDbLinkedDocumentStorage.Create(route, primary, projectedValues);
        await MongoDbLinkedDocumentStorage.ReconcileAsync(
            database,
            route,
            primary,
            linked,
            linked.Updates(),
            cancellationToken);
    }

    private static List<UpdateDefinition<BsonDocument>> ProjectionUpdates(
        ExecutableStorageRoute route,
        IReadOnlyList<string> paths,
        ExecutableStorageObjectRole target,
        string canonicalJson)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>();
        var projections = route.ProjectedColumns.Where(column =>
            column.Target == target && paths.Contains(column.Definition.Path, StringComparer.Ordinal)).ToArray();
        var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(canonicalJson, projections);
        foreach (var projection in projections)
        {
            var value = projectedValues[projection];
            updates.Add(value.IsPresent
                ? Builders<BsonDocument>.Update.Set(projection.Column.Identifier, value.Value)
                : Builders<BsonDocument>.Update.Unset(projection.Column.Identifier));
        }
        return updates;
    }

    private async Task ValidateAsync(ValidatePhysicalSchemaOperation operation, CancellationToken cancellationToken)
    {
        using var collectionCursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        var collections = (await collectionCursor.ToListAsync(cancellationToken))
            .ToDictionary(collection => collection.GetValue("name").AsString, StringComparer.Ordinal);
        foreach (var route in operation.Routes)
        {
            var storageTargets = new[] { route.PrimaryStorage }
                .Concat(route.LinkedIndexStorage is null ? [] : [route.LinkedIndexStorage])
                .ToArray();
            if (storageTargets.Any(target => !collections.ContainsKey(target.Name.Identifier)))
            {
                throw new InvalidOperationException($"MongoDB physical route '{route.StorageUnit.Value}' is missing a resolved collection.");
            }
            foreach (var target in storageTargets)
                ValidateWritableCollection(target.Name.Identifier, collections[target.Name.Identifier], "resolved physical route");

            foreach (var storage in route.CollectionElementStorages)
            {
                if (!collections.TryGetValue(storage.Storage.Name.Identifier, out var collection))
                {
                    throw new InvalidOperationException(
                        $"MongoDB collection-element storage '{storage.Storage.Name.Identifier}' is missing.");
                }
                ValidateCollectionElementStorage(storage, collection);
                await ValidateCollectionElementOwnerOrdinalIndexAsync(storage, cancellationToken);
            }

            foreach (var index in route.Indexes)
            {
                var storage = index.Target == ExecutableStorageObjectRole.LinkedIndexStorage
                    ? route.LinkedIndexStorage!
                    : route.PrimaryStorage;
                var actual = (await (await database.GetCollection<BsonDocument>(storage.Name.Identifier)
                        .Indexes.ListAsync(cancellationToken))
                    .ToListAsync(cancellationToken))
                    .SingleOrDefault(candidate => candidate.GetValue("name", "").AsString == index.Name.Identifier);
                if (actual is null || !IndexMatches(
                        actual,
                        IndexKeys(index),
                        index.IsUnique,
                        MongoDbPhysicalIndexSemantics.PartialFilter(route, index)))
                {
                    throw new InvalidOperationException(
                        $"MongoDB index '{index.Name.Identifier}' on collection '{storage.Name.Identifier}' conflicts with the resolved physical route.");
                }
            }
        }

        await mutationDefinitions.ValidateAsync(operation.ProviderDefinitions, cancellationToken);
    }

    private async Task ValidateCollectionElementOwnerOrdinalIndexAsync(
        ExecutableCollectionElementStorageRoute storage,
        CancellationToken cancellationToken)
    {
        var actual = (await (await database.GetCollection<BsonDocument>(storage.Storage.Name.Identifier)
                .Indexes.ListAsync(cancellationToken))
            .ToListAsync(cancellationToken))
            .SingleOrDefault(index => index.GetValue("name", "").AsString == storage.OwnerOrdinalKey.Name.Identifier);
        if (actual is null || !IndexMatches(
                actual,
                CollectionElementOwnerOrdinalIndexKeys(storage),
                unique: true,
                partialFilter: null))
        {
            throw new InvalidOperationException(
                $"MongoDB collection-element owner key '{storage.OwnerOrdinalKey.Name.Identifier}' on collection " +
                $"'{storage.Storage.Name.Identifier}' conflicts with the resolved physical route.");
        }
    }

    internal static BsonDocument CollectionElementValidator(ExecutableCollectionElementStorageRoute storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        RequireCollectionElementCollation(storage.Value.Definition);
        var properties = new BsonDocument
        {
            [storage.DocumentKind.Column.Identifier] = BsonTypeRule("string"),
            [storage.StorageScope.Column.Identifier] = BsonTypeRule("string"),
            [storage.IdComparisonKey.Column.Identifier] = BsonTypeRule("string"),
            [storage.IdLookupKey.Column.Identifier] = BsonTypeRule("string"),
            [storage.Ordinal.Column.Identifier] = BsonTypeRule("int"),
            [storage.Value.Column.Identifier] = BsonTypeRule(CollectionElementValueBsonType(storage.Value.Definition.Type))
        };
        return new BsonDocument("$jsonSchema", new BsonDocument
        {
            ["bsonType"] = "object",
            ["required"] = new BsonArray(properties.Names),
            ["properties"] = properties
        });
    }

    private static BsonDocument BsonTypeRule(string bsonType) => new("bsonType", bsonType);

    private static string CollectionElementValueBsonType(PortablePhysicalType type) => type switch
    {
        PortablePhysicalType.String or PortablePhysicalType.Guid => "string",
        PortablePhysicalType.Int32 => "int",
        PortablePhysicalType.Int64 or PortablePhysicalType.DateTime => "long",
        PortablePhysicalType.Decimal => "decimal",
        PortablePhysicalType.Boolean => "bool",
        PortablePhysicalType.Binary => "binData",
        _ => throw new InvalidOperationException(
            $"MongoDB collection-element storage does not support value type '{type}'.")
    };

    private static void RequireCollectionElementCollation(ProjectedColumnDefinition definition)
    {
        // Collection elements have one provider-owned collection. MongoDB supports collation at
        // that collection/index boundary, not per field, and this provider's identity/key routes
        // require the native simple binary collation.
        if (definition.Collation is not null &&
            !IsSimpleBinaryCollationAlias(definition.Collation))
        {
            throw new InvalidOperationException(
                $"MongoDB collection-element projection '{definition.LogicalName}' cannot represent " +
                $"per-field collation '{definition.Collation}'; only simple collection collation is supported.");
        }
    }

    private static bool IsSimpleBinaryCollationAlias(string collation) =>
        string.Equals(collation, "simple", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(collation, "ordinal", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(collation, "binary", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCollectionElementStorage(
        ExecutableCollectionElementStorageRoute storage,
        BsonDocument metadata)
    {
        ValidateWritableCollection(storage.Storage.Name.Identifier, metadata, "resolved collection-element storage");
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (!UsesSimpleCollectionCollation(options) ||
            !options.TryGetValue("validator", out var validator) ||
            !validator.IsBsonDocument ||
            !CollectionElementValidatorMatches(storage, validator.AsBsonDocument) ||
            options.GetValue("validationLevel", "").AsString != "strict" ||
            options.GetValue("validationAction", "").AsString != "error")
        {
            throw new InvalidOperationException(
                $"MongoDB collection-element storage '{storage.Storage.Name.Identifier}' conflicts with the resolved physical route.");
        }
    }

    private static bool UsesSimpleCollectionCollation(BsonDocument options)
    {
        // MongoDB omits an explicit simple collation from listCollections because it is the
        // collection default. When present, its locale must still be simple.
        return !options.TryGetValue("collation", out var collation) ||
               collation.IsBsonDocument &&
               IsSimpleCollation(collation.AsBsonDocument);
    }

    private static bool CollectionElementValidatorMatches(
        ExecutableCollectionElementStorageRoute storage,
        BsonDocument validator)
    {
        var expected = CollectionElementValidator(storage)["$jsonSchema"].AsBsonDocument;
        if (!validator.TryGetValue("$jsonSchema", out var actualSchemaValue) ||
            !actualSchemaValue.IsBsonDocument ||
            !HasExactlyFields(validator, ["$jsonSchema"]))
        {
            return false;
        }

        var actual = actualSchemaValue.AsBsonDocument;
        if (!HasExactlyFields(actual, ["bsonType", "required", "properties"]) ||
            !actual.GetValue("bsonType", "").IsString ||
            actual["bsonType"].AsString != "object" ||
            !actual.TryGetValue("required", out var required) ||
            !required.IsBsonArray ||
            !StringArrayMatches(required.AsBsonArray, expected["required"].AsBsonArray) ||
            !actual.TryGetValue("properties", out var properties) ||
            !properties.IsBsonDocument)
        {
            return false;
        }

        var expectedProperties = expected["properties"].AsBsonDocument;
        var actualProperties = properties.AsBsonDocument;
        if (!HasExactlyFields(actualProperties, expectedProperties.Names))
            return false;

        foreach (var field in expectedProperties)
        {
            if (!actualProperties.TryGetValue(field.Name, out var actualRule) ||
                !actualRule.IsBsonDocument ||
                !field.Value.IsBsonDocument ||
                !HasExactlyFields(actualRule.AsBsonDocument, ["bsonType"]) ||
                !actualRule.AsBsonDocument.GetValue("bsonType", "").IsString ||
                actualRule.AsBsonDocument["bsonType"] != field.Value.AsBsonDocument["bsonType"])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactlyFields(BsonDocument document, IEnumerable<string> expected)
    {
        var names = expected.ToHashSet(StringComparer.Ordinal);
        return document.ElementCount == names.Count &&
               document.Names.All(names.Contains) &&
               document.Names.Distinct(StringComparer.Ordinal).Count() == names.Count;
    }

    private static bool StringArrayMatches(BsonArray actual, BsonArray expected)
    {
        if (actual.Count != expected.Count || actual.Any(value => !value.IsString))
            return false;
        var expectedValues = expected.Select(value => value.AsString).ToHashSet(StringComparer.Ordinal);
        return actual.Select(value => value.AsString).ToHashSet(StringComparer.Ordinal).SetEquals(expectedValues);
    }

    private async Task<BsonDocument> CollectionMetadataAsync(string name, CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", name) },
            cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new InvalidOperationException($"MongoDB collection '{name}' is missing.");
    }

    private static void ValidateWritableCollection(
        string name,
        BsonDocument metadata,
        string evidenceSource)
    {
        var type = metadata.GetValue("type", "").AsString;
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (type != "collection" || options.Contains("timeseries") || options.Contains("viewOn"))
        {
            throw new InvalidOperationException(
                $"MongoDB collection '{name}' conflicts with {evidenceSource} because it is not a writable native collection.");
        }
        if (options.GetValue("capped", false).ToBoolean())
        {
            throw new InvalidOperationException(
                $"MongoDB collection '{name}' conflicts with {evidenceSource} because capped collections " +
                "cannot participate in Groundwork snapshot transactions.");
        }
        if (options.TryGetValue("collation", out var collation) &&
            collation.AsBsonDocument.GetValue("locale", "simple").AsString != "simple")
        {
            throw new InvalidOperationException(
                $"MongoDB collection '{name}' conflicts with {evidenceSource} because it does not use simple binary collation.");
        }
    }

    internal static BsonDocument KeyDocument(ExecutableKeyRoute key, BsonDocument values)
    {
        var result = new BsonDocument();
        foreach (var column in key.Columns)
            result[column.Identifier] = values[column.Identifier];
        return result;
    }

    private static PhysicalSchemaOperationAcknowledgement AcknowledgeExisting(
        PhysicalSchemaOperation operation,
        BsonDocument evidence)
    {
        var fingerprint = evidence.GetValue("fingerprint").AsString;
        if (fingerprint != operation.Fingerprint)
            throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, fingerprint);
        return new PhysicalSchemaOperationAcknowledgement(
            operation.Identity,
            fingerprint,
            new DateTimeOffset(evidence.GetValue("applied_at").ToUniversalTime()));
    }

    private static BsonDocument OperationEvidenceIdentity(
        PhysicalSchemaTargetIdentity target,
        string operationIdentity) =>
        new()
        {
            ["target"] = TargetIdentityDocument(target),
            ["operation"] = operationIdentity
        };

    internal static BsonDocument TargetIdentityDocument(PhysicalSchemaTargetIdentity target) =>
        new()
        {
            ["provider"] = target.ProviderName,
            ["manifest"] = target.ManifestIdentity.Value
        };

    internal static TimeSpan LeaseRenewalInterval(TimeSpan duration) =>
        TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 3));

    private static ApplicationLock RequireLease(
        IPhysicalSchemaApplicationLock applicationLock,
        PhysicalSchemaTargetIdentity? expectedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(applicationLock);
        if (applicationLock is not ApplicationLock lease)
            throw new InvalidOperationException("MongoDB physical schema execution requires its acquired MongoDB application lease.");
        if (expectedTarget is not null && lease.Target != expectedTarget)
        {
            throw new InvalidOperationException(
                $"MongoDB application lease '{lease.Target}' does not own requested target '{expectedTarget}'.");
        }
        return lease;
    }

    private sealed class ApplicationLock : IPhysicalSchemaApplicationLock
    {
        private readonly IMongoCollection<BsonDocument> collection;
        private readonly string owner;
        private readonly long fence;
        private readonly TimeSpan leaseDuration;
        private readonly TimeProvider timeProvider;
        private readonly Action<DateTimeOffset>? afterRenewal;
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource ownershipLost = new();
        private readonly Task renewal;

        public ApplicationLock(
            IMongoCollection<BsonDocument> collection,
            PhysicalSchemaTargetIdentity target,
            string owner,
            long fence,
            TimeSpan leaseDuration,
            TimeProvider timeProvider,
            Action<DateTimeOffset>? afterRenewal)
        {
            this.collection = collection;
            this.owner = owner;
            this.fence = fence;
            this.leaseDuration = leaseDuration;
            this.timeProvider = timeProvider;
            this.afterRenewal = afterRenewal;
            Target = target;
            renewal = RenewAsync();
        }

        public PhysicalSchemaTargetIdentity Target { get; }

        public CancellationToken OwnershipLost => ownershipLost.Token;

        public async Task AssertOwnedAsync(IClientSessionHandle? session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = OwnershipFilter(requireUnexpired: true);
            // A transactional assertion must write the lease document, not merely read it. That
            // write makes a concurrent steal/renewal conflict with the same transaction that
            // records operation evidence or applied state, closing the check-then-write window.
            var owned = session is null
                ? await collection.Find(filter).Limit(1).AnyAsync(cancellationToken)
                : (await collection.UpdateOneAsync(
                    session,
                    filter,
                    Builders<BsonDocument>.Update.Inc("fence_assertion", 1L),
                    cancellationToken: cancellationToken)).MatchedCount == 1;
            if (owned)
                return;

            await ownershipLost.CancelAsync();
            throw new OperationCanceledException(
                $"MongoDB physical-schema application lease for '{Target}' is no longer owned by fence {fence}.",
                ownershipLost.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            await renewal;
            await collection.UpdateOneAsync(
                OwnershipFilter(requireUnexpired: false),
                Builders<BsonDocument>.Update
                    .Set("expires_at", timeProvider.GetUtcNow().UtcDateTime)
                    .Unset("owner"));
            stopping.Dispose();
            ownershipLost.Dispose();
        }

        private async Task RenewAsync()
        {
            var interval = LeaseRenewalInterval(leaseDuration);
            try
            {
                while (true)
                {
                    await Task.Delay(interval, timeProvider, stopping.Token);
                    var filter = OwnershipFilter(requireUnexpired: true);
                    var renewedExpiry = timeProvider.GetUtcNow().Add(leaseDuration);
                    var result = await collection.UpdateOneAsync(
                        filter,
                        Builders<BsonDocument>.Update.Set("expires_at", renewedExpiry.UtcDateTime),
                        cancellationToken: stopping.Token);
                    if (result.MatchedCount == 1)
                    {
                        afterRenewal?.Invoke(renewedExpiry);
                        continue;
                    }

                    await ownershipLost.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch
            {
                await ownershipLost.CancelAsync();
            }
        }

        private FilterDefinition<BsonDocument> OwnershipFilter(bool requireUnexpired)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", TargetIdentityDocument(Target)) &
                         Builders<BsonDocument>.Filter.Eq("owner", owner) &
                         Builders<BsonDocument>.Filter.Eq("fence", fence);
            if (requireUnexpired)
                filter &= Builders<BsonDocument>.Filter.Gt("expires_at", timeProvider.GetUtcNow().UtcDateTime);
            return filter;
        }
    }
}
