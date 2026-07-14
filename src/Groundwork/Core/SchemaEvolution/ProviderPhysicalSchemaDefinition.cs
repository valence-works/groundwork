using Groundwork.Core.Manifests;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// One provider-owned desired-state definition carried through the provider-neutral schema
/// coordinator. Core owns identity, fingerprinting, diffing, durable snapshots, and publication;
/// the named provider owns the canonical definition payload and its execution semantics.
/// </summary>
public sealed class ProviderPhysicalSchemaDefinition : IEquatable<ProviderPhysicalSchemaDefinition>
{
    public ProviderPhysicalSchemaDefinition(
        string providerName,
        StorageUnitIdentity storageUnit,
        string kind,
        string subjectIdentity,
        string canonicalDefinition)
    {
        ProviderName = string.IsNullOrWhiteSpace(providerName)
            ? throw new ArgumentException("A provider physical-schema definition requires a provider name.", nameof(providerName))
            : providerName;
        StorageUnit = storageUnit ?? throw new ArgumentNullException(nameof(storageUnit));
        Kind = string.IsNullOrWhiteSpace(kind)
            ? throw new ArgumentException("A provider physical-schema definition requires a kind.", nameof(kind))
            : kind;
        SubjectIdentity = string.IsNullOrWhiteSpace(subjectIdentity)
            ? throw new ArgumentException("A provider physical-schema definition requires a subject identity.", nameof(subjectIdentity))
            : subjectIdentity;
        CanonicalDefinition = string.IsNullOrWhiteSpace(canonicalDefinition)
            ? throw new ArgumentException("A provider physical-schema definition requires canonical content.", nameof(canonicalDefinition))
            : canonicalDefinition;
        Fingerprint = PhysicalSchemaFingerprint.Create(
            [ProviderName, StorageUnit.Value, Kind, SubjectIdentity, CanonicalDefinition]);
    }

    public string ProviderName { get; }

    public StorageUnitIdentity StorageUnit { get; }

    public string Kind { get; }

    public string SubjectIdentity { get; }

    public string CanonicalDefinition { get; }

    public string Fingerprint { get; }

    internal ProviderPhysicalSchemaDefinitionIdentity Identity => new(
        ProviderName,
        StorageUnit,
        Kind,
        SubjectIdentity);

    internal static ProviderPhysicalSchemaDefinition[] Canonicalize(
        IEnumerable<ProviderPhysicalSchemaDefinition>? definitions) =>
        (definitions ?? [])
        .OrderBy(definition => definition.ProviderName, StringComparer.Ordinal)
        .ThenBy(definition => definition.StorageUnit.Value, StringComparer.Ordinal)
        .ThenBy(definition => definition.Kind, StringComparer.Ordinal)
        .ThenBy(definition => definition.SubjectIdentity, StringComparer.Ordinal)
        .ToArray();

    public bool Equals(ProviderPhysicalSchemaDefinition? other) =>
        other is not null &&
        ProviderName == other.ProviderName &&
        StorageUnit == other.StorageUnit &&
        Kind == other.Kind &&
        SubjectIdentity == other.SubjectIdentity &&
        CanonicalDefinition == other.CanonicalDefinition &&
        Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as ProviderPhysicalSchemaDefinition);

    public override int GetHashCode() => HashCode.Combine(
        ProviderName,
        StorageUnit,
        Kind,
        SubjectIdentity,
        CanonicalDefinition,
        Fingerprint);
}

internal sealed record ProviderPhysicalSchemaDefinitionIdentity(
    string ProviderName,
    StorageUnitIdentity StorageUnit,
    string Kind,
    string SubjectIdentity);
