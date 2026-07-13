using System.Collections.Frozen;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.MongoDb;

/// <summary>
/// Immutable MongoDB execution model compiled from provider-neutral physical storage declarations.
/// MongoDB runtime code consumes this model and never re-derives names or physical forms.
/// </summary>
public sealed class MongoDbPhysicalStorageModel
{
    private MongoDbPhysicalStorageModel(
        StorageManifest manifest,
        ProviderIdentity provider,
        IReadOnlyList<ExecutableStorageRoute> routes)
    {
        Manifest = manifest;
        Provider = provider;
        Routes = Array.AsReadOnly(routes.OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal).ToArray());
        RoutesByStorageUnit = Routes.ToFrozenDictionary(route => route.StorageUnit.Value, StringComparer.Ordinal);
        StorageByStorageUnit = manifest.StorageUnits.ToFrozenDictionary(unit => unit.Identity.Value, unit => unit.PhysicalStorage!, StringComparer.Ordinal);
        Target = new PhysicalSchemaTarget(manifest.Identity, manifest.Version, provider, Routes);
    }

    public StorageManifest Manifest { get; }

    public ProviderIdentity Provider { get; }

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    public IReadOnlyDictionary<string, ExecutableStorageRoute> RoutesByStorageUnit { get; }

    public IReadOnlyDictionary<string, StorageUnitPhysicalStorage> StorageByStorageUnit { get; }

    public PhysicalSchemaTarget Target { get; }

    public static MongoDbPhysicalStorageModel Compile(
        StorageManifest manifest,
        ProviderIdentity? provider = null,
        IPhysicalNamePolicy? namePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        provider ??= MongoDbGroundworkCapabilities.Provider;
        namePolicy ??= PhysicalNamePolicy.Identity;

        var resolution = PhysicalStorageResolver.Resolve(manifest, namePolicy, MongoDbPhysicalNameNormalizer.Instance);
        if (!resolution.IsValid)
            throw Invalid("physical storage", resolution.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!compilation.IsValid)
            throw Invalid("executable routes", compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

        ValidateReservedFields(compilation.Routes);
        ValidateReservedCollections(compilation.Routes);
        foreach (var projection in compilation.Routes.SelectMany(route => route.ProjectedColumns))
            MongoDbPhysicalProjectionValues.ValidateDefault(projection);

        return new MongoDbPhysicalStorageModel(manifest, provider, compilation.Routes);
    }

    private static InvalidOperationException Invalid(string phase, IEnumerable<string> diagnostics) =>
        new($"MongoDB {phase} compilation failed:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");

    private static void ValidateReservedFields(IReadOnlyList<ExecutableStorageRoute> routes)
    {
        foreach (var route in routes)
        {
            var primary = new[]
                {
                    route.Envelope.Id,
                    route.Envelope.DocumentKind,
                    route.Envelope.StorageScope,
                    route.Envelope.Version,
                    route.Envelope.SchemaVersion,
                    route.Envelope.CanonicalJson
                }
                .Concat(route.ProjectedColumns
                    .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage)
                    .Select(column => column.Column));
            RejectReserved(route, "primary", primary, MongoDbPhysicalStorageFields.PrimaryReserved);

            if (route.LinkedRelationship is null)
                continue;
            var linked = new[]
                {
                    route.LinkedRelationship.DocumentId,
                    route.LinkedRelationship.DocumentKind,
                    route.LinkedRelationship.StorageScope
                }
                .Concat(route.ProjectedColumns
                    .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
                    .Select(column => column.Column));
            RejectReserved(route, "linked", linked, MongoDbPhysicalStorageFields.LinkedReserved);
        }
    }

    private static void ValidateReservedCollections(IReadOnlyList<ExecutableStorageRoute> routes)
    {
        foreach (var route in routes)
        {
            var collision = new[] { route.PrimaryStorage.Name }
                .Concat(route.LinkedIndexStorage is null
                    ? Enumerable.Empty<ProviderPhysicalObjectName>()
                    : [route.LinkedIndexStorage.Name])
                .FirstOrDefault(name => MongoDbPhysicalStorageFields.ProviderOwnedCollections.Contains(name.Identifier));
            if (collision is not null)
            {
                throw Invalid(
                    "reserved collection",
                    [$"Storage unit '{route.StorageUnit.Value}' collection '{collision.Identifier}' collides with MongoDB provider-owned infrastructure."]);
            }
        }
    }

    private static void RejectReserved(
        ExecutableStorageRoute route,
        string target,
        IEnumerable<ExecutableColumnRoute> fields,
        IReadOnlySet<string> reserved)
    {
        var collision = fields.FirstOrDefault(field => reserved.Contains(field.Identifier));
        if (collision is not null)
        {
            throw Invalid(
                "reserved field",
                [$"Storage unit '{route.StorageUnit.Value}' {target} field '{collision.Identifier}' collides with MongoDB provider-owned storage."]);
        }
    }
}

/// <summary>MongoDB identifier normalization used before executable-route fingerprinting.</summary>
public sealed class MongoDbPhysicalNameNormalizer : IProviderPhysicalNameNormalizer
{
    public static MongoDbPhysicalNameNormalizer Instance { get; } = new();

    private MongoDbPhysicalNameNormalizer()
    {
    }

    public string Normalize(ProviderPhysicalNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.LogicalName;
        var maximum = context.ObjectKind == PhysicalObjectKind.PhysicalIndex ? 120 : 120;
        var isCollection = context.ObjectKind is PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage or PhysicalObjectKind.SchemaHistory;
        var invalid = string.IsNullOrWhiteSpace(name) ||
                      name.IndexOf('\0') >= 0 ||
                      (!isCollection && (name.Contains('.', StringComparison.Ordinal) || name.StartsWith('$'))) ||
                      (isCollection && (name.Contains('$', StringComparison.Ordinal) || name.StartsWith("system.", StringComparison.Ordinal)));
        return invalid || Encoding.UTF8.GetByteCount(name) > maximum
            ? EncodeWithinUtf8Budget(name, maximum)
            : name;
    }

    private static string EncodeWithinUtf8Budget(string name, int maximumBytes)
    {
        var encoded = PhysicalizationNameEncoder.Encode(name);
        if (Encoding.UTF8.GetByteCount(encoded) <= maximumBytes)
            return encoded;

        const int suffixLength = 13; // '_' plus the stable 12-character hash.
        var suffix = encoded[^suffixLength..];
        var prefixBudget = maximumBytes - Encoding.UTF8.GetByteCount(suffix);
        var prefix = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in encoded[..^suffixLength].EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > prefixBudget)
                break;
            prefix.Append(rune);
            usedBytes += rune.Utf8SequenceLength;
        }

        var readable = prefix.ToString().TrimEnd('_');
        return $"{readable}{suffix}";
    }

    public string GetCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage => "mongodb:collections",
        PhysicalObjectKind.SchemaHistory => "mongodb:collections",
        PhysicalObjectKind.PhysicalIndex => $"mongodb:{context.StorageUnit.Value}:indexes",
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField => $"mongodb:{context.StorageUnit.Value}:primary-fields",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField => $"mongodb:{context.StorageUnit.Value}:linked-fields",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context.ObjectKind, null)
    };
}


internal static class MongoDbPhysicalStorageFields
{
    public const string Id = "_id";
    public const string NativeContent = "_groundwork_content";
    public const string CreatedAt = "_groundwork_created_at";
    public const string UpdatedAt = "_groundwork_updated_at";
    public const string Incarnation = "_groundwork_incarnation";
    public const string LinkedPrimaryVersion = "_groundwork_primary_version";
    public const string BoundedMutationOperationsCollection = "groundwork_bounded_mutation_operations";

    public static IReadOnlySet<string> PrimaryReserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Id,
        NativeContent,
        Documents.MongoDbPhysicalMutationStorage.Root,
        CreatedAt,
        UpdatedAt,
        Incarnation
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> LinkedReserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Id,
        Documents.MongoDbPhysicalMutationStorage.Root,
        LinkedPrimaryVersion,
        Incarnation
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> ProviderOwnedCollections { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "groundwork_schema_history",
        "groundwork_physical_schema_state",
        "groundwork_physical_schema_operations",
        "groundwork_physical_schema_locks",
        BoundedMutationOperationsCollection,
        "groundwork_diagnostic_records",
        "groundwork_diagnostic_streams",
        "groundwork_diagnostic_append_operations",
        "groundwork_diagnostic_append_outcomes",
        "groundwork_diagnostic_trim_operations",
        "groundwork_diagnostic_provider_state",
        "groundwork_diagnostic_stream_definitions"
    }.ToFrozenSet(StringComparer.Ordinal);
}
