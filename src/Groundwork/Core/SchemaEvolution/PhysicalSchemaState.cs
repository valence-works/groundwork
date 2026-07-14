using System.Text;
using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

public sealed class PhysicalSchemaTarget
{
    public PhysicalSchemaTarget(
        StorageManifestIdentity manifestIdentity,
        StorageManifestVersion manifestVersion,
        ProviderIdentity provider,
        IReadOnlyList<ExecutableStorageRoute> routes,
        IReadOnlyList<ProviderPhysicalSchemaDefinition>? providerDefinitions = null)
    {
        ManifestIdentity = manifestIdentity;
        ManifestVersion = manifestVersion;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Routes = Array.AsReadOnly(routes?
            .OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal)
            .ToArray() ?? throw new ArgumentNullException(nameof(routes)));

        var duplicate = Routes
            .GroupBy(route => route.StorageUnit.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Storage unit '{duplicate.Key}' has more than one executable route.", nameof(routes));

        ProviderDefinitions = Array.AsReadOnly(
            ProviderPhysicalSchemaDefinition.Canonicalize(providerDefinitions));
        if (ProviderDefinitions.Any(definition =>
                !string.Equals(definition.ProviderName, provider.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Every provider physical-schema definition must belong to provider '{provider.Name}'.",
                nameof(providerDefinitions));
        }
        if (ProviderDefinitions.Any(definition =>
                Routes.All(route => route.StorageUnit != definition.StorageUnit)))
        {
            throw new ArgumentException(
                "Every provider physical-schema definition must belong to an executable storage route.",
                nameof(providerDefinitions));
        }
        var duplicateProviderDefinition = ProviderDefinitions
            .GroupBy(definition => definition.Identity)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProviderDefinition is not null)
        {
            throw new ArgumentException(
                $"Provider physical-schema definition '{duplicateProviderDefinition.Key}' is declared more than once.",
                nameof(providerDefinitions));
        }

        Identity = new PhysicalSchemaTargetIdentity(manifestIdentity, provider.Name);
        Fingerprint = PhysicalSchemaFingerprint.Create(
            [
                manifestIdentity.Value,
                manifestVersion.Value,
                provider.Name,
                provider.Version,
                .. Routes.Select(route => route.Fingerprint),
                .. ProviderDefinitions.Select(definition => definition.Fingerprint)
            ]);
    }

    public PhysicalSchemaTargetIdentity Identity { get; }

    public StorageManifestIdentity ManifestIdentity { get; }

    public StorageManifestVersion ManifestVersion { get; }

    public ProviderIdentity Provider { get; }

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    public IReadOnlyList<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }

    public string Fingerprint { get; }
}

/// <summary>The provider-level exclusion scope. Manifest version is deliberately not part of the key.</summary>
public sealed record PhysicalSchemaTargetIdentity(
    StorageManifestIdentity ManifestIdentity,
    string ProviderName)
{
    public override string ToString() => $"{ProviderName}:{ManifestIdentity.Value}";
}

public sealed record PhysicalSchemaResolvedName(
    string Kind,
    string LogicalName,
    string Identifier,
    ExecutableStorageObjectRole Target);

public sealed class AppliedStorageRouteSnapshot
{
    internal AppliedStorageRouteSnapshot(
        StorageUnitIdentity storageUnit,
        string definitionFingerprint,
        string routeFingerprint,
        IReadOnlyList<PhysicalSchemaResolvedName> resolvedNames,
        string canonicalRouteJson)
    {
        StorageUnit = storageUnit;
        DefinitionFingerprint = definitionFingerprint;
        RouteFingerprint = routeFingerprint;
        ResolvedNames = Array.AsReadOnly(resolvedNames.ToArray());
        CanonicalRouteJson = canonicalRouteJson;
    }

    public StorageUnitIdentity StorageUnit { get; }

    public string DefinitionFingerprint { get; }

    public string RouteFingerprint { get; }

    public IReadOnlyList<PhysicalSchemaResolvedName> ResolvedNames { get; }

    public string CanonicalRouteJson { get; }
}

public sealed record AppliedSemanticOperationSnapshot(
    string Identity,
    string Fingerprint,
    PhysicalSchemaOperationKind Kind,
    StorageUnitIdentity? StorageUnit,
    string SubjectIdentity,
    string SlotIdentity,
    string CanonicalPayload);

public sealed class PhysicalSchemaAppliedSnapshot
{
    internal PhysicalSchemaAppliedSnapshot(
        IReadOnlyList<AppliedStorageRouteSnapshot> routes,
        IReadOnlyList<AppliedSemanticOperationSnapshot> semanticOperations,
        IReadOnlyList<ProviderPhysicalSchemaDefinition>? providerDefinitions = null)
    {
        Routes = Array.AsReadOnly(routes.OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal).ToArray());
        SemanticOperations = Array.AsReadOnly(semanticOperations.OrderBy(operation => operation.Identity, StringComparer.Ordinal).ToArray());
        ProviderDefinitions = Array.AsReadOnly(
            ProviderPhysicalSchemaDefinition.Canonicalize(providerDefinitions));
        foreach (var route in Routes)
        {
            ExecutableStorageRouteSerializer.ValidateCanonicalSnapshot(
                route.CanonicalRouteJson,
                route.DefinitionFingerprint,
                route.RouteFingerprint);
        }
        foreach (var operation in SemanticOperations)
            PhysicalSchemaOperationIntegrity.Validate(operation);
        ValidateProviderDefinitions();
        CanonicalJson = Serialize(this);
        Fingerprint = PhysicalSchemaFingerprint.CreateCanonical(CanonicalJson);
    }

    public IReadOnlyList<AppliedStorageRouteSnapshot> Routes { get; }

    public IReadOnlyList<AppliedSemanticOperationSnapshot> SemanticOperations { get; }

    public IReadOnlyList<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }

    /// <summary>Canonical provider-neutral snapshot persisted for restart comparison and inspection.</summary>
    public string CanonicalJson { get; }

    /// <summary>Integrity fingerprint of the complete canonical applied snapshot.</summary>
    public string Fingerprint { get; }

    private void ValidateProviderDefinitions()
    {
        if (ProviderDefinitions.Any(definition =>
                Routes.All(route => route.StorageUnit != definition.StorageUnit)))
        {
            throw new InvalidOperationException(
                "Applied provider physical-schema definitions must belong to an executable storage route.");
        }

        var expected = ProviderDefinitions
            .Select(definition => new ApplyProviderPhysicalSchemaDefinitionOperation(definition))
            .Select(operation => new AppliedSemanticOperationSnapshot(
                operation.Identity,
                operation.Fingerprint,
                operation.Kind,
                operation.StorageUnit,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                operation.CanonicalPayload))
            .OrderBy(operation => operation.Identity, StringComparer.Ordinal)
            .ToArray();
        var actual = SemanticOperations
            .Where(operation => operation.Kind == PhysicalSchemaOperationKind.ApplyProviderDefinition)
            .OrderBy(operation => operation.Identity, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "Applied provider physical-schema definitions do not match their semantic operation evidence.");
        }
    }

    internal static PhysicalSchemaAppliedSnapshot Deserialize(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        var routes = root.GetProperty("routes").EnumerateArray().Select(route =>
            new AppliedStorageRouteSnapshot(
                new StorageUnitIdentity(route.GetProperty("storageUnit").GetString()!),
                route.GetProperty("definitionFingerprint").GetString()!,
                route.GetProperty("routeFingerprint").GetString()!,
                route.GetProperty("resolvedNames").EnumerateArray().Select(name =>
                    new PhysicalSchemaResolvedName(
                        name.GetProperty("kind").GetString()!,
                        name.GetProperty("logicalName").GetString()!,
                        name.GetProperty("identifier").GetString()!,
                        Enum.Parse<ExecutableStorageObjectRole>(name.GetProperty("target").GetString()!)))
                    .ToArray(),
                route.GetProperty("canonicalRoute").GetString()!))
            .ToArray();
        var operations = root.GetProperty("semanticOperations").EnumerateArray().Select(operation =>
        {
            var storageUnit = operation.GetProperty("storageUnit");
            return new AppliedSemanticOperationSnapshot(
                operation.GetProperty("identity").GetString()!,
                operation.GetProperty("fingerprint").GetString()!,
                Enum.Parse<PhysicalSchemaOperationKind>(operation.GetProperty("kind").GetString()!),
                storageUnit.ValueKind == JsonValueKind.Null
                    ? null
                    : new StorageUnitIdentity(storageUnit.GetString()!),
                operation.GetProperty("subjectIdentity").GetString()!,
                operation.GetProperty("slotIdentity").GetString()!,
                operation.GetProperty("canonicalPayload").GetString()!);
        }).ToArray();
        var providerDefinitions = root.GetProperty("providerDefinitions").EnumerateArray().Select(definition =>
            new ProviderPhysicalSchemaDefinition(
                definition.GetProperty("providerName").GetString()!,
                new StorageUnitIdentity(definition.GetProperty("storageUnit").GetString()!),
                definition.GetProperty("kind").GetString()!,
                definition.GetProperty("subjectIdentity").GetString()!,
                definition.GetProperty("canonicalDefinition").GetString()!))
            .ToArray();
        var snapshot = new PhysicalSchemaAppliedSnapshot(routes, operations, providerDefinitions);
        if (!string.Equals(snapshot.CanonicalJson, canonicalJson, StringComparison.Ordinal))
            throw new InvalidOperationException("Applied physical schema snapshot is not in canonical form.");
        return snapshot;
    }

    private static string Serialize(PhysicalSchemaAppliedSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("routes");
            writer.WriteStartArray();
            foreach (var route in snapshot.Routes)
            {
                writer.WriteStartObject();
                writer.WriteString("storageUnit", route.StorageUnit.Value);
                writer.WriteString("definitionFingerprint", route.DefinitionFingerprint);
                writer.WriteString("routeFingerprint", route.RouteFingerprint);
                writer.WriteString("canonicalRoute", route.CanonicalRouteJson);
                writer.WritePropertyName("resolvedNames");
                writer.WriteStartArray();
                foreach (var name in route.ResolvedNames
                             .OrderBy(name => name.Target)
                             .ThenBy(name => name.Kind, StringComparer.Ordinal)
                             .ThenBy(name => name.LogicalName, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", name.Kind);
                    writer.WriteString("logicalName", name.LogicalName);
                    writer.WriteString("identifier", name.Identifier);
                    writer.WriteString("target", name.Target.ToString());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("providerDefinitions");
            writer.WriteStartArray();
            foreach (var definition in snapshot.ProviderDefinitions)
            {
                writer.WriteStartObject();
                writer.WriteString("providerName", definition.ProviderName);
                writer.WriteString("storageUnit", definition.StorageUnit.Value);
                writer.WriteString("kind", definition.Kind);
                writer.WriteString("subjectIdentity", definition.SubjectIdentity);
                writer.WriteString("canonicalDefinition", definition.CanonicalDefinition);
                writer.WriteString("fingerprint", definition.Fingerprint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("semanticOperations");
            writer.WriteStartArray();
            foreach (var operation in snapshot.SemanticOperations)
            {
                writer.WriteStartObject();
                writer.WriteString("identity", operation.Identity);
                writer.WriteString("fingerprint", operation.Fingerprint);
                writer.WriteString("kind", operation.Kind.ToString());
                if (operation.StorageUnit is null)
                    writer.WriteNull("storageUnit");
                else
                    writer.WriteString("storageUnit", operation.StorageUnit.Value);
                writer.WriteString("subjectIdentity", operation.SubjectIdentity);
                writer.WriteString("slotIdentity", operation.SlotIdentity);
                writer.WriteString("canonicalPayload", operation.CanonicalPayload);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed record PhysicalSchemaAppliedOperation(
    string Identity,
    string Fingerprint,
    PhysicalSchemaOperationKind Kind,
    StorageUnitIdentity? StorageUnit,
    string SubjectIdentity,
    string SlotIdentity,
    DateTimeOffset AppliedAt,
    string CanonicalPayload);

/// <summary>Durable evidence of one fully applied provider target.</summary>
public sealed class PhysicalSchemaAppliedState
{
    internal PhysicalSchemaAppliedState(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        DateTimeOffset appliedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IReadOnlyList<PhysicalSchemaAppliedOperation> appliedOperations)
        : this(
            target.ManifestIdentity,
            target.ManifestVersion,
            target.Provider,
            target.Fingerprint,
            plannedAt,
            appliedAt,
            snapshot,
            appliedOperations)
    {
    }

    internal PhysicalSchemaAppliedState(
        StorageManifestIdentity manifestIdentity,
        StorageManifestVersion manifestVersion,
        ProviderIdentity provider,
        string targetFingerprint,
        DateTimeOffset plannedAt,
        DateTimeOffset appliedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IReadOnlyList<PhysicalSchemaAppliedOperation> appliedOperations)
    {
        ManifestIdentity = manifestIdentity;
        ManifestVersion = manifestVersion;
        Provider = provider;
        TargetFingerprint = targetFingerprint;
        PlannedAt = plannedAt;
        AppliedAt = appliedAt;
        Snapshot = snapshot;
        AppliedOperations = Array.AsReadOnly(appliedOperations.ToArray());
        foreach (var operation in AppliedOperations)
            PhysicalSchemaOperationIntegrity.Validate(operation);

        var expectedTargetFingerprint = PhysicalSchemaFingerprint.Create(
            [
                manifestIdentity.Value,
                manifestVersion.Value,
                provider.Name,
                provider.Version,
                .. Snapshot.Routes.Select(route => route.RouteFingerprint),
                .. Snapshot.ProviderDefinitions.Select(definition => definition.Fingerprint)
            ]);
        if (expectedTargetFingerprint != targetFingerprint)
            throw new InvalidOperationException("Applied physical schema target fingerprint does not match its snapshot.");
    }

    public StorageManifestIdentity ManifestIdentity { get; }

    public StorageManifestVersion ManifestVersion { get; }

    public ProviderIdentity Provider { get; }

    public string TargetFingerprint { get; }

    public DateTimeOffset PlannedAt { get; }

    public DateTimeOffset AppliedAt { get; }

    public PhysicalSchemaAppliedSnapshot Snapshot { get; }

    public IReadOnlyList<PhysicalSchemaAppliedOperation> AppliedOperations { get; }
}

public sealed class PhysicalSchemaHistoryState
{
    private PhysicalSchemaHistoryState(PhysicalSchemaAppliedState? appliedState, bool hasLegacyHistory)
    {
        AppliedState = appliedState;
        HasLegacyHistory = hasLegacyHistory;
    }

    public static PhysicalSchemaHistoryState Empty { get; } = new(null, false);

    public static PhysicalSchemaHistoryState LegacyHistoryDetected { get; } = new(null, true);

    public PhysicalSchemaAppliedState? AppliedState { get; }

    public bool HasLegacyHistory { get; }

    public static PhysicalSchemaHistoryState FromApplied(PhysicalSchemaAppliedState appliedState) =>
        new(appliedState ?? throw new ArgumentNullException(nameof(appliedState)), false);
}

public enum LegacyPhysicalSchemaHistoryPolicy
{
    /// <summary>
    /// Groundwork is unreleased/greenfield: legacy rows without a typed applied snapshot are not
    /// guessed, adopted, or upgraded. Operators must remove them before applying a target.
    /// </summary>
    RejectEntriesWithoutAppliedSnapshot
}

public sealed record PhysicalSchemaOperationAcknowledgement(
    string Identity,
    string Fingerprint,
    DateTimeOffset AppliedAt);
