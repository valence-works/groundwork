using System.Security.Cryptography;
using System.Text;
using Groundwork.DiagnosticRecords;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.DiagnosticRecords;

public static class MongoDbDiagnosticRecordMaterializer
{
    public static async Task MaterializeAsync(
        IMongoDatabase database,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(definition);
        definition = DiagnosticRecordStreamDefinitionSnapshot.Capture(definition);

        var existing = await ReadCollectionMetadataAsync(database, cancellationToken);
        var definitionEnsured = false;
        if (existing.TryGetValue(MongoDbDiagnosticRecordNames.StreamDefinitions, out var definitionsMetadata))
        {
            ValidateSimpleCollation(MongoDbDiagnosticRecordNames.StreamDefinitions, definitionsMetadata);
            await EnsureDefinitionAsync(database, definition, cancellationToken);
            definitionEnsured = true;
        }
        foreach (var name in Collections)
            await EnsureSimpleCollectionAsync(database, existing, name, cancellationToken);

        if (!definitionEnsured)
            await EnsureDefinitionAsync(database, definition, cancellationToken);
        await EnsureIndexesAsync(database, definition, cancellationToken);
    }

    private static string[] Collections =>
    [
        MongoDbDiagnosticRecordNames.Records,
        MongoDbDiagnosticRecordNames.Streams,
        MongoDbDiagnosticRecordNames.AppendOperations,
        MongoDbDiagnosticRecordNames.AppendOutcomes,
        MongoDbDiagnosticRecordNames.TrimOperations,
        MongoDbDiagnosticRecordNames.ProviderState,
        MongoDbDiagnosticRecordNames.StreamDefinitions
    ];

    private static async Task<Dictionary<string, BsonDocument>> ReadCollectionMetadataAsync(
        IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionsAsync(cancellationToken: cancellationToken);
        return (await cursor.ToListAsync(cancellationToken)).ToDictionary(
            document => document["name"].AsString,
            StringComparer.Ordinal);
    }

    private static async Task EnsureSimpleCollectionAsync(
        IMongoDatabase database,
        IDictionary<string, BsonDocument> existing,
        string name,
        CancellationToken cancellationToken)
    {
        if (existing.TryGetValue(name, out var metadata))
        {
            ValidateSimpleCollation(name, metadata);
            return;
        }
        try
        {
            await database.CreateCollectionAsync(
                name,
                new CreateCollectionOptions { Collation = Collation.Simple },
                cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code == 48)
        {
            // A concurrent materializer won collection creation. Validate its result below.
        }

        var refreshed = await ReadCollectionMetadataAsync(database, cancellationToken);
        if (!refreshed.TryGetValue(name, out metadata))
            throw new InvalidOperationException($"MongoDB collection '{name}' was not materialized.");
        ValidateSimpleCollation(name, metadata);
        existing[name] = metadata;
    }

    private static void ValidateSimpleCollation(string name, BsonDocument metadata)
    {
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (!options.TryGetValue("collation", out var collation))
            return; // MongoDB's absent collection collation is the binary/simple collation.
        var locale = collation.AsBsonDocument.GetValue("locale", "simple").AsString;
        if (!StringComparer.Ordinal.Equals(locale, "simple"))
            throw new InvalidOperationException(
                $"MongoDB diagnostic collection '{name}' must use simple collation, but uses '{locale}'.");
    }

    private static async Task EnsureDefinitionAsync(
        IMongoDatabase database,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions)
            .WithWriteConcern(WriteConcern.WMajority);
        var id = DefinitionId(definition.Stream);
        var expected = DefinitionDocument(id, definition);
        try
        {
            await collection.InsertOneAsync(expected, cancellationToken: cancellationToken);
            return;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Existing definition is validated below.
        }

        var actual = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync(cancellationToken);
        var state = DiagnosticRecordPhysicalSchemaState.Capture(definition);
        if (!StringComparer.Ordinal.Equals(actual["fingerprint"].AsString, expected["fingerprint"].AsString) ||
            actual["schema_version"].ToInt32() != definition.SchemaVersion ||
            !StringComparer.Ordinal.Equals(actual["algorithm_manifest_fingerprint"].AsString, state.ComparisonAlgorithmManifestFingerprint))
            throw new InvalidOperationException(
                $"MongoDB diagnostic stream '{definition.Stream.Value}' has an incompatible persisted definition or comparison-key algorithm state.");
    }

    private static BsonDocument DefinitionDocument(string id, DiagnosticRecordStreamDefinition definition)
    {
        var state = DiagnosticRecordPhysicalSchemaState.Capture(definition);
        var canonical = new BsonDocument
        {
            { "stream", definition.Stream.Value },
            { "schema_version", definition.SchemaVersion },
            { "logical_storage_name", definition.LogicalStorageName },
            { "max_clock_skew_ticks", definition.MaxOperationClockSkew.Ticks },
            { "append_window_ticks", definition.AppendIdempotencyWindow.Ticks },
            { "trim_window_ticks", definition.TrimIdempotencyWindow.Ticks },
            { "logical_high_water_field", definition.LogicalHighWaterField is null ? BsonNull.Value : definition.LogicalHighWaterField },
            { "limits", new BsonDocument
                {
                    { "max_batch_records", definition.Limits.MaxBatchRecords },
                    { "max_payload_bytes", definition.Limits.MaxPayloadBytes },
                    { "max_record_id_bytes", definition.Limits.MaxRecordIdBytes },
                    { "max_fields_per_record", definition.Limits.MaxFieldsPerRecord },
                    { "max_query_limit", definition.Limits.MaxQueryLimit },
                    { "max_predicate_nodes", definition.Limits.MaxPredicateNodes },
                    { "max_predicate_values", definition.Limits.MaxPredicateValues },
                    { "max_json_depth", definition.Limits.MaxJsonDepth }
                }
            },
            { "fields", new BsonArray(definition.Fields.OrderBy(field => field.Name, StringComparer.Ordinal).Select(field => new BsonDocument
                {
                    { "name", field.Name }, { "type", (int)field.Type }, { "cardinality", (int)field.Cardinality },
                    { "predicates", new BsonArray(field.SupportedPredicates.Order().Select(value => (int)value)) },
                    { "required", field.IsRequired }, { "orderable", field.IsOrderable },
                    { "latest", field.SupportsLatestPerKey }, { "case_policy", (int)field.CasePolicy },
                    { "max_values", field.MaxValues }, { "max_string_bytes", field.MaxStringBytes is null ? BsonNull.Value : field.MaxStringBytes.Value },
                    { "missing", (int)field.MissingValueBehavior }
                }))
            },
            { "ascii_comparison_key_algorithm", DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId },
            { "ordinal_comparison_key_algorithm", DiagnosticStringComparisonKey.OrdinalAlgorithmId },
            { "algorithm_manifest", state.ComparisonAlgorithmManifest },
            { "algorithm_manifest_fingerprint", state.ComparisonAlgorithmManifestFingerprint },
            { "canonical_definition", state.CanonicalDefinition }
        };
        var fingerprint = state.DefinitionFingerprint;
        canonical.InsertAt(0, new("_id", id));
        canonical.Add("fingerprint", fingerprint);
        return canonical;
    }

    private static string DefinitionId(DiagnosticStreamId stream) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stream.Value)));

    private static async Task EnsureIndexesAsync(
        IMongoDatabase database,
        DiagnosticRecordStreamDefinition definition,
        CancellationToken cancellationToken)
    {
        var records = database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.Records);
        await CreateIndexesAsync(records,
        [
            Index("ux_groundwork_diagnostic_records_scope_record", true,
                ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("record_id", 1)),
            Index("ix_groundwork_diagnostic_records_scope_cursor", false,
                ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("cursor", 1)),
            Index("ix_groundwork_diagnostic_records_scope_fields", false,
                ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("query_values.name", 1),
                ("query_values.type", 1), ("query_values.comparison_key_hash", 1), ("cursor", 1)),
            Index("ix_groundwork_diagnostic_records_scope_field_native", false,
                ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("query_values.name", 1),
                ("query_values.type", 1), ("query_values.native", 1), ("cursor", 1))
        ], cancellationToken);

        var ordered = definition.Fields.Where(field => field.IsOrderable || field.SupportsLatestPerKey)
            .Append(DiagnosticRecordFieldResolver.Resolve(definition, DiagnosticRecordFieldNames.OccurredAt)!)
            .DistinctBy(field => field.Name, StringComparer.Ordinal);
        await CreateIndexesAsync(records, ordered.SelectMany(field =>
        {
            var hash = MongoDbDiagnosticRecordStore.SortKey(field.Name);
            var result = new List<CreateIndexModel<BsonDocument>>
            {
                Index($"ix_groundwork_diagnostic_records_order_{hash}", false,
                    ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1),
                    (MongoDbDiagnosticRecordStore.SortPrefixPath(field.Name), 1), ("cursor", 1))
            };
            if (field.SupportsLatestPerKey)
                result.Add(Index($"ix_groundwork_diagnostic_records_latest_{hash}", false,
                    ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1),
                    (MongoDbDiagnosticRecordStore.SortPrefixPath(field.Name), 1), ("cursor", -1)));
            return result;
        }), cancellationToken);

        foreach (var operationsName in new[] { MongoDbDiagnosticRecordNames.AppendOperations, MongoDbDiagnosticRecordNames.TrimOperations })
        {
            var operations = database.GetCollection<BsonDocument>(operationsName);
            await CreateIndexesAsync(operations,
            [
                Index($"ix_{operationsName}_outcome_cleanup", false, ("has_outcome", 1), ("outcome_expires_at_ticks", 1), ("_id", 1)),
                Index($"ix_{operationsName}_tombstone_cleanup", false, ("tombstone_until_ticks", 1), ("_id", 1))
            ], cancellationToken);
        }

        await CreateIndexesAsync(database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.AppendOutcomes),
        [
            Index("ux_groundwork_diagnostic_append_outcomes_operation_ordinal", true, ("operation_id", 1), ("ordinal", 1))
        ], cancellationToken);
    }

    private static CreateIndexModel<BsonDocument> Index(
        string name,
        bool unique,
        params (string Name, int Direction)[] keys)
    {
        var document = new BsonDocument(keys.Select(key => new BsonElement(key.Name, key.Direction)));
        return new(document, new CreateIndexOptions { Name = name, Unique = unique, Collation = Collation.Simple });
    }

    private static async Task CreateIndexesAsync(
        IMongoCollection<BsonDocument> collection,
        IEnumerable<CreateIndexModel<BsonDocument>> indexes,
        CancellationToken cancellationToken)
    {
        try
        {
            await collection.Indexes.CreateManyAsync(indexes, cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.Code is 85 or 86)
        {
            throw new InvalidOperationException(
                $"MongoDB diagnostic-record indexes in '{collection.CollectionNamespace.CollectionName}' conflict with the required simple-collation schema.",
                exception);
        }
    }
}
