using Groundwork.Core.Capabilities;

namespace Groundwork.Core.Intents;

public sealed record StorageIntent
{
    public StorageIntent(
        IReadOnlySet<CapabilityId>? requirements,
        string? rationale = null,
        WorkloadIntent descriptor = WorkloadIntent.Unspecified)
        : this(requirements as IEnumerable<CapabilityId>, rationale, descriptor)
    {
    }

    private StorageIntent(
        IEnumerable<CapabilityId>? requirements,
        string? rationale,
        WorkloadIntent descriptor)
    {
        Requirements = NormalizeRequirements(requirements);
        Rationale = rationale;
        Descriptor = descriptor;
    }

    /// <summary>The capabilities this storage unit requires from a provider.</summary>
    public IReadOnlySet<CapabilityId> Requirements { get; }

    public string? Rationale { get; }

    /// <summary>
    /// Non-binding, human-readable workload label for diagnostics and documentation only.
    /// Provider fit is derived from <see cref="Requirements"/>, never from this descriptor.
    /// </summary>
    public WorkloadIntent Descriptor { get; }

    public static StorageIntent PortableDocument(
        WorkloadIntent descriptor = WorkloadIntent.RuntimeDefinedBusinessData) =>
        new(Array.Empty<CapabilityId>(), null, descriptor);

    public static StorageIntent Operational(
        string rationale,
        WorkloadIntent descriptor,
        params CapabilityId[]? requirements) =>
        new(requirements, rationale, descriptor);

    public bool Equals(StorageIntent? other) =>
        other is not null &&
        Descriptor == other.Descriptor &&
        string.Equals(Rationale, other.Rationale, StringComparison.Ordinal) &&
        Requirements.SetEquals(other.Requirements);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Descriptor);
        hashCode.Add(Rationale, StringComparer.Ordinal);

        foreach (var requirement in Requirements.OrderBy(requirement => requirement.Value, StringComparer.Ordinal))
            hashCode.Add(requirement);

        return hashCode.ToHashCode();
    }

    private static IReadOnlySet<CapabilityId> NormalizeRequirements(IEnumerable<CapabilityId>? requirements) =>
        requirements?.ToHashSet() ?? new HashSet<CapabilityId>();
}

/// <summary>
/// Non-binding soft label describing the intended shape of a storage unit. Used only for
/// diagnostics and human readability; it never participates in provider-fit computation.
/// </summary>
public enum WorkloadIntent
{
    Unspecified,
    MetadataConfiguration,
    CatalogAuthoredData,
    RuntimeDefinedBusinessData,
    RuntimeContinuationState,
    OperationalStream,
    Projection,
    AuditTrail
}
