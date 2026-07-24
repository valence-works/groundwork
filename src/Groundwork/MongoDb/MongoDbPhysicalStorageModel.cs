using System.Collections.Frozen;
using System.Text;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Physicalization;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.MongoDb.Documents;

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
        IReadOnlyList<ExecutableStorageRoute> routes,
        IReadOnlyList<MongoDbPhysicalMutationBinding> mutationBindings)
    {
        Manifest = manifest;
        Provider = provider;
        Routes = Array.AsReadOnly(routes.OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal).ToArray());
        RoutesByStorageUnit = Routes.ToFrozenDictionary(route => route.StorageUnit.Value, StringComparer.Ordinal);
        StorageByStorageUnit = manifest.StorageUnits.ToFrozenDictionary(unit => unit.Identity.Value, unit => unit.PhysicalStorage!, StringComparer.Ordinal);
        MutationBindingsByStorageUnit = mutationBindings
            .GroupBy(binding => binding.Schema.Route.StorageUnit.Value, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<MongoDbPhysicalMutationBinding>)Array.AsReadOnly(group
                    .OrderBy(binding => binding.Plan.MutationIdentity, StringComparer.Ordinal)
                    .ToArray()),
                StringComparer.Ordinal);
        Target = new PhysicalSchemaTarget(
            manifest.Identity,
            manifest.Version,
            provider,
            Routes,
            mutationBindings.Select(binding => binding.ProviderDefinition)
                .Concat(MongoDbPhysicalMutationSelectorSchemaDefinition
                    .Compile(mutationBindings)
                    .Select(definition => definition.ProviderDefinition))
                .ToArray());
    }

    public StorageManifest Manifest { get; }

    public ProviderIdentity Provider { get; }

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    public IReadOnlyDictionary<string, ExecutableStorageRoute> RoutesByStorageUnit { get; }

    public IReadOnlyDictionary<string, StorageUnitPhysicalStorage> StorageByStorageUnit { get; }

    internal IReadOnlyDictionary<string, IReadOnlyList<MongoDbPhysicalMutationBinding>> MutationBindingsByStorageUnit { get; }

    public PhysicalSchemaTarget Target { get; }

    public static MongoDbPhysicalStorageModel Compile(
        StorageManifest manifest,
        ProviderIdentity? provider = null,
        IPhysicalNamePolicy? namePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        provider ??= MongoDbGroundworkCapabilities.Provider;
        namePolicy ??= PhysicalNamePolicy.Identity;
        new StorageManifestValidator().Validate(manifest).RequireValid();

        var resolution = PhysicalStorageResolver.Resolve(manifest, namePolicy, MongoDbPhysicalNameNormalizer.Instance);
        if (!resolution.IsValid)
            throw Invalid("physical storage", resolution.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        if (!compilation.IsValid)
            throw Invalid("executable routes", compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

        ValidateMutationBindingReservation(manifest, compilation.Routes);
        ValidateReservedFields(compilation.Routes);
        ValidateReservedCollections(compilation.Routes);
        foreach (var projection in compilation.Routes.SelectMany(route => route.ProjectedColumns))
            MongoDbPhysicalProjectionValues.ValidateDefault(projection);

        var bindings = compilation.Routes.SelectMany(route =>
            MongoDbPhysicalMutationBinding.Compile(
                route,
                manifest.StorageUnits.Single(unit => unit.Identity == route.StorageUnit).PhysicalStorage!,
                provider)).ToArray();
        return new MongoDbPhysicalStorageModel(manifest, provider, compilation.Routes, bindings);
    }

    private static InvalidOperationException Invalid(string phase, IEnumerable<string> diagnostics) =>
        new($"MongoDB {phase} compilation failed:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");

    private static void ValidateMutationBindingReservation(
        StorageManifest manifest,
        IReadOnlyList<ExecutableStorageRoute> routes)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            MongoDbPhysicalMutationStorage.Root,
            MongoDbPhysicalMutationStorage.BindingRoot
        };
        var names = new List<(string Surface, string Value)>();
        foreach (var unit in manifest.StorageUnits.Where(unit => unit.PhysicalStorage is not null))
        {
            var storage = unit.PhysicalStorage!;
            names.AddRange(storage.LogicalIndexes.SelectMany(index =>
                new[] { ("logical index", index.Identity) }
                    .Concat(index.Fields.Select(field => ("logical index path", field.Path)))));
            names.AddRange(storage.BoundedQueries.SelectMany(query =>
                new[]
                {
                    ("bounded query", query.Identity),
                    ("bounded query index", query.IndexIdentity)
                }.Concat(query.PredicateFields.Select(field => ("bounded query path", field.Path)))));
            names.AddRange(storage.BoundedMutations.SelectMany(mutation =>
                new[]
                {
                    ("bounded mutation", mutation.Identity),
                    ("bounded mutation query", mutation.PredicateQueryIdentity)
                }.Concat(mutation.Action is BoundedTransitionMutationAction transition
                    ? [("bounded mutation transition path", transition.Path)]
                    : [])));
            names.AddRange(storage.NameOverrides.SelectMany(nameOverride => new[]
            {
                ("physical name override source", nameOverride.FeatureDefaultLogicalName),
                ("physical name override target", nameOverride.LogicalName)
            }));
        }

        foreach (var route in routes)
        {
            AddName(names, "primary collection", route.PrimaryStorage.Name);
            if (route.LinkedIndexStorage is not null)
                AddName(names, "linked collection", route.LinkedIndexStorage.Name);
            AddColumn(names, "envelope field", route.Envelope.Id);
            AddColumn(names, "envelope field", route.Envelope.DocumentKind);
            AddColumn(names, "envelope field", route.Envelope.StorageScope);
            AddColumn(names, "envelope field", route.Envelope.Version);
            AddColumn(names, "envelope field", route.Envelope.SchemaVersion);
            AddColumn(names, "envelope field", route.Envelope.CanonicalJson);
            if (route.LinkedRelationship is not null)
            {
                AddColumn(names, "linked field", route.LinkedRelationship.DocumentId);
                AddColumn(names, "linked field", route.LinkedRelationship.DocumentKind);
                AddColumn(names, "linked field", route.LinkedRelationship.StorageScope);
            }
            foreach (var projection in route.ProjectedColumns)
            {
                names.Add(("projected field logical name", projection.Definition.LogicalName));
                names.Add(("projected field path", projection.Definition.Path));
                AddColumn(names, "projected field", projection.Column);
                AddName(names, "projected provider field", projection.Name);
            }
            foreach (var index in route.Indexes)
            {
                names.Add(("physical index logical name", index.Definition.LogicalName));
                AddName(names, "physical index", index.Name);
                foreach (var column in index.Columns)
                    AddColumn(names, "physical index field", column.Column);
            }
        }

        var collision = names.FirstOrDefault(item => item.Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(reserved.Contains));
        if (collision != default)
        {
            throw Invalid(
                "reserved mutation binding name",
                [$"{collision.Surface} '{collision.Value}' collides with MongoDB provider-owned mutation binding storage."]);
        }
    }

    private static void AddName(
        ICollection<(string Surface, string Value)> names,
        string surface,
        ProviderPhysicalObjectName name)
    {
        names.Add(($"{surface} feature-default name", name.FeatureDefaultLogicalName));
        names.Add(($"{surface} logical name", name.LogicalName));
        names.Add(($"{surface} provider identifier", name.Identifier));
    }

    private static void AddColumn(
        ICollection<(string Surface, string Value)> names,
        string surface,
        ExecutableColumnRoute column)
    {
        names.Add(($"{surface} logical name", column.LogicalName));
        names.Add(($"{surface} provider identifier", column.Identifier));
    }

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
        var isCollection = context.ObjectKind is PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage or PhysicalObjectKind.CollectionElementStorage or PhysicalObjectKind.SchemaHistory;
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
        PhysicalObjectKind.PrimaryStorage or PhysicalObjectKind.LinkedIndexStorage or PhysicalObjectKind.CollectionElementStorage => "mongodb:collections",
        PhysicalObjectKind.SchemaHistory => "mongodb:collections",
        PhysicalObjectKind.PhysicalIndex => $"mongodb:{context.StorageUnit.Value}:indexes",
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField => $"mongodb:{context.StorageUnit.Value}:primary-fields",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField => $"mongodb:{context.StorageUnit.Value}:linked-fields",
        PhysicalObjectKind.CollectionElementField => $"mongodb:{context.StorageUnit.Value}:collection-element-fields",
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
        Documents.MongoDbPhysicalMutationStorage.BindingRoot,
        CreatedAt,
        UpdatedAt,
        Incarnation
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> LinkedReserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Id,
        Documents.MongoDbPhysicalMutationStorage.Root,
        Documents.MongoDbPhysicalMutationStorage.BindingRoot,
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
