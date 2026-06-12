namespace Groundwork.Core.Intents;

public sealed record StorageIntent
{
    public StorageIntent(
        StorageIntentKind kind,
        IReadOnlySet<StorageRequirement>? requirements,
        string? rationale)
    {
        Kind = kind;
        Requirements = NormalizeRequirements(requirements);
        Rationale = rationale;
    }

    public StorageIntentKind Kind { get; }

    public IReadOnlySet<StorageRequirement> Requirements { get; }

    public string? Rationale { get; }

    public static StorageIntent PortableDocument() =>
        new(StorageIntentKind.PortableDocument, new HashSet<StorageRequirement>(), null);

    public static StorageIntent BenchmarkGated(string rationale, params StorageRequirement[]? requirements) =>
        new(StorageIntentKind.BenchmarkGated, NormalizeRequirements(requirements), rationale);

    public static StorageIntent SpecializedProvider(string rationale, params StorageRequirement[]? requirements) =>
        new(StorageIntentKind.SpecializedProvider, NormalizeRequirements(requirements), rationale);

    public bool Equals(StorageIntent? other) =>
        other is not null &&
        Kind == other.Kind &&
        string.Equals(Rationale, other.Rationale, StringComparison.Ordinal) &&
        Requirements.SetEquals(other.Requirements);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Kind);
        hashCode.Add(Rationale, StringComparer.Ordinal);

        foreach (var requirement in Requirements.Order())
            hashCode.Add(requirement);

        return hashCode.ToHashCode();
    }

    private static IReadOnlySet<StorageRequirement> NormalizeRequirements(IEnumerable<StorageRequirement>? requirements) =>
        requirements?.ToHashSet() ?? new HashSet<StorageRequirement>();
}

public enum StorageIntentKind
{
    PortableDocument,
    BenchmarkGated,
    SpecializedProvider
}

public enum StorageRequirement
{
    AtomicClaim,
    LeaseRecovery,
    OrderedConsumption,
    RetryRecovery,
    Idempotency,
    RetentionPolicy,
    AtomicCommit,
    ConcurrencyEvidence,
    OperationalDiagnostics
}
