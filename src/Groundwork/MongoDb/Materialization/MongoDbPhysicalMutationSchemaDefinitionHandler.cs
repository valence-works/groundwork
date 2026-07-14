using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.MongoDb.Documents;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Groundwork.MongoDb.Materialization;

/// <summary>
/// Applies and validates MongoDB bounded-mutation bindings as durable physical-schema definitions.
/// </summary>
internal sealed class MongoDbPhysicalMutationSchemaDefinitionHandler
{
    private readonly IMongoDatabase database;
    private readonly Func<CancellationToken, ValueTask>? beforeBackfillWrite;

    public MongoDbPhysicalMutationSchemaDefinitionHandler(
        IMongoDatabase database,
        Func<CancellationToken, ValueTask>? beforeBackfillWrite)
    {
        this.database = database;
        this.beforeBackfillWrite = beforeBackfillWrite;
    }

    public async Task ApplyAsync(
        ProviderPhysicalSchemaDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.Kind == MongoDbPhysicalMutationSchemaBinding.DefinitionKind)
        {
            var binding = ResolveBinding(definition);
            await BackfillFencesAsync(binding, cancellationToken);
            await EnsureWriteFenceAsync(binding, binding.Primary, cancellationToken);
            if (binding.Linked is not null)
                await EnsureWriteFenceAsync(binding, binding.Linked, cancellationToken);
            await ValidateBindingAsync(binding, validateDocuments: true, cancellationToken);
            return;
        }

        if (definition.Kind == MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind)
        {
            var selector = ResolveSelector(definition);
            await EnsureIndexAsync(selector.Primary, cancellationToken);
            if (selector.Linked is not null)
                await EnsureIndexAsync(selector.Linked, cancellationToken);
            await BackfillSelectorAsync(selector, cancellationToken);
            await ValidateSelectorAsync(selector, validateDocuments: true, cancellationToken);
            return;
        }

        throw Unsupported(definition);
    }

    public async Task ValidateAsync(
        IReadOnlyList<ProviderPhysicalSchemaDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var bindings = definitions
            .Where(definition => definition.Kind == MongoDbPhysicalMutationSchemaBinding.DefinitionKind)
            .Select(ResolveBinding)
            .ToArray();
        var selectors = definitions
            .Where(definition => definition.Kind == MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind)
            .Select(ResolveSelector)
            .ToArray();
        var knownKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            MongoDbPhysicalMutationSchemaBinding.DefinitionKind,
            MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind
        };
        var unsupported = definitions.FirstOrDefault(definition => !knownKinds.Contains(definition.Kind));
        if (unsupported is not null)
            throw Unsupported(unsupported);

        foreach (var binding in bindings)
        {
            var selector = selectors.SingleOrDefault(candidate =>
                candidate.Route.StorageUnit == binding.Route.StorageUnit &&
                candidate.LogicalIndexIdentity == binding.Primary.LogicalIndexIdentity);
            if (selector is null ||
                selector.Route.Fingerprint != binding.Route.Fingerprint ||
                !selector.Primary.Serialize().Equals(binding.Primary.Serialize()) ||
                !Equals(selector.Linked?.Serialize(), binding.Linked?.Serialize()))
            {
                throw new InvalidOperationException(
                    $"MongoDB bounded-mutation binding '{binding.MutationIdentity}' has no exact durable selector definition.");
            }
            await ValidateBindingAsync(binding, validateDocuments: true, cancellationToken);
        }
        foreach (var selector in selectors)
            await ValidateSelectorAsync(selector, validateDocuments: true, cancellationToken);
    }

    private static MongoDbPhysicalMutationSchemaBinding ResolveBinding(
        ProviderPhysicalSchemaDefinition definition)
    {
        if (definition.ProviderName != MongoDbGroundworkCapabilities.Provider.Name ||
            definition.Kind != MongoDbPhysicalMutationSchemaBinding.DefinitionKind)
        {
            throw new InvalidOperationException(
                $"Unsupported MongoDB provider physical-schema definition '{definition.ProviderName}:{definition.Kind}'.");
        }

        var binding = MongoDbPhysicalMutationSchemaBinding.Deserialize(definition.CanonicalDefinition);
        if (binding.Route.StorageUnit != definition.StorageUnit ||
            binding.MutationIdentity != definition.SubjectIdentity ||
            binding.ProviderName != definition.ProviderName)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation definition '{definition.SubjectIdentity}' has inconsistent binding identity.");
        }
        return binding;
    }

    private static MongoDbPhysicalMutationSelectorSchemaDefinition ResolveSelector(
        ProviderPhysicalSchemaDefinition definition)
    {
        if (definition.ProviderName != MongoDbGroundworkCapabilities.Provider.Name ||
            definition.Kind != MongoDbPhysicalMutationSelectorSchemaDefinition.DefinitionKind)
        {
            throw Unsupported(definition);
        }

        var selector = MongoDbPhysicalMutationSelectorSchemaDefinition.Deserialize(
            definition.CanonicalDefinition);
        if (selector.Route.StorageUnit != definition.StorageUnit ||
            selector.LogicalIndexIdentity != definition.SubjectIdentity ||
            selector.ProviderName != definition.ProviderName)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation selector definition '{definition.SubjectIdentity}' has inconsistent identity.");
        }
        return selector;
    }

    private static InvalidOperationException Unsupported(ProviderPhysicalSchemaDefinition definition) =>
        new(
            $"Unsupported MongoDB provider physical-schema definition " +
            $"'{definition.ProviderName}:{definition.Kind}'.");

    private async Task EnsureIndexAsync(
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var model = new CreateIndexModel<BsonDocument>(
            selector.IndexKeys,
            new CreateIndexOptions<BsonDocument>
            {
                Name = selector.Index.Identifier,
                Unique = false,
                Collation = Collation.Simple,
                Sparse = false,
                Hidden = false
            });
        try
        {
            await database.GetCollection<BsonDocument>(selector.StorageObject.Identifier)
                .Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code is 85 or 86)
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation index '{selector.Index.Identifier}' on collection " +
                $"'{selector.StorageObject.Identifier}' conflicts with the executable mutation binding.",
                exception);
        }

        await ValidateIndexAsync(selector, cancellationToken);
    }

    private async Task ValidateIndexAsync(
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var actual = (await (await database.GetCollection<BsonDocument>(selector.StorageObject.Identifier)
                .Indexes.ListAsync(cancellationToken))
            .ToListAsync(cancellationToken))
            .SingleOrDefault(index => index.GetValue("name", "").AsString == selector.Index.Identifier);
        if (actual is null || !MongoDbPhysicalSchemaExecutor.IndexMatches(
                actual,
                selector.IndexKeys,
                unique: false,
                partialFilter: null))
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation index '{selector.Index.Identifier}' on collection " +
                $"'{selector.StorageObject.Identifier}' conflicts with the executable mutation binding.");
        }
    }

    private async Task BackfillFencesAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        CancellationToken cancellationToken)
    {
        await BackfillFenceAsync(binding, binding.Primary, cancellationToken);
        if (binding.Linked is not null)
            await BackfillFenceAsync(binding, binding.Linked, cancellationToken);
    }

    private async Task BackfillFenceAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(selector.StorageObject.Identifier);
        var discriminator = Builders<BsonDocument>.Filter.Eq(
            selector.DiscriminatorField,
            selector.DiscriminatorValue);
        using var cursor = await collection.Find(discriminator).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                if (beforeBackfillWrite is not null)
                    await beforeBackfillWrite(cancellationToken);
                await collection.UpdateOneAsync(
                    CurrentDocumentFilter(binding.Route, selector, document),
                    Builders<BsonDocument>.Update.Set(binding.FenceField, binding.Fingerprint),
                    cancellationToken: cancellationToken);
            }
        }
    }

    private static FilterDefinition<BsonDocument> CurrentDocumentFilter(
        ExecutableStorageRoute route,
        MongoDbPhysicalMutationSelector selector,
        BsonDocument document)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(
            MongoDbPhysicalStorageFields.Id,
            document[MongoDbPhysicalStorageFields.Id]);
        if (document.TryGetValue(MongoDbPhysicalStorageFields.Incarnation, out var incarnation))
            filter &= Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Incarnation, incarnation);
        var versionField = selector.Target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Version.Identifier
            : MongoDbPhysicalStorageFields.LinkedPrimaryVersion;
        if (document.TryGetValue(versionField, out var version))
            filter &= Builders<BsonDocument>.Filter.Eq(versionField, version);
        return filter;
    }

    private async Task BackfillSelectorAsync(
        MongoDbPhysicalMutationSelectorSchemaDefinition definition,
        CancellationToken cancellationToken)
    {
        var route = definition.Route;
        var primaryCollection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        var discriminator = Builders<BsonDocument>.Filter.Eq(
            route.Discriminator.Column.Identifier,
            route.Discriminator.Value);
        using var cursor = await primaryCollection.Find(discriminator).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                var content = document[route.Envelope.CanonicalJson.Identifier].AsBsonDocument;
                var canonicalJson = MongoDbCanonicalJson.Serialize(content);
                var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(
                    canonicalJson,
                    route.ProjectedColumns);
                var updates = MirrorUpdates(
                    route,
                    document,
                    content,
                    definition.Primary,
                    projectedValues);
                if (beforeBackfillWrite is not null)
                    await beforeBackfillWrite(cancellationToken);
                await primaryCollection.UpdateOneAsync(
                    MongoDbLinkedDocumentStorage.CurrentPrimaryFilter(route, document),
                    Builders<BsonDocument>.Update.Combine(updates),
                    cancellationToken: cancellationToken);

                if (definition.Linked is not null)
                {
                    await BackfillLinkedSelectorAsync(
                        definition,
                        document,
                        content,
                        projectedValues,
                        cancellationToken);
                }
            }
        }
    }

    private async Task BackfillLinkedSelectorAsync(
        MongoDbPhysicalMutationSelectorSchemaDefinition definition,
        BsonDocument primary,
        BsonDocument content,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues,
        CancellationToken cancellationToken)
    {
        var route = definition.Route;
        var selector = definition.Linked!;
        var linked = MongoDbLinkedDocumentStorage.Create(route, primary, projectedValues);
        var updates = linked.Updates()
            .Concat(MirrorUpdates(route, primary, content, selector, projectedValues))
            .ToArray();
        await MongoDbLinkedDocumentStorage.ReconcileAsync(
            database,
            route,
            primary,
            linked,
            updates,
            cancellationToken);
    }

    private static IReadOnlyList<UpdateDefinition<BsonDocument>> MirrorUpdates(
        ExecutableStorageRoute route,
        BsonDocument primary,
        BsonDocument content,
        MongoDbPhysicalMutationSelector selector,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        var updates = new List<UpdateDefinition<BsonDocument>>();
        foreach (var mirror in selector.Fields)
        {
            var value = MongoDbPhysicalMutationStorage.ResolveMirror(
                primary,
                content,
                route,
                mirror,
                projectedValues);
            updates.Add(value.IsPresent
                ? Builders<BsonDocument>.Update.Set(mirror.Identifier, value.Value)
                : Builders<BsonDocument>.Update.Unset(mirror.Identifier));
        }
        return updates;
    }

    private async Task EnsureWriteFenceAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var metadata = await CollectionMetadataAsync(selector.StorageObject.Identifier, cancellationToken);
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        var existing = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var rules = existing.ElementCount == 0
            ? []
            : existing.ElementCount == 1 && existing.TryGetValue("$and", out var and) && and.IsBsonArray
                ? and.AsBsonArray.Select(value => value.AsBsonDocument).ToList()
                : [existing];
        var rule = FenceRule(binding, selector);
        if (!rules.Any(candidate => candidate.Equals(rule)))
            rules.Add(rule);
        var validator = new BsonDocument("$and", new BsonArray(rules
            .OrderBy(CanonicalJson, StringComparer.Ordinal)));
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = selector.StorageObject.Identifier,
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error"
            },
            cancellationToken: cancellationToken);
        await ValidateWriteFenceAsync(binding, selector, cancellationToken);
    }

    private async Task ValidateWriteFenceAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var metadata = await CollectionMetadataAsync(selector.StorageObject.Identifier, cancellationToken);
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        var validator = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var rules = validator.ElementCount == 1 && validator.TryGetValue("$and", out var and) && and.IsBsonArray
            ? and.AsBsonArray.Select(value => value.AsBsonDocument).ToArray()
            : [];
        if (options.GetValue("validationLevel", "").AsString != "strict" ||
            options.GetValue("validationAction", "").AsString != "error" ||
            !rules.Any(candidate => candidate.Equals(FenceRule(binding, selector))))
        {
            throw new InvalidOperationException(
                $"MongoDB bounded-mutation write fence on collection '{selector.StorageObject.Identifier}' " +
                $"does not certify binding '{binding.MutationIdentity}'.");
        }
    }

    private async Task<BsonDocument> CollectionMetadataAsync(
        string collection,
        CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions
            {
                Filter = Builders<BsonDocument>.Filter.Eq("name", collection)
            },
            cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).Single();
    }

    private static BsonDocument FenceRule(
        MongoDbPhysicalMutationSchemaBinding binding,
        MongoDbPhysicalMutationSelector selector)
    {
        var requirements = new BsonArray
        {
            new BsonDocument(binding.FenceField, binding.Fingerprint),
            new BsonDocument(
                MongoDbPhysicalStorageFields.Incarnation,
                new BsonDocument("$exists", true))
        };
        if (selector.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
        {
            requirements.Add(new BsonDocument(
                MongoDbPhysicalStorageFields.LinkedPrimaryVersion,
                new BsonDocument("$exists", true)));
        }
        return new BsonDocument("$or", new BsonArray
        {
            new BsonDocument(
                selector.DiscriminatorField,
                new BsonDocument("$ne", selector.DiscriminatorValue)),
            new BsonDocument("$and", requirements)
        });
    }

    private async Task ValidateBindingAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        bool validateDocuments,
        CancellationToken cancellationToken)
    {
        await ValidateWriteFenceAsync(binding, binding.Primary, cancellationToken);
        if (binding.Linked is not null)
            await ValidateWriteFenceAsync(binding, binding.Linked, cancellationToken);
        if (!validateDocuments)
            return;

        await ValidateFenceDocumentsAsync(binding, binding.Primary, cancellationToken);
        if (binding.Linked is not null)
            await ValidateFenceDocumentsAsync(binding, binding.Linked, cancellationToken);
    }

    private async Task ValidateFenceDocumentsAsync(
        MongoDbPhysicalMutationSchemaBinding binding,
        MongoDbPhysicalMutationSelector selector,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(selector.StorageObject.Identifier);
        using var cursor = await collection.Find(Builders<BsonDocument>.Filter.Eq(
                selector.DiscriminatorField,
                selector.DiscriminatorValue))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                if (!TryReadDotted(document, binding.FenceField, out var fence) ||
                    !fence.IsString ||
                    fence.AsString != binding.Fingerprint)
                {
                    throw new InvalidOperationException(
                        $"MongoDB bounded-mutation write fence '{binding.FenceField}' conflicts with its executable binding.");
                }
            }
        }
    }

    private async Task ValidateSelectorAsync(
        MongoDbPhysicalMutationSelectorSchemaDefinition definition,
        bool validateDocuments,
        CancellationToken cancellationToken)
    {
        await ValidateIndexAsync(definition.Primary, cancellationToken);
        if (definition.Linked is not null)
            await ValidateIndexAsync(definition.Linked, cancellationToken);
        if (!validateDocuments)
            return;

        var route = definition.Route;
        var primaryCollection = database.GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier);
        using var cursor = await primaryCollection.Find(Builders<BsonDocument>.Filter.Eq(
                route.Discriminator.Column.Identifier,
                route.Discriminator.Value))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var primary in cursor.Current)
            {
                var content = primary[route.Envelope.CanonicalJson.Identifier].AsBsonDocument;
                var canonicalJson = MongoDbCanonicalJson.Serialize(content);
                var projectedValues = MongoDbPhysicalProjectionValues.ResolveAll(canonicalJson, route.ProjectedColumns);
                ValidateMirrors(route, primary, primary, content, definition.Primary, projectedValues);
                if (definition.Linked is null)
                    continue;

                var linkedState = MongoDbLinkedDocumentStorage.Create(route, primary, projectedValues);
                var linked = await database.GetCollection<BsonDocument>(definition.Linked.StorageObject.Identifier)
                    .Find(Builders<BsonDocument>.Filter.Eq(
                        MongoDbPhysicalStorageFields.Id,
                        linkedState.Identity))
                    .SingleOrDefaultAsync(cancellationToken);
                if (linked is null ||
                    linked.GetValue(MongoDbPhysicalStorageFields.Incarnation, BsonNull.Value) != linkedState.Incarnation ||
                    linked.GetValue(MongoDbPhysicalStorageFields.LinkedPrimaryVersion, BsonNull.Value) != linkedState.PrimaryVersion)
                {
                    throw new InvalidOperationException(
                        $"MongoDB bounded-mutation selector '{definition.LogicalIndexIdentity}' has missing or stale linked mirror state.");
                }
                ValidateMirrors(route, linked, primary, content, definition.Linked, projectedValues);
            }
        }
        if (definition.Linked is not null)
            await ValidateLinkedRowsAsync(definition, cancellationToken);
    }

    private async Task ValidateLinkedRowsAsync(
        MongoDbPhysicalMutationSelectorSchemaDefinition definition,
        CancellationToken cancellationToken)
    {
        var route = definition.Route;
        var selector = definition.Linked!;
        var relationship = route.LinkedRelationship!;
        var linkedCollection = database.GetCollection<BsonDocument>(selector.StorageObject.Identifier);
        using var cursor = await linkedCollection.Find(Builders<BsonDocument>.Filter.Eq(
                selector.DiscriminatorField,
                selector.DiscriminatorValue))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var linked in cursor.Current)
            {
                if (!linked.TryGetValue(relationship.DocumentId.Identifier, out var documentId) ||
                    !linked.TryGetValue(relationship.StorageScope.Identifier, out var storageScope) ||
                    !linked.TryGetValue(MongoDbPhysicalStorageFields.Incarnation, out var incarnation) ||
                    !linked.TryGetValue(MongoDbPhysicalStorageFields.LinkedPrimaryVersion, out var primaryVersion))
                {
                    throw new InvalidOperationException(
                        $"MongoDB bounded-mutation selector '{definition.LogicalIndexIdentity}' has malformed linked mirror state.");
                }

                var identityValues = new BsonDocument
                {
                    [relationship.DocumentId.Identifier] = documentId,
                    [relationship.DocumentKind.Identifier] = selector.DiscriminatorValue,
                    [relationship.StorageScope.Identifier] = storageScope
                };
                var expectedIdentity = MongoDbPhysicalSchemaExecutor.KeyDocument(route.AuxiliaryKey!, identityValues);
                var hasCanonicalIdentity = linked.TryGetValue(MongoDbPhysicalStorageFields.Id, out var actualIdentity) &&
                                           actualIdentity.Equals(expectedIdentity);
                var hasPrimary = hasCanonicalIdentity && await database
                    .GetCollection<BsonDocument>(route.PrimaryStorage.Name.Identifier)
                    .Find(
                        Builders<BsonDocument>.Filter.Eq(route.Envelope.Id.Identifier, documentId) &
                        Builders<BsonDocument>.Filter.Eq(route.Envelope.DocumentKind.Identifier, route.Discriminator.Value) &
                        Builders<BsonDocument>.Filter.Eq(route.Envelope.StorageScope.Identifier, storageScope) &
                        Builders<BsonDocument>.Filter.Eq(MongoDbPhysicalStorageFields.Incarnation, incarnation) &
                        Builders<BsonDocument>.Filter.Eq(route.Envelope.Version.Identifier, primaryVersion))
                    .Limit(1)
                    .AnyAsync(cancellationToken);
                if (!hasPrimary)
                {
                    throw new InvalidOperationException(
                        $"MongoDB bounded-mutation selector '{definition.LogicalIndexIdentity}' has orphan or duplicate linked mirror state.");
                }
            }
        }
    }

    private static void ValidateMirrors(
        ExecutableStorageRoute route,
        BsonDocument target,
        BsonDocument primary,
        BsonDocument content,
        MongoDbPhysicalMutationSelector selector,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        foreach (var mirror in selector.Fields)
        {
            var expected = MongoDbPhysicalMutationStorage.ResolveMirror(
                primary,
                content,
                route,
                mirror,
                projectedValues);
            var present = TryReadDotted(target, mirror.Identifier, out var actual);
            if (expected.IsPresent != present ||
                (expected.IsPresent && !expected.Value.Equals(actual)))
            {
                throw new InvalidOperationException(
                    $"MongoDB bounded-mutation mirror '{mirror.Identifier}' conflicts with canonical document state.");
            }
        }
    }

    private static bool TryReadDotted(BsonDocument document, string path, out BsonValue value)
    {
        value = document;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!value.IsBsonDocument || !value.AsBsonDocument.TryGetValue(segment, out value))
                return false;
        }
        return true;
    }

    private static string CanonicalJson(BsonDocument document) =>
        document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson });
}
