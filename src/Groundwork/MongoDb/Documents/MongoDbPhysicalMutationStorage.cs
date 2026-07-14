using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using MongoDB.Bson;

namespace Groundwork.MongoDb.Documents;

/// <summary>Provider-owned, route-scoped predicate mirrors and indexes for set-based mutations.</summary>
internal static class MongoDbPhysicalMutationStorage
{
    public const string Root = "_groundwork_mutation";
    public const string BindingRoot = "_groundwork_mutation_bindings";

    public static string Field(StorageUnitIdentity storageUnit, string path) =>
        $"{Root}.{Segment(storageUnit, path)}";

    public static string BindingFenceField(StorageUnitIdentity storageUnit, string mutationIdentity) =>
        $"{BindingRoot}.{Segment(storageUnit, mutationIdentity)}";

    public static string IndexName(
        ExecutableStorageRoute route,
        string logicalIndexIdentity,
        ExecutableStorageObjectRole target) =>
        $"groundwork_mutation_{(target == ExecutableStorageObjectRole.PrimaryStorage ? "p" : "l")}_" +
        Encoded($"{route.StorageUnit.Value}\u001f{logicalIndexIdentity}\u001f{target}");

    public static string IndexName(
        ExecutableStorageRoute route,
        BoundedQueryDeclaration query,
        ExecutableStorageObjectRole target) =>
        IndexName(route, query.IndexIdentity, target);

    public static IReadOnlyList<string> IndexPaths(
        StorageUnitPhysicalStorage storage,
        BoundedQueryDeclaration query) =>
        storage.LogicalIndexes.Single(candidate => candidate.Identity == query.IndexIdentity)
            .Fields.Select(field => field.Path)
            .ToArray();

    public static IReadOnlyList<BoundedQueryDeclaration> MutationQueries(StorageUnitPhysicalStorage storage) =>
        storage.BoundedMutations
            .Select(mutation => storage.BoundedQueries.Single(query =>
                query.Identity == mutation.PredicateQueryIdentity))
            .DistinctBy(query => query.Identity, StringComparer.Ordinal)
            .ToArray();

    public static string PrimaryField(ExecutableStorageRoute route, string path) => path switch
    {
        "id" => route.Envelope.Id.Identifier,
        "documentKind" => route.Envelope.DocumentKind.Identifier,
        "storageScope" => route.Envelope.StorageScope.Identifier,
        "version" => route.Envelope.Version.Identifier,
        "schemaVersion" => route.Envelope.SchemaVersion.Identifier,
        _ => Field(route.StorageUnit, path)
    };

    public static string LinkedField(ExecutableStorageRoute route, string path) => path switch
    {
        "id" => route.LinkedRelationship!.DocumentId.Identifier,
        "documentKind" => route.LinkedRelationship!.DocumentKind.Identifier,
        "storageScope" => route.LinkedRelationship!.StorageScope.Identifier,
        "version" => MongoDbPhysicalStorageFields.LinkedPrimaryVersion,
        _ => Field(route.StorageUnit, path)
    };

    public static BsonValue QueryValue(
        ExecutableStorageRoute route,
        string path,
        IndexValueKind valueKind,
        string? value)
    {
        var projection = route.ProjectedColumns.FirstOrDefault(candidate =>
            candidate.Definition.Path == path);
        return projection is null
            ? value is null
                ? BsonNull.Value
                : valueKind switch
                {
                    IndexValueKind.Boolean => new BsonBoolean(bool.Parse(value)),
                    IndexValueKind.Number => new BsonDecimal128(Decimal128.Parse(value)),
                    _ => new BsonString(value)
                }
            : MongoDbPhysicalProjectionValues.ParseQueryValue(projection, value);
    }

    public static void ApplyMirrors(
        BsonDocument target,
        BsonDocument primary,
        BsonDocument content,
        ExecutableStorageRoute route,
        IReadOnlyList<MongoDbPhysicalMutationBinding> bindings,
        ExecutableStorageObjectRole targetRole,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        var mirrors = new BsonDocument();
        foreach (var mirror in bindings
                     .Select(binding => targetRole == ExecutableStorageObjectRole.PrimaryStorage
                         ? binding.Schema.Primary
                         : binding.Schema.Linked)
                     .Where(selector => selector is not null)
                     .SelectMany(selector => selector!.Fields)
                     .Where(field => field.Identifier.StartsWith($"{Root}.", StringComparison.Ordinal))
                     .DistinctBy(field => field.Identifier, StringComparer.Ordinal))
        {
            var value = ResolveMirror(primary, content, route, mirror, projectedValues);
            if (value.IsPresent)
                SetDotted(mirrors, mirror.Identifier[(Root.Length + 1)..], value.Value);
        }
        target[Root] = mirrors;
        target[BindingRoot] = new BsonDocument(bindings.Select(binding =>
            new BsonElement(
                binding.Schema.FenceField[(BindingRoot.Length + 1)..],
                binding.Schema.Fingerprint)));
    }

    private static void SetDotted(BsonDocument document, string path, BsonValue value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = document;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!current.TryGetValue(segments[index], out var child) || !child.IsBsonDocument)
            {
                child = new BsonDocument();
                current[segments[index]] = child;
            }
            current = child.AsBsonDocument;
        }
        current[segments[^1]] = value;
    }

    public static MongoDbPhysicalProjectionValue ResolveMirror(
        BsonDocument primary,
        BsonDocument content,
        ExecutableStorageRoute route,
        MongoDbPhysicalMutationMirrorField mirror,
        IReadOnlyDictionary<ExecutableProjectedColumnRoute, MongoDbPhysicalProjectionValue> projectedValues)
    {
        if (TryReadEnvelope(primary, route, mirror.Path, out var envelope))
            return new MongoDbPhysicalProjectionValue(true, envelope);
        var projection = route.ProjectedColumns.FirstOrDefault(candidate =>
            candidate.Definition.Path == mirror.Path);
        if (projection is not null)
            return projectedValues[projection];
        return TryRead(content, mirror.Path, out var native)
            ? new MongoDbPhysicalProjectionValue(true, native)
            : MongoDbPhysicalProjectionValue.Omitted;
    }

    private static bool TryReadEnvelope(
        BsonDocument primary,
        ExecutableStorageRoute route,
        string path,
        out BsonValue value)
    {
        var identifier = path switch
        {
            "id" => route.Envelope.Id.Identifier,
            "documentKind" => route.Envelope.DocumentKind.Identifier,
            "storageScope" => route.Envelope.StorageScope.Identifier,
            "version" => route.Envelope.Version.Identifier,
            "schemaVersion" => route.Envelope.SchemaVersion.Identifier,
            _ => null
        };
        if (identifier is not null && primary.TryGetValue(identifier, out value))
            return true;
        value = BsonNull.Value;
        return false;
    }

    private static bool TryRead(BsonDocument document, string path, out BsonValue value)
    {
        value = document;
        foreach (var segment in path.Split(
                     '.',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!value.IsBsonDocument || !value.AsBsonDocument.TryGetValue(segment, out value))
                return false;
        }
        return true;
    }

    private static string Encoded(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    private static string Segment(StorageUnitIdentity storageUnit, string path) =>
        Encoded($"{storageUnit.Value}\u001f{path}");
}
