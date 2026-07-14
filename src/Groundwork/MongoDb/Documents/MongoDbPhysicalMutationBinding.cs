using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Store;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Groundwork.MongoDb.Documents;

/// <summary>
/// The one immutable MongoDB execution binding shared by materialization, capability evidence,
/// runtime mutation dispatch, and explain evidence.
/// </summary>
internal sealed class MongoDbPhysicalMutationBinding
{
    private MongoDbPhysicalMutationBinding(
        PhysicalMutationPlan plan,
        MongoDbPhysicalMutationSchemaBinding schema)
    {
        Plan = plan;
        Schema = schema;
        Certification = new PhysicalMutationExecutionCertification(
            plan,
            schema.Primary.ToCertification(),
            schema.Linked?.ToCertification(),
            schema.Fingerprint);
    }

    public PhysicalMutationPlan Plan { get; }

    public MongoDbPhysicalMutationSchemaBinding Schema { get; }

    public PhysicalMutationExecutionCertification Certification { get; }

    public ProviderPhysicalSchemaDefinition ProviderDefinition => new(
        Schema.ProviderName,
        Schema.Route.StorageUnit,
        MongoDbPhysicalMutationSchemaBinding.DefinitionKind,
        Plan.MutationIdentity,
        Schema.CanonicalJson);

    public bool Certifies(PhysicalMutationPlan plan) =>
        Plan.Equals(plan);

    public static IReadOnlyList<MongoDbPhysicalMutationBinding> Compile(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        ProviderIdentity provider)
    {
        var capabilities = MongoDbPhysicalMutationCapabilities.Create(route, storage, provider);
        var compilation = PhysicalMutationPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }

        MongoDbPhysicalMutationModelValidation.Validate(route, storage);
        return compilation.Plans
            .OrderBy(plan => plan.MutationIdentity, StringComparer.Ordinal)
            .Select(plan => new MongoDbPhysicalMutationBinding(
                plan,
                MongoDbPhysicalMutationSchemaBinding.Create(route, storage, plan)))
            .ToArray();
    }
}

internal static class MongoDbPhysicalMutationCapabilities
{
    public static IReadOnlySet<PortableQueryOperation> Operations { get; } =
        Enum.GetValues<PortableQueryOperation>().ToFrozenSet();

    public static PhysicalQueryPlannerCapabilities Create(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        ProviderIdentity provider,
        IReadOnlySet<PortableQueryOperation>? operations = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = route.Envelope.Id.Identifier,
            ["documentKind"] = route.Envelope.DocumentKind.Identifier,
            ["storageScope"] = route.Envelope.StorageScope.Identifier,
            ["version"] = route.Envelope.Version.Identifier,
            ["schemaVersion"] = route.Envelope.SchemaVersion.Identifier
        };
        foreach (var path in storage.LogicalIndexes
                     .SelectMany(index => index.Fields)
                     .Select(field => field.Path)
                     .Distinct(StringComparer.Ordinal))
        {
            fields[path] = route.ProjectedColumns.SingleOrDefault(column =>
                    column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                    column.Definition.Path == path)?.Column.Identifier
                ?? $"{MongoDbPhysicalStorageFields.NativeContent}.{path}";
        }

        return new PhysicalQueryPlannerCapabilities(
            provider,
            [PhysicalQuerySourceKind.LinkedIndex, PhysicalQuerySourceKind.NativeDocumentFields],
            operations ?? Operations,
            new Dictionary<PhysicalQuerySourceKind, string>
            {
                [PhysicalQuerySourceKind.LinkedIndex] = MongoDbPhysicalQueryHandler.LinkedIdentity,
                [PhysicalQuerySourceKind.NativeDocumentFields] = MongoDbPhysicalQueryHandler.NativeIdentity
            },
            fields,
            supportsCompoundPredicates: true,
            supportsDisjunction: true,
            supportsOffsetPaging: true,
            supportsKeysetPaging: false,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey: false);
    }
}

internal static class MongoDbPhysicalMutationModelValidation
{
    public static void Validate(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage)
    {
        var mutationQueries = storage.BoundedMutations
            .Select(mutation => storage.BoundedQueries.Single(query =>
                query.Identity == mutation.PredicateQueryIdentity))
            .DistinctBy(query => query.Identity, StringComparer.Ordinal)
            .ToArray();
        foreach (var query in mutationQueries)
        {
            var logicalIndex = storage.LogicalIndexes.Single(index =>
                index.Identity == query.IndexIdentity);
            var unsupported = MongoDbScaleBearingOperationValidation.UnsupportedOperations(storage, query);
            if (unsupported.Length != 0)
            {
                throw new InvalidOperationException(
                    $"GW-QUERY-003: MongoDB cannot certify scale-bearing bounded mutation query '{query.Identity}' operations " +
                    $"{string.Join(", ", unsupported)} as indexed: case-insensitive regular-expression semantics " +
                    "cannot be served by the declared ordinary MongoDB B-tree index.");
            }

            foreach (var field in logicalIndex.Fields.Where(field =>
                         !PhysicalDocumentFieldPaths.IsEnvelope(field.Path)))
            {
                var valueKind = logicalIndex.GetValueKind(field);
                var projections = route.ProjectedColumns.Where(candidate =>
                    candidate.Definition.Path == field.Path).ToArray();
                if (projections.Length == 0)
                {
                    if (valueKind is IndexValueKind.Number or IndexValueKind.DateTime)
                    {
                        throw new InvalidOperationException(
                            $"MongoDB cannot certify exact '{valueKind}' bounded mutation semantics for path " +
                            $"'{field.Path}' without a typed projected field.");
                    }
                    continue;
                }
                var incompatible = projections.FirstOrDefault(projection =>
                    !PortableQueryOperationCompatibility.Supports(valueKind, projection.Definition.Type));
                if (incompatible is not null)
                {
                    throw new InvalidOperationException(
                        $"MongoDB projected mutation path '{field.Path}' type '{incompatible.Definition.Type}' cannot " +
                        $"preserve logical value kind '{valueKind}'.");
                }
            }
        }
    }
}

internal static class MongoDbScaleBearingOperationValidation
{
    public static PortableQueryOperation[] UnsupportedOperations(
        StorageUnitPhysicalStorage storage,
        BoundedQueryDeclaration query)
    {
        var logicalIndex = storage.LogicalIndexes.Single(index => index.Identity == query.IndexIdentity);
        var predicates = query.PredicateFields.Count == 0
            ? logicalIndex.Fields.Take(1)
                .Select(field => new BoundedQueryPredicateField(field.Path, query.Operations))
            : query.PredicateFields;
        return predicates
            .SelectMany(predicate => predicate.Operations.Select(operation => (predicate.Path, Operation: operation)))
            .Where(item => item.Path != PhysicalDocumentFieldPaths.Id &&
                           item.Operation is PortableQueryOperation.Contains or PortableQueryOperation.StartsWith)
            .Select(item => item.Operation)
            .Distinct()
            .Order()
            .ToArray();
    }
}

internal sealed class MongoDbPhysicalMutationSchemaBinding
{
    public const string DefinitionKind = "mongodb.bounded-mutation-binding.v1";

    private MongoDbPhysicalMutationSchemaBinding(
        string providerName,
        ExecutableStorageRoute route,
        string mutationIdentity,
        string handlerIdentity,
        string fenceField,
        BoundedMutationActionKind actionKind,
        string? transitionPath,
        IReadOnlyList<string> transitionSources,
        string? transitionTarget,
        MongoDbPhysicalMutationSelector primary,
        MongoDbPhysicalMutationSelector? linked,
        string? canonicalJson = null)
    {
        ProviderName = providerName;
        Route = route;
        MutationIdentity = mutationIdentity;
        HandlerIdentity = handlerIdentity;
        FenceField = fenceField;
        ActionKind = actionKind;
        TransitionPath = transitionPath;
        TransitionSources = Array.AsReadOnly(transitionSources.Order(StringComparer.Ordinal).ToArray());
        TransitionTarget = transitionTarget;
        Primary = primary;
        Linked = linked;
        CanonicalJson = canonicalJson ?? Serialize(this);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The provider implementation owns this structural binding. Its version deliberately stays
    /// in <see cref="PhysicalMutationPlan"/> execution certification rather than the durable
    /// schema definition, because a provider upgrade does not rewrite an identical physical index.
    /// </summary>
    public string ProviderName { get; }

    public ExecutableStorageRoute Route { get; }

    public string MutationIdentity { get; }

    public string HandlerIdentity { get; }

    public string FenceField { get; }

    public BoundedMutationActionKind ActionKind { get; }

    public string? TransitionPath { get; }

    public IReadOnlyList<string> TransitionSources { get; }

    public string? TransitionTarget { get; }

    public MongoDbPhysicalMutationSelector Primary { get; }

    public MongoDbPhysicalMutationSelector? Linked { get; }

    public string CanonicalJson { get; }

    public string Fingerprint { get; }

    public static MongoDbPhysicalMutationSchemaBinding Create(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalMutationPlan plan)
    {
        var query = storage.BoundedQueries.Single(candidate =>
            candidate.Identity == plan.Predicate.QueryIdentity);
        var logicalIndex = storage.LogicalIndexes.Single(candidate =>
            candidate.Identity == query.IndexIdentity);
        var primary = MongoDbPhysicalMutationSelector.Create(
            route,
            logicalIndex,
            plan.Predicate,
            ExecutableStorageObjectRole.PrimaryStorage);
        var linked = route.LinkedIndexStorage is null
            ? null
            : MongoDbPhysicalMutationSelector.Create(
                route,
                logicalIndex,
                plan.Predicate,
                ExecutableStorageObjectRole.LinkedIndexStorage);
        var transition = plan.Action as PhysicalTransitionMutationAction;
        return new MongoDbPhysicalMutationSchemaBinding(
            plan.Predicate.Provider.Name,
            route,
            plan.MutationIdentity,
            plan.HandlerIdentity,
            MongoDbPhysicalMutationStorage.BindingFenceField(route.StorageUnit, plan.MutationIdentity),
            plan.Action.Kind,
            transition?.Path,
            transition?.AllowedSourceValues ?? [],
            transition?.TargetValue,
            primary,
            linked);
    }

    public static MongoDbPhysicalMutationSchemaBinding Deserialize(string canonicalJson)
    {
        var root = BsonDocument.Parse(canonicalJson);
        if (root.GetValue("schemaVersion").AsString != "1")
            throw new InvalidOperationException("Unsupported MongoDB bounded-mutation binding schema version.");
        var route = ExecutableStorageRouteSerializer.Deserialize(root["canonicalRoute"].AsString);
        var action = root["action"].AsBsonDocument;
        var result = new MongoDbPhysicalMutationSchemaBinding(
            root["providerName"].AsString,
            route,
            root["mutationIdentity"].AsString,
            root["handlerIdentity"].AsString,
            root["fenceField"].AsString,
            Enum.Parse<BoundedMutationActionKind>(action["kind"].AsString),
            action.GetValue("path", BsonNull.Value).IsBsonNull ? null : action["path"].AsString,
            action["sources"].AsBsonArray.Select(value => value.AsString).ToArray(),
            action.GetValue("target", BsonNull.Value).IsBsonNull ? null : action["target"].AsString,
            MongoDbPhysicalMutationSelector.Deserialize(root["primary"].AsBsonDocument),
            root.GetValue("linked", BsonNull.Value).IsBsonNull
                ? null
                : MongoDbPhysicalMutationSelector.Deserialize(root["linked"].AsBsonDocument),
            canonicalJson);
        if (route.Fingerprint != root["routeFingerprint"].AsString ||
            Serialize(result) != canonicalJson)
        {
            throw new InvalidOperationException("MongoDB bounded-mutation binding is not canonical or has inconsistent route evidence.");
        }
        return result;
    }

    private static string Serialize(MongoDbPhysicalMutationSchemaBinding binding)
    {
        var action = new BsonDocument
        {
            ["kind"] = binding.ActionKind.ToString(),
            ["path"] = binding.TransitionPath is null ? BsonNull.Value : binding.TransitionPath,
            ["sources"] = new BsonArray(binding.TransitionSources),
            ["target"] = binding.TransitionTarget is null ? BsonNull.Value : binding.TransitionTarget
        };
        var document = new BsonDocument
        {
            ["schemaVersion"] = "1",
            ["providerName"] = binding.ProviderName,
            ["storageUnit"] = binding.Route.StorageUnit.Value,
            ["mutationIdentity"] = binding.MutationIdentity,
            ["handlerIdentity"] = binding.HandlerIdentity,
            ["fenceField"] = binding.FenceField,
            ["routeFingerprint"] = binding.Route.Fingerprint,
            ["canonicalRoute"] = ExecutableStorageRouteSerializer.Serialize(binding.Route),
            ["action"] = action,
            ["primary"] = binding.Primary.Serialize(),
            ["linked"] = binding.Linked is null ? BsonNull.Value : binding.Linked.Serialize()
        };
        return document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
    }
}

internal sealed class MongoDbPhysicalMutationSelector
{
    private MongoDbPhysicalMutationSelector(
        string logicalIndexIdentity,
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName storageObject,
        ProviderPhysicalObjectName index,
        string discriminatorField,
        string discriminatorValue,
        string scopeField,
        MissingValueBehavior missingValueBehavior,
        IReadOnlyList<MongoDbPhysicalMutationMirrorField> fields)
    {
        LogicalIndexIdentity = logicalIndexIdentity;
        Target = target;
        StorageObject = storageObject;
        Index = index;
        DiscriminatorField = discriminatorField;
        DiscriminatorValue = discriminatorValue;
        ScopeField = scopeField;
        MissingValueBehavior = missingValueBehavior;
        Fields = Array.AsReadOnly(fields.ToArray());
        FieldByPath = Fields.ToFrozenDictionary(field => field.Path, StringComparer.Ordinal);
    }

    public string LogicalIndexIdentity { get; }

    public ExecutableStorageObjectRole Target { get; }

    public ProviderPhysicalObjectName StorageObject { get; }

    public ProviderPhysicalObjectName Index { get; }

    public string DiscriminatorField { get; }

    public string DiscriminatorValue { get; }

    public string ScopeField { get; }

    public MissingValueBehavior MissingValueBehavior { get; }

    public IReadOnlyList<MongoDbPhysicalMutationMirrorField> Fields { get; }

    public IReadOnlyDictionary<string, MongoDbPhysicalMutationMirrorField> FieldByPath { get; }

    public BsonDocument IndexKeys
    {
        get
        {
            var keys = new BsonDocument
            {
                [DiscriminatorField] = 1,
                [ScopeField] = 1
            };
            foreach (var mirror in Fields)
                keys[mirror.Identifier] = 1;
            return keys;
        }
    }

    public PhysicalMutationSelectorCertification ToCertification() =>
        new(
            Target,
            StorageObject,
            Index,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["documentKind"] = DiscriminatorField,
                ["storageScope"] = ScopeField
            }.Concat(Fields.Select(field => new KeyValuePair<string, string>(field.Path, field.Identifier)))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    public static MongoDbPhysicalMutationSelector Create(
        ExecutableStorageRoute route,
        LogicalIndexDeclaration logicalIndex,
        PhysicalQueryPlan query,
        ExecutableStorageObjectRole target)
    {
        var storageObject = target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.PrimaryStorage.Name
            : route.LinkedIndexStorage!.Name;
        var indexName = MongoDbPhysicalMutationStorage.IndexName(route, query.LogicalIndexIdentity, target);
        var index = new ProviderPhysicalObjectName(
            PhysicalObjectKind.PhysicalIndex,
            indexName,
            indexName,
            indexName,
            $"mongodb:{storageObject.Identifier}:indexes",
            route.StorageUnit);
        var fields = logicalIndex.Fields.SelectMany(field =>
            field.Path == PhysicalDocumentFieldPaths.Id
                ? IdentityFields(route, query, target)
                :
                [
                    new MongoDbPhysicalMutationMirrorField(
                        field.Path,
                        target == ExecutableStorageObjectRole.PrimaryStorage
                            ? MongoDbPhysicalMutationStorage.PrimaryField(route, field.Path)
                            : MongoDbPhysicalMutationStorage.LinkedField(route, field.Path),
                        logicalIndex.GetValueKind(field))
                ]).ToArray();
        return new MongoDbPhysicalMutationSelector(
            logicalIndex.Identity,
            target,
            storageObject,
            index,
            target == ExecutableStorageObjectRole.PrimaryStorage
                ? route.Envelope.DocumentKind.Identifier
                : route.LinkedRelationship!.DocumentKind.Identifier,
            route.Discriminator.Value,
            target == ExecutableStorageObjectRole.PrimaryStorage
                ? route.Envelope.StorageScope.Identifier
                : route.LinkedRelationship!.StorageScope.Identifier,
            logicalIndex.MissingValueBehavior,
            fields);
    }

    private static IReadOnlyList<MongoDbPhysicalMutationMirrorField> IdentityFields(
        ExecutableStorageRoute route,
        PhysicalQueryPlan query,
        ExecutableStorageObjectRole target)
    {
        var identity = target == ExecutableStorageObjectRole.PrimaryStorage
            ? route.Envelope.Identity
            : route.LinkedRelationship!.Identity;
        var operations = query.Predicates.SingleOrDefault(predicate =>
            predicate.Path == PhysicalDocumentFieldPaths.Id)?.Operations;
        var fields = new List<MongoDbPhysicalMutationMirrorField>();
        if (operations?.Any(operation => operation is
                PortableQueryOperation.Equal or
                PortableQueryOperation.In or
                PortableQueryOperation.NotEqual) == true)
        {
            fields.Add(new MongoDbPhysicalMutationMirrorField(
                PhysicalDocumentIdentityFieldPaths.Lookup,
                identity.LookupKey.Identifier,
                IndexValueKind.Keyword));
        }
        fields.Add(new MongoDbPhysicalMutationMirrorField(
            PhysicalDocumentIdentityFieldPaths.Comparison,
            identity.ComparisonKey.Identifier,
            IndexValueKind.Keyword));
        return fields;
    }

    public BsonDocument Serialize() => new()
    {
        ["logicalIndexIdentity"] = LogicalIndexIdentity,
        ["target"] = Target.ToString(),
        ["storageObject"] = SerializeName(StorageObject),
        ["index"] = SerializeName(Index),
        ["discriminatorField"] = DiscriminatorField,
        ["discriminatorValue"] = DiscriminatorValue,
        ["scopeField"] = ScopeField,
        ["missingValueBehavior"] = MissingValueBehavior.ToString(),
        ["fields"] = new BsonArray(Fields.Select(field => new BsonDocument
        {
            ["path"] = field.Path,
            ["identifier"] = field.Identifier,
            ["valueKind"] = field.ValueKind.ToString()
        }))
    };

    public static MongoDbPhysicalMutationSelector Deserialize(BsonDocument document) => new(
        document["logicalIndexIdentity"].AsString,
        Enum.Parse<ExecutableStorageObjectRole>(document["target"].AsString),
        DeserializeName(document["storageObject"].AsBsonDocument),
        DeserializeName(document["index"].AsBsonDocument),
        document["discriminatorField"].AsString,
        document["discriminatorValue"].AsString,
        document["scopeField"].AsString,
        Enum.Parse<MissingValueBehavior>(document["missingValueBehavior"].AsString),
        document["fields"].AsBsonArray.Select(value =>
        {
            var field = value.AsBsonDocument;
            return new MongoDbPhysicalMutationMirrorField(
                field["path"].AsString,
                field["identifier"].AsString,
                Enum.Parse<IndexValueKind>(field["valueKind"].AsString));
        }).ToArray());

    private static BsonDocument SerializeName(ProviderPhysicalObjectName name) => new()
    {
        ["objectKind"] = name.ObjectKind.ToString(),
        ["featureDefaultLogicalName"] = name.FeatureDefaultLogicalName,
        ["logicalName"] = name.LogicalName,
        ["identifier"] = name.Identifier,
        ["collisionScope"] = name.CollisionScope,
        ["namingOwner"] = name.NamingOwner.Value
    };

    private static ProviderPhysicalObjectName DeserializeName(BsonDocument name) => new(
        Enum.Parse<PhysicalObjectKind>(name["objectKind"].AsString),
        name["featureDefaultLogicalName"].AsString,
        name["logicalName"].AsString,
        name["identifier"].AsString,
        name["collisionScope"].AsString,
        new StorageUnitIdentity(name["namingOwner"].AsString));
}

internal sealed record MongoDbPhysicalMutationMirrorField(
    string Path,
    string Identifier,
    IndexValueKind ValueKind);
