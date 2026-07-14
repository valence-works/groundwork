using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// One deduplicated desired-state definition for the physical selector shared by every mutation
/// bound to the same logical index. Per-mutation binding definitions remain additive write fences.
/// </summary>
internal sealed class MongoDbPhysicalMutationSelectorSchemaDefinition
{
    public const string DefinitionKind = "mongodb.bounded-mutation-selector.v1";

    private MongoDbPhysicalMutationSelectorSchemaDefinition(
        string providerName,
        ExecutableStorageRoute route,
        string logicalIndexIdentity,
        MongoDbPhysicalMutationSelector primary,
        MongoDbPhysicalMutationSelector? linked,
        string? canonicalJson = null)
    {
        ProviderName = providerName;
        Route = route;
        LogicalIndexIdentity = logicalIndexIdentity;
        Primary = primary;
        Linked = linked;
        CanonicalJson = canonicalJson ?? Serialize(this);
    }

    public string ProviderName { get; }

    public ExecutableStorageRoute Route { get; }

    public string LogicalIndexIdentity { get; }

    public MongoDbPhysicalMutationSelector Primary { get; }

    public MongoDbPhysicalMutationSelector? Linked { get; }

    public string CanonicalJson { get; }

    public ProviderPhysicalSchemaDefinition ProviderDefinition => new(
        ProviderName,
        Route.StorageUnit,
        DefinitionKind,
        LogicalIndexIdentity,
        CanonicalJson);

    public static IReadOnlyList<MongoDbPhysicalMutationSelectorSchemaDefinition> Compile(
        IReadOnlyList<MongoDbPhysicalMutationBinding> bindings) =>
        bindings
            .GroupBy(binding => new
            {
                binding.Schema.Route.StorageUnit,
                binding.Schema.Primary.LogicalIndexIdentity
            })
            .Select(group => Create(group.Select(binding => binding.Schema).ToArray()))
            .OrderBy(definition => definition.Route.StorageUnit.Value, StringComparer.Ordinal)
            .ThenBy(definition => definition.LogicalIndexIdentity, StringComparer.Ordinal)
            .ToArray();

    public static MongoDbPhysicalMutationSelectorSchemaDefinition Deserialize(string canonicalJson)
    {
        var root = BsonDocument.Parse(canonicalJson);
        if (root.GetValue("schemaVersion").AsString != "1")
            throw new InvalidOperationException("Unsupported MongoDB bounded-mutation selector schema version.");
        var route = ExecutableStorageRouteSerializer.Deserialize(root["canonicalRoute"].AsString);
        var result = new MongoDbPhysicalMutationSelectorSchemaDefinition(
            root["providerName"].AsString,
            route,
            root["logicalIndexIdentity"].AsString,
            MongoDbPhysicalMutationSelector.Deserialize(root["primary"].AsBsonDocument),
            root.GetValue("linked", BsonNull.Value).IsBsonNull
                ? null
                : MongoDbPhysicalMutationSelector.Deserialize(root["linked"].AsBsonDocument),
            canonicalJson);
        if (route.Fingerprint != root["routeFingerprint"].AsString ||
            result.Primary.LogicalIndexIdentity != result.LogicalIndexIdentity ||
            result.Linked is not null && result.Linked.LogicalIndexIdentity != result.LogicalIndexIdentity ||
            Serialize(result) != canonicalJson)
        {
            throw new InvalidOperationException(
                "MongoDB bounded-mutation selector definition is not canonical or has inconsistent route evidence.");
        }
        return result;
    }

    private static MongoDbPhysicalMutationSelectorSchemaDefinition Create(
        IReadOnlyList<MongoDbPhysicalMutationSchemaBinding> bindings)
    {
        var first = bindings[0];
        var definition = new MongoDbPhysicalMutationSelectorSchemaDefinition(
            first.ProviderName,
            first.Route,
            first.Primary.LogicalIndexIdentity,
            first.Primary,
            first.Linked);
        if (bindings.Any(binding =>
                binding.ProviderName != definition.ProviderName ||
                binding.Route.Fingerprint != definition.Route.Fingerprint ||
                binding.Primary.LogicalIndexIdentity != definition.LogicalIndexIdentity ||
                !binding.Primary.Serialize().Equals(definition.Primary.Serialize()) ||
                !Equals(binding.Linked?.Serialize(), definition.Linked?.Serialize())))
        {
            throw new InvalidOperationException(
                $"MongoDB bounded mutations for logical index '{definition.LogicalIndexIdentity}' do not share one exact physical selector.");
        }
        return definition;
    }

    private static string Serialize(MongoDbPhysicalMutationSelectorSchemaDefinition definition) =>
        new BsonDocument
        {
            ["schemaVersion"] = "1",
            ["providerName"] = definition.ProviderName,
            ["storageUnit"] = definition.Route.StorageUnit.Value,
            ["logicalIndexIdentity"] = definition.LogicalIndexIdentity,
            ["routeFingerprint"] = definition.Route.Fingerprint,
            ["canonicalRoute"] = ExecutableStorageRouteSerializer.Serialize(definition.Route),
            ["primary"] = definition.Primary.Serialize(),
            ["linked"] = definition.Linked is null ? BsonNull.Value : definition.Linked.Serialize()
        }.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
}
