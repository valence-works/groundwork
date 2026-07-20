using System.Security.Cryptography;
using System.Text;
using Groundwork.DiagnosticRecords;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb.DiagnosticRecords;

/// <summary>
/// Performs a non-mutating MongoDB inspection of the complete diagnostic-record deployment.
/// Runtime admission deliberately lists and reads only: collection creation, index creation, and
/// definition insertion remain exclusive to the explicit deployment workflow.
/// </summary>
public sealed class MongoDbDiagnosticRecordDeploymentInspector(string connectionString, string databaseName)
    : IDiagnosticRecordDeploymentInspector
{
    public string Provider => "mongodb";

    private readonly string connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A MongoDB connection string is required.", nameof(connectionString))
        : connectionString;

    private readonly string databaseName = string.IsNullOrWhiteSpace(databaseName)
        ? throw new ArgumentException("A MongoDB database name is required.", nameof(databaseName))
        : databaseName;

    public async ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        foreach (var stream in deployment.Streams)
            MongoDbDiagnosticRecordValidator.ValidateDefinitionAndThrow(stream);

        if (deployment.Streams.Count == 0)
            return DiagnosticRecordDeploymentInspection.Ready(Provider);

        using var client = new MongoClient(connectionString);
        var database = client.GetDatabase(databaseName);
        if (!await MongoDbTransactionTopology.SupportsTransactionsAsync(database, cancellationToken))
        {
            throw new NotSupportedException(
                $"MongoDB diagnostic records require multi-document transactions; deployment type " +
                $"'{client.Cluster.Description.Type}' is unsupported. Configure a replica set or sharded cluster.");
        }

        var collections = await ReadCollectionMetadataAsync(database, cancellationToken);
        if (RequiredCollections.Any(name => !collections.ContainsKey(name)))
            return DiagnosticRecordDeploymentInspection.Missing(Provider, deployment);
        if (RequiredCollections.Any(name => !HasCompatibleCollectionOptions(collections[name])))
            return DiagnosticRecordDeploymentInspection.Drifted(Provider, deployment.Streams.Select(stream => stream.Stream.Value).ToArray());

        var indexStatus = await InspectIndexesAsync(database, deployment, cancellationToken);
        if (indexStatus == IndexInspectionStatus.Missing)
            return DiagnosticRecordDeploymentInspection.Missing(Provider, deployment);
        if (indexStatus == IndexInspectionStatus.Drifted)
            return DiagnosticRecordDeploymentInspection.Drifted(Provider, deployment.Streams.Select(stream => stream.Stream.Value).ToArray());

        var missing = new List<string>();
        var drifted = new List<string>();
        var definitions = database.GetCollection<BsonDocument>(MongoDbDiagnosticRecordNames.StreamDefinitions);
        foreach (var stream in deployment.Streams)
        {
            var actual = await definitions.Find(Builders<BsonDocument>.Filter.Eq("_id", DefinitionId(stream.Stream)))
                .FirstOrDefaultAsync(cancellationToken);
            if (actual is null)
            {
                missing.Add(stream.Stream.Value);
                continue;
            }

            var expected = DiagnosticRecordPhysicalSchemaState.Capture(stream);
            if (!actual.TryGetValue("fingerprint", out var fingerprint) ||
                fingerprint.BsonType != BsonType.String ||
                !StringComparer.Ordinal.Equals(fingerprint.AsString, expected.DefinitionFingerprint) ||
                !actual.TryGetValue("schema_version", out var schemaVersion) ||
                !schemaVersion.IsNumeric ||
                schemaVersion.ToInt32() != stream.SchemaVersion ||
                !actual.TryGetValue("algorithm_manifest_fingerprint", out var algorithmFingerprint) ||
                algorithmFingerprint.BsonType != BsonType.String ||
                !StringComparer.Ordinal.Equals(
                    algorithmFingerprint.AsString,
                    expected.ComparisonAlgorithmManifestFingerprint))
                drifted.Add(stream.Stream.Value);
        }

        if (missing.Count != 0)
            return DiagnosticRecordDeploymentInspection.Missing(Provider, deployment, missing);
        if (drifted.Count != 0)
            return DiagnosticRecordDeploymentInspection.Drifted(Provider, drifted);
        return DiagnosticRecordDeploymentInspection.Ready(Provider);
    }

    private static readonly string[] RequiredCollections =
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

    private static bool HasCompatibleCollectionOptions(BsonDocument metadata)
    {
        if (!StringComparer.Ordinal.Equals(metadata.GetValue("type", string.Empty).AsString, "collection"))
            return false;
        var options = metadata.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).ToBoolean() ||
            options.Contains("timeseries") ||
            options.Contains("clusteredIndex"))
            return false;
        if (!options.TryGetValue("collation", out var collation))
            return true;
        return collation.BsonType == BsonType.Document &&
               StringComparer.Ordinal.Equals(
                   collation.AsBsonDocument.GetValue("locale", string.Empty).AsString,
                   "simple");
    }

    private static async Task<IndexInspectionStatus> InspectIndexesAsync(
        IMongoDatabase database,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        foreach (var group in RequiredIndexes(deployment).GroupBy(index => index.Collection, StringComparer.Ordinal))
        {
            var collection = database.GetCollection<BsonDocument>(group.Key);
            using var cursor = await collection.Indexes.ListAsync(cancellationToken);
            var indexes = (await cursor.ToListAsync(cancellationToken)).ToDictionary(
                index => index["name"].AsString,
                StringComparer.Ordinal);

            foreach (var required in group)
            {
                if (!indexes.TryGetValue(required.Name, out var actual))
                    return IndexInspectionStatus.Missing;
                if (!Matches(required, actual))
                    return IndexInspectionStatus.Drifted;
            }
        }

        return IndexInspectionStatus.Ready;
    }

    private static bool Matches(RequiredIndex expected, BsonDocument actual) =>
        actual.TryGetValue("key", out var keys) && keys.BsonType == BsonType.Document &&
        keys.AsBsonDocument.Equals(expected.Keys) &&
        actual.GetValue("unique", false).ToBoolean() == expected.Unique &&
        !actual.GetValue("sparse", false).ToBoolean() &&
        !actual.GetValue("hidden", false).ToBoolean() &&
        !actual.Contains("partialFilterExpression") &&
        !actual.Contains("expireAfterSeconds") &&
        HasSimpleIndexCollation(actual);

    private static bool HasSimpleIndexCollation(BsonDocument index)
    {
        if (!index.TryGetValue("collation", out var collation) || collation.IsBsonNull)
            return true;
        return collation.BsonType == BsonType.Document &&
            StringComparer.Ordinal.Equals(collation.AsBsonDocument.GetValue("locale", string.Empty).AsString, "simple");
    }

    private static IEnumerable<RequiredIndex> RequiredIndexes(DiagnosticRecordDeploymentManifest deployment)
    {
        yield return Index(MongoDbDiagnosticRecordNames.Records, "ux_groundwork_diagnostic_records_scope_record", true,
            ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("record_id", 1));
        yield return Index(MongoDbDiagnosticRecordNames.Records, "ix_groundwork_diagnostic_records_scope_cursor", false,
            ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("cursor", 1));
        yield return Index(MongoDbDiagnosticRecordNames.Records, "ix_groundwork_diagnostic_records_scope_fields", false,
            ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("query_values.name", 1),
            ("query_values.type", 1), ("query_values.comparison_key_hash", 1), ("cursor", 1));
        yield return Index(MongoDbDiagnosticRecordNames.Records, "ix_groundwork_diagnostic_records_scope_field_native", false,
            ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1), ("query_values.name", 1),
            ("query_values.type", 1), ("query_values.native", 1), ("cursor", 1));

        foreach (var group in deployment.Streams
                     .SelectMany(stream => stream.Fields.Where(field => field.IsOrderable || field.SupportsLatestPerKey)
                         .Append(DiagnosticRecordFieldResolver.Resolve(stream, DiagnosticRecordFieldNames.OccurredAt)!))
                     .GroupBy(field => field.Name, StringComparer.Ordinal))
        {
            var field = group.First();
            var hash = MongoDbDiagnosticRecordStore.SortKey(field.Name);
            yield return Index(MongoDbDiagnosticRecordNames.Records, $"ix_groundwork_diagnostic_records_order_{hash}", false,
                ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1),
                (MongoDbDiagnosticRecordStore.SortPrefixPath(field.Name), 1), ("cursor", 1));
            if (group.Any(item => item.SupportsLatestPerKey))
                yield return Index(MongoDbDiagnosticRecordNames.Records, $"ix_groundwork_diagnostic_records_latest_{hash}", false,
                    ("tenant_id", 1), ("scope_id", 1), ("stream_id", 1),
                    (MongoDbDiagnosticRecordStore.SortPrefixPath(field.Name), 1), ("cursor", -1));
        }

        foreach (var collection in new[] { MongoDbDiagnosticRecordNames.AppendOperations, MongoDbDiagnosticRecordNames.TrimOperations })
        {
            yield return Index(collection, $"ix_{collection}_outcome_cleanup", false,
                ("has_outcome", 1), ("outcome_expires_at_ticks", 1), ("_id", 1));
            yield return Index(collection, $"ix_{collection}_tombstone_cleanup", false,
                ("tombstone_until_ticks", 1), ("_id", 1));
        }

        yield return Index(MongoDbDiagnosticRecordNames.AppendOutcomes,
            "ux_groundwork_diagnostic_append_outcomes_operation_ordinal", true,
            ("operation_id", 1), ("ordinal", 1));
    }

    private static RequiredIndex Index(string collection, string name, bool unique, params (string Name, int Direction)[] keys) =>
        new(collection, name, unique, new BsonDocument(keys.Select(key => new BsonElement(key.Name, key.Direction))));

    private static string DefinitionId(DiagnosticStreamId stream) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stream.Value)));

    private sealed record RequiredIndex(string Collection, string Name, bool Unique, BsonDocument Keys);

    private enum IndexInspectionStatus
    {
        Ready,
        Missing,
        Drifted
    }
}
