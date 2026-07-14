using Groundwork.Core.PhysicalStorage;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// One canonical linked-row shape shared by runtime writes and every schema backfill path.
/// </summary>
internal sealed class MongoDbLinkedDocumentState
{
    internal MongoDbLinkedDocumentState(
        BsonDocument document,
        IReadOnlyList<string> absentProjectionFields)
    {
        Document = document;
        AbsentProjectionFields = absentProjectionFields;
    }

    public BsonDocument Document { get; }

    public BsonValue Identity => Document[MongoDbPhysicalStorageFields.Id];

    public BsonValue PrimaryVersion => Document[MongoDbPhysicalStorageFields.LinkedPrimaryVersion];

    public BsonValue Incarnation => Document[MongoDbPhysicalStorageFields.Incarnation];

    public IReadOnlyList<string> AbsentProjectionFields { get; }

    public IReadOnlyList<UpdateDefinition<BsonDocument>> Updates()
    {
        var updates = Document.Elements
            .Where(element => element.Name != MongoDbPhysicalStorageFields.Id)
            .Select(element => Builders<BsonDocument>.Update.Set(element.Name, element.Value))
            .ToList();
        updates.AddRange(AbsentProjectionFields.Select(field =>
            Builders<BsonDocument>.Update.Unset(field)));
        return updates;
    }
}

internal static class MongoDbLinkedDocumentStorage
{
    public static MongoDbLinkedDocumentState Create(
        ExecutableStorageRoute route,
        BsonDocument primary,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        var relationship = route.LinkedRelationship ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked relationship.");
        var document = new BsonDocument
        {
            [relationship.DocumentKind.Identifier] = route.Discriminator.Value,
            [relationship.StorageScope.Identifier] = primary[route.Envelope.StorageScope.Identifier],
            [MongoDbPhysicalStorageFields.LinkedPrimaryVersion] = primary[route.Envelope.Version.Identifier],
            [MongoDbPhysicalStorageFields.Incarnation] = primary[MongoDbPhysicalStorageFields.Incarnation]
        };
        MongoDbPhysicalDocumentIdentity.WriteLinked(
            document,
            route,
            primary[route.Envelope.Id.Identifier].AsString);
        var absent = new List<string>();
        foreach (var projection in route.ProjectedColumns.Where(column =>
                     column.Target == ExecutableStorageObjectRole.LinkedIndexStorage))
        {
            var value = projectedValues[projection];
            if (value.IsPresent)
                document[projection.Column.Identifier] = value.Value;
            else
                absent.Add(projection.Column.Identifier);
        }
        if (primary.TryGetValue(MongoDbPhysicalMutationStorage.BindingRoot, out var mutationBindings))
            document[MongoDbPhysicalMutationStorage.BindingRoot] = mutationBindings.DeepClone();
        document[MongoDbPhysicalStorageFields.Id] = MongoDbPhysicalSchemaExecutor.KeyDocument(
            route.AuxiliaryKey!,
            document);
        return new MongoDbLinkedDocumentState(document, absent);
    }

    public static async Task ReconcileAsync(
        IMongoDatabase database,
        ExecutableStorageRoute route,
        BsonDocument primary,
        MongoDbLinkedDocumentState linked,
        IReadOnlyList<UpdateDefinition<BsonDocument>> updates,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(
            route.LinkedIndexStorage?.Name.Identifier ??
            throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked storage."));
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        try
        {
            await session.WithTransactionAsync(
                async (currentSession, token) =>
                {
                    var currentPrimary = await database
                        .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
                        .Find(currentSession, CurrentPrimaryFilter(route, primary))
                        .Limit(1)
                        .AnyAsync(token);
                    if (!currentPrimary)
                        return false;

                    await collection.UpdateOneAsync(
                        currentSession,
                        ReconciliationFilter(linked),
                        Builders<BsonDocument>.Update.Combine(updates),
                        new UpdateOptions { IsUpsert = true },
                        token);
                    return true;
                },
                new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority),
                cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            if (!await HasStrictlyNewerIncarnationAsync(
                    database,
                    route,
                    collection,
                    primary,
                    linked,
                    cancellationToken))
            {
                throw;
            }
        }
    }

    public static FilterDefinition<BsonDocument> CurrentPrimaryFilter(
        ExecutableStorageRoute route,
        BsonDocument primary) =>
        Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Id,
            primary[MongoDbPhysicalStorageFields.Id]) &
        Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Incarnation,
            primary[MongoDbPhysicalStorageFields.Incarnation]) &
        Builders<BsonDocument>.Filter.Eq(
            route.Envelope.Version.Identifier,
            primary[route.Envelope.Version.Identifier]);

    public static FilterDefinition<BsonDocument> ReconciliationFilter(
        MongoDbLinkedDocumentState linked) =>
        Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Id, linked.Identity) &
        (Builders<BsonDocument>.Filter.Lte(
             MongoDbPhysicalStorageFields.LinkedPrimaryVersion,
             linked.PrimaryVersion) |
         Builders<BsonDocument>.Filter.Exists(
             MongoDbPhysicalStorageFields.LinkedPrimaryVersion,
             false));

    public static async Task<bool> HasStrictlyNewerIncarnationAsync(
        IMongoDatabase database,
        ExecutableStorageRoute route,
        IMongoCollection<BsonDocument> linkedCollection,
        BsonDocument backfillPrimary,
        MongoDbLinkedDocumentState attempted,
        CancellationToken cancellationToken)
    {
        var existing = await linkedCollection.Find(Builders<BsonDocument>.Filter.Eq(
                MongoDbPhysicalStorageFields.Id,
                attempted.Identity))
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
            return false;

        var existingVersion = existing.GetValue(
            MongoDbPhysicalStorageFields.LinkedPrimaryVersion,
            BsonNull.Value);
        var existingIncarnation = existing.GetValue(
            MongoDbPhysicalStorageFields.Incarnation,
            BsonNull.Value);
        if (existingIncarnation.Equals(attempted.Incarnation))
        {
            return existingVersion.IsNumeric &&
                   existingVersion.ToInt64() > attempted.PrimaryVersion.ToInt64();
        }

        return await database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
            .Find(
                Builders<BsonDocument>.Filter.Eq(
                    MongoDbPhysicalStorageFields.Id,
                    backfillPrimary[MongoDbPhysicalStorageFields.Id]) &
                Builders<BsonDocument>.Filter.Eq(
                    MongoDbPhysicalStorageFields.Incarnation,
                    existingIncarnation) &
                Builders<BsonDocument>.Filter.Eq(
                    route.Envelope.Version.Identifier,
                    existingVersion))
            .Limit(1)
            .AnyAsync(cancellationToken);
    }
}
